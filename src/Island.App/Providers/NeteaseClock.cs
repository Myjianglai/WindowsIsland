using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Island.App.Providers;

internal sealed partial class NeteaseClock
{
    private const int TailLimit = 3 * 1024 * 1024;
    private readonly string _path;
    private long _offset = -1;
    private DateTime _created;
    private string _title = "";
    private string _id = "";
    private double _duration;
    private double _position;
    private long _stamp;
    private bool _playing;
    private bool _resumeAfterBuffer;
    private bool _hasPosition;

    public NeteaseClock() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetEase", "CloudMusic", "cloudmusic.elog")) { }

    internal NeteaseClock(string path) => _path = path;

    internal readonly record struct Playback(TimeSpan Position, TimeSpan Duration, bool Playing, string Id, bool HasPosition);

    public Playback? Read(string title, bool? playing = null)
    {
        try
        {
            using var file = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var created = File.GetCreationTimeUtc(_path);
            long size = file.Length;
            long from = Math.Max(0, size - TailLimit);
            bool replay = _offset < from || _offset > size || _created != created;
            if (replay)
            {
                _title = _id = "";
                _stamp = 0;
                _position = _duration = 0;
                _playing = _resumeAfterBuffer = _hasPosition = false;
                _created = created;
            }
            file.Position = replay ? from : _offset;
            int count = (int)(size - file.Position);
            if (count > 0)
            {
                var data = new byte[count];
                int read = file.ReadAtLeast(data, count, throwOnEndOfStream: false);
                for (int i = 0; i < read; i++)
                {
                    int value = data[i], upper = value >> 4;
                    data[i] = (byte)(((upper ^ ((value + 8) & 15)) << 4) | ((value >> 6) << 2) | (~upper & 3));
                }
                int start = replay && from > 0 ? Array.IndexOf(data, (byte)'\n', 0, read) + 1 : 0;
                for (int i = start; i < read; i++)
                {
                    if (data[i] != '\n') continue;
                    Apply(data.AsSpan(start, i - start));
                    start = i + 1;
                }
                _offset = (replay ? from : _offset) + start;
            }
            var now = Environment.TickCount64;
            if (_stamp <= 0 || _stamp > now || _duration <= 0 || _title != LocalLyrics.Normalize(title)) return null;
            bool running = playing ?? _playing;
            double position = Math.Max(0, _position + (running ? (now - _stamp) / 1000d : 0));
            return new Playback(TimeSpan.FromSeconds(Math.Min(_duration, position)),
                TimeSpan.FromSeconds(_duration), running, _id, _hasPosition);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Apply(ReadOnlySpan<byte> line)
    {
        int marker = line.IndexOf("playing"u8);
        if (marker < 0) return;
        int quote = line[marker..].IndexOf((byte)'"');
        if (quote < 0 || quote > 16) return;
        var timestamp = Header().Match(Encoding.ASCII.GetString(line[..Math.Min(marker, 90)]));
        if (!timestamp.Success || !long.TryParse(timestamp.Groups[1].Value, out long tick)
            || tick < _stamp || tick > Environment.TickCount64) return;
        var fields = line[(marker + quote + 1)..];
        int end = fields.IndexOf((byte)'"');
        if (end < 0) return;
        string kind = Encoding.UTF8.GetString(fields[..end]);
        var arguments = Encoding.UTF8.GetString(fields[(end + 1)..]).Trim();
        if (!arguments.StartsWith(',')) return;
        arguments = arguments[1..].Trim();
        if (kind is not ("playOneTrackInPlayingList" or "setPlaying" or "nativePlay" or "refreshCurPlaying"
            or "pause" or "resume" or "setPlayingPosition" or "loadingSeek" or "PlayerBuffering" or "PlayerBufferingEnd")
            && !kind.StartsWith("native", StringComparison.Ordinal)) return;
        try
        {
            using var data = JsonDocument.Parse("[" + arguments + "]");
            var args = data.RootElement;
            if (args.GetArrayLength() == 0) return;
            var first = args[0];
            var entry = kind == "refreshCurPlaying" && args.GetArrayLength() > 1 ? args[1] : first;
            if (entry.ValueKind == JsonValueKind.Object &&
                kind is ("playOneTrackInPlayingList" or "setPlaying" or "nativePlay" or "refreshCurPlaying"))
            {
                if (!entry.TryGetProperty("track", out var track)
                    || track.ValueKind != JsonValueKind.Object || !track.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String || !track.TryGetProperty("id", out var identifier)
                    || !track.TryGetProperty("duration", out var length) || length.ValueKind != JsonValueKind.Number
                    || !length.TryGetDouble(out double milliseconds) || milliseconds is <= 0 or > 604800000) return;
                string id = identifier.ToString();
                if (id.Length == 0 || (id == _id && (kind == "refreshCurPlaying" || _hasPosition && kind != "playOneTrackInPlayingList"))) return;
                _id = id;
                _title = LocalLyrics.Normalize(name.GetString() ?? "");
                _duration = milliseconds / 1000;
                _resumeAfterBuffer = true;
                _hasPosition = kind != "refreshCurPlaying";
                Update(tick, 0, false);
            }
            else if (_id.Length > 0 && kind is "setPlayingPosition" or "loadingSeek")
            {
                if (first.ValueKind == JsonValueKind.Number && first.TryGetDouble(out double seconds)
                    && seconds >= 0 && seconds <= _duration + 3)
                {
                    _hasPosition = true;
                    Update(tick, seconds, _playing);
                }
            }
            else if (_id.Length > 0)
            {
                var owner = first.ValueKind == JsonValueKind.String ? first.GetString()
                    : args.GetArrayLength() > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null;
                if (owner is null || !string.Equals(owner.Split('_', 2)[0], _id, StringComparison.Ordinal)) return;
                var playing = kind switch
                {
                    "resume" => true,
                    "pause" => false,
                    "PlayerBuffering" => false,
                    "PlayerBufferingEnd" => _resumeAfterBuffer,
                    _ when kind.StartsWith("native", StringComparison.Ordinal)
                        && first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out int state) && state is 1 or 2 => state == 1,
                    _ => _playing
                };
                if (kind is "resume" or "pause" || kind.StartsWith("native", StringComparison.Ordinal)) _resumeAfterBuffer = playing;
                else if (kind == "PlayerBuffering" && _playing) _resumeAfterBuffer = true;
                Update(tick, PositionAt(tick), playing);
            }
        }
        catch (JsonException) { }
    }

    private double PositionAt(long tick) => Math.Max(0, _position + (_playing ? (tick - _stamp) / 1000d : 0));

    private void Update(long tick, double seconds, bool playing)
    {
        _stamp = tick;
        _position = seconds;
        _playing = playing;
    }

    [GeneratedRegex(@"^\[\d+:\d+:\d{4}/\d{6}:(\d+):", RegexOptions.CultureInvariant)]
    private static partial Regex Header();
}
