using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;

namespace Island.App.Providers;

internal sealed class NeteaseLyrics
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private readonly ConcurrentDictionary<string, IReadOnlyList<LyricLine>> _cache = new();

    public Task<IReadOnlyList<LyricLine>> FindAsync(
        string title, string artist, string? metadata, TimeSpan duration, CancellationToken token)
        => FindCoreAsync(title, artist, metadata, duration, true, token);

    public Task<IReadOnlyList<LyricLine>> FindByTrackAsync(string title, string artist, TimeSpan duration, CancellationToken token)
        => FindCoreAsync(title, artist, null, duration, false, token);

    private async Task<IReadOnlyList<LyricLine>> FindCoreAsync(
        string title, string artist, string? metadata, TimeSpan duration, bool useLocalQueue, CancellationToken token)
    {
        var metadataId = LocalLyrics.GetNeteaseId(metadata);
        if (metadataId is null && (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))) return [];
        var cacheKey = $"{title}\n{artist}\n{metadata}\n{Math.Round(duration.TotalSeconds)}";
        if (_cache.TryGetValue(cacheKey, out var known)) return known;
        try
        {
            string? id = metadataId ?? (useLocalQueue ? ReadQueueId(title, artist, duration) : null);
            if (id is null)
            {
                var lookup = await GetAsync("api/search/get/web?type=1&limit=8&s=" + Uri.EscapeDataString(title + " " + artist), token);
                if (lookup is { } search && TryGet(search, "result", out var result) && TryGet(result, "songs", out var songs))
                    id = MatchId(songs, title, artist, duration);
            }
            if (id is null) return [];
            var response = await GetAsync($"api/song/lyric?id={id}&lv=-1&kv=-1&tv=-1", token);
            if (response is not { } payload || !TryGet(payload, "lrc", out var lrc)) return [];
            var lines = LocalLyrics.Parse(ReadString(lrc, "lyric"));
            if (lines.Count > 0)
            {
                if (_cache.Count >= 80) _cache.Clear();
                _cache.TryAdd(cacheKey, lines);
            }
            return lines;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or ArgumentException)
        {
            return [];
        }
    }

    internal static string? ReadQueueId(string title, string artist, TimeSpan duration)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase", "CloudMusic", "webdata", "file");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (fileName, property) in new[] { ("playingList", "list"), ("fmPlay", "queue") })
        {
            try
            {
                using var stream = File.Open(Path.Combine(root, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) continue;
                using var doc = JsonDocument.Parse(stream);
                if (TryGet(doc.RootElement, property, out var collection)
                    && MatchId(collection, title, artist, duration) is { } id)
                    ids.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return ids.Count == 1 ? ids.First() : null;
    }

    internal static string? MatchId(JsonElement collection, string title, string artist, TimeSpan duration)
    {
        if (collection.ValueKind != JsonValueKind.Array) return null;
        var expectedName = LocalLyrics.Normalize(title);
        var expectedArtists = LocalLyrics.NormalizeArtists(artist);
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in collection.EnumerateArray())
        {
            var track = TryGet(item, "track", out var nested) ? nested : item;
            if (LocalLyrics.Normalize(ReadString(track, "name")) != expectedName
                || !TryGet(track, "artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
                continue;
            var names = string.Join("/", artists.EnumerateArray().Select(artistInfo => ReadString(artistInfo, "name")));
            if (LocalLyrics.NormalizeArtists(names) != expectedArtists) continue;
            if (duration > TimeSpan.Zero && TryGet(track, "duration", out var length)
                && length.ValueKind == JsonValueKind.Number && length.TryGetDouble(out double milliseconds)
                && Math.Abs(duration.TotalMilliseconds - milliseconds) > 2500) continue;
            if (TryGet(track, "id", out var songId))
            {
                var id = songId.ToString();
                if (id.Length is > 0 and <= 20 && id.All(char.IsAsciiDigit)) matches.Add(id);
            }
        }
        return matches.Count == 1 ? matches.First() : null;
    }

    private static async Task<JsonElement?> GetAsync(string route, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.163.com/" + route);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
        await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (data.Length + read > MaxBytes) return null;
            data.Write(buffer, 0, read);
        }
        data.Position = 0;
        using var json = JsonDocument.Parse(data);
        return json.RootElement.Clone();
    }

    private static bool TryGet(JsonElement value, string property, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out result);
    }

    private static string ReadString(JsonElement value, string property) =>
        TryGet(value, property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
