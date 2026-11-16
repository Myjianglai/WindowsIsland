using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Island.Core.Events;
using Island.Core.Providers;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace Island.App.Providers;

internal sealed class MusicProvider : IIslandProvider
{
    public const string SourceId = "music";
    private sealed record LyricsOptions(string Folder, bool Online);
    private LyricsOptions _options = new("", false);
    private readonly NeteaseLyrics _neteaseLyrics = new();
    private event Action? LyricsFolderChanged;
    public string Id => SourceId;

    public void SetLyricsOptions(string path, bool online)
    {
        var next = new LyricsOptions(path, online);
        if (Interlocked.Exchange(ref _options, next) != next)
        {
            LyricsFolderChanged?.Invoke();
        }
    }

    public async Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken)
    {
        var manager = await Manager.RequestAsync().AsTask(cancellationToken);
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
        });
        int readMetadata = 1;
        int watchNeteaseClock = 0;
        void Signal() => changes.Writer.TryWrite(true);
        using var clockStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task WakeClockAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
            while (await timer.WaitForNextTickAsync(clockStop.Token))
                if (Volatile.Read(ref watchNeteaseClock) != 0) Signal();
        }
        var clockWorker = WakeClockAsync();
        NeteaseClock? neteaseClock = null;
        void SignalMetadata()
        {
            Interlocked.Exchange(ref readMetadata, 1);
            Signal();
        }
        void OnCurrentChanged(Manager _, CurrentSessionChangedEventArgs args) => Signal();
        void OnSessionsChanged(Manager _, SessionsChangedEventArgs args) => Signal();
        void OnMediaChanged(Session _, MediaPropertiesChangedEventArgs args) => SignalMetadata();
        void OnPlaybackChanged(Session _, PlaybackInfoChangedEventArgs args) => Signal();
        void OnTimelineChanged(Session _, TimelinePropertiesChangedEventArgs args) => Signal();

        Session? session = null;
        void Unsubscribe()
        {
            if (session is not null)
            {
                session.MediaPropertiesChanged -= OnMediaChanged;
                session.PlaybackInfoChanged -= OnPlaybackChanged;
                session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
        }
        string title = "";
        string artist = "";
        ReadOnlyMemory<byte> artwork = default;
        IReadOnlyList<LyricLine> lyrics = [];
        string? player = null;
        string? subtitle = null;
        string? requestKey = null;
        CancellationTokenSource? lyricCancellation = null;
        Task<IReadOnlyList<LyricLine>>? pendingLyrics = null;
        void CancelLyrics()
        {
            lyricCancellation?.Cancel();
            lyricCancellation?.Dispose();
            lyricCancellation = null;
            pendingLyrics = null;
            requestKey = null;
        }
        try
        {
            LyricsFolderChanged += SignalMetadata;
            manager.CurrentSessionChanged += OnCurrentChanged;
            manager.SessionsChanged += OnSessionsChanged;
            Signal();
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            {
                var current = manager.GetCurrentSession();
                if (current is null || current.GetPlaybackInfo()?.PlaybackStatus != PlaybackStatus.Playing)
                {
                    current = manager.GetSessions().FirstOrDefault(candidate =>
                        IdentifyPlayer(candidate.SourceAppUserModelId) is not null
                        && candidate.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing) ?? current;
                }
                if (!Equals(current, session))
                {
                    Unsubscribe();
                    CancelLyrics();
                    session = current;
                    Interlocked.Exchange(ref readMetadata, 1);
                    if (session is not null)
                    {
                        session.MediaPropertiesChanged += OnMediaChanged;
                        session.PlaybackInfoChanged += OnPlaybackChanged;
                        session.TimelinePropertiesChanged += OnTimelineChanged;
                    }
                }
                if (session is null)
                {
                    Volatile.Write(ref watchNeteaseClock, 0);
                    neteaseClock = null;
                    await publisher.PublishAsync(Status("暂无播放会话"), cancellationToken);
                    continue;
                }
                if (Interlocked.Exchange(ref readMetadata, 0) == 1)
                {
                    var metadata = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
                    title = string.IsNullOrWhiteSpace(metadata?.Title) ? "未知曲目" : metadata.Title;
                    artist = metadata?.Artist ?? "";
                    artwork = await LoadArtworkAsync(metadata?.Thumbnail, cancellationToken);
                    player = IdentifyPlayer(session.SourceAppUserModelId);
                    subtitle = metadata?.Subtitle;
                    lyrics = LocalLyrics.Find(Volatile.Read(ref _options).Folder, title, artist, subtitle, player);
                }
                var capability = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;
                bool useLocalClock = player == "网易云音乐" &&
                    (duration <= TimeSpan.Zero || timeline.LastUpdatedTime <= DateTimeOffset.UnixEpoch);
                Volatile.Write(ref watchNeteaseClock, useLocalClock ? 1 : 0);
                var localClock = useLocalClock ? (neteaseClock ??= new NeteaseClock()).Read(title) : null;
                if (localClock is { } local)
                {
                    duration = local.Duration;
                    if (local.Id.Length is > 0 and <= 20 && local.Id.All(char.IsAsciiDigit))
                        subtitle = $"orpheus://song/{local.Id}";
                }
                if (Volatile.Read(ref _options).Online && player == "网易云音乐" && lyrics.Count == 0)
                {
                    var key = $"{title}\n{artist}\n{subtitle}\n{Math.Round(duration.TotalSeconds)}";
                    if (requestKey != key)
                    {
                        CancelLyrics();
                        requestKey = key;
                        lyricCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        pendingLyrics = _neteaseLyrics.FindAsync(title, artist, subtitle, duration, lyricCancellation.Token);
                        _ = pendingLyrics.ContinueWith(_ => Signal(), TaskScheduler.Default);
                    }
                }
                else if (pendingLyrics is not null)
                {
                    CancelLyrics();
                }
                var activeLyrics = pendingLyrics is { IsCompletedSuccessfully: true } lookup && lyrics.Count == 0
                    ? lookup.Result : lyrics;
                bool playing = localClock?.Playing ?? capability.PlaybackStatus == PlaybackStatus.Playing;
                var rate = capability.PlaybackRate ?? 1;
                var active = session;
                var snapshot = new MediaSnapshot(artwork, localClock?.Position ?? timeline.Position - timeline.StartTime,
                    duration, useLocalClock || timeline.LastUpdatedTime <= DateTimeOffset.UnixEpoch
                        ? DateTimeOffset.UtcNow : timeline.LastUpdatedTime, playing,
                    double.IsFinite(rate) ? rate : 1,
                    capability.Controls.IsPlaybackPositionEnabled && timeline.EndTime > timeline.StartTime
                        ? (position, token) => SeekAsync(active, position, token, cancellationToken) : null,
                    activeLyrics, player, !useLocalClock || localClock.HasValue);
                await publisher.PublishAsync(new IslandEvent(Id, IslandEventType.Media, title, artist, "music",
                    BuildActions(session, capability.Controls, playing, cancellationToken), media: snapshot),
                    cancellationToken);
            }
        }
        finally
        {
            clockStop.Cancel();
            try { await clockWorker; }
            catch (OperationCanceledException) when (clockStop.IsCancellationRequested) { }
            CancelLyrics();
            LyricsFolderChanged -= SignalMetadata;
            manager.CurrentSessionChanged -= OnCurrentChanged;
            manager.SessionsChanged -= OnSessionsChanged;
            Unsubscribe();
        }
    }

    public static IslandEvent Status(string title) => new(SourceId, IslandEventType.Media, title);

    internal static string? IdentifyPlayer(string source)
    {
        var name = source.ToLowerInvariant();
        if (name.Contains("cloudmusic") || name.Contains("netease") || name.Contains("网易云")) return "网易云音乐";
        if (name.Contains("qqmusic") || name.Contains("qq音乐")) return "QQ 音乐";
        if (name.Contains("kugou") || name.Contains("酷狗")) return "酷狗音乐";
        if (name.Contains("qishui") || name.Contains("luna") || name.Contains("汽水")) return "汽水音乐";
        return null;
    }

    private static async ValueTask SeekAsync(Session session, TimeSpan position, CancellationToken token, CancellationToken lifetime)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        canceled.Token.ThrowIfCancellationRequested();
        var timeline = session.GetTimelineProperties();
        var min = timeline.MinSeekTime;
        var max = timeline.MaxSeekTime;
        if (max <= min)
        {
            min = timeline.StartTime;
            max = timeline.EndTime;
        }
        if (max <= min || !await session.TryChangePlaybackPositionAsync(
            Math.Clamp((timeline.StartTime + position).Ticks, min.Ticks, max.Ticks)).AsTask(canceled.Token))
        {
            throw new InvalidOperationException("播放器未接受进度设置。");
        }
    }

    private static IEnumerable<IslandAction> BuildActions(Session session,
        GlobalSystemMediaTransportControlsSessionPlaybackControls controls, bool playing, CancellationToken lifetime)
    {
        IslandAction Action(string title, Func<IAsyncOperation<bool>> command) => new(title, async token =>
        {
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            canceled.Token.ThrowIfCancellationRequested();
            if (!await command().AsTask(canceled.Token))
            {
                throw new InvalidOperationException("播放器未接受命令。");
            }
        });

        if (controls.IsPreviousEnabled)
        {
            yield return Action("上一首", session.TrySkipPreviousAsync);
        }
        if (playing && controls.IsPauseEnabled)
        {
            yield return Action("暂停", session.TryPauseAsync);
        }
        else if (!playing && controls.IsPlayEnabled)
        {
            yield return Action("播放", session.TryPlayAsync);
        }
        else if (controls.IsPlayPauseToggleEnabled)
        {
            yield return Action(playing ? "暂停" : "播放", session.TryTogglePlayPauseAsync);
        }
        if (controls.IsNextEnabled)
        {
            yield return Action("下一首", session.TrySkipNextAsync);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> LoadArtworkAsync(IRandomAccessStreamReference? reference,
        CancellationToken cancellationToken)
    {
        if (reference is null)
        {
            return default;
        }
        try
        {
            using var source = await reference.OpenReadAsync().AsTask(cancellationToken);
            // 封面只留在内存，限制单次读取大小。
            if (source.Size is 0 or > 4 * 1024 * 1024)
            {
                return default;
            }
            var bytes = new byte[(int)source.Size];
            using var stream = source.AsStreamForRead();
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException)
        {
            return default;
        }
    }
}
