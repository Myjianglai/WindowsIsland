using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private sealed record LyricsOptions(string Folder, bool NeteaseOnline, bool GeneralOnline);
    private LyricsOptions _options = new("", false, false);
    private readonly NeteaseLyrics _neteaseLyrics = new();
    private readonly LrcLibLyrics _lrcLibLyrics = new();
    private event Action? LyricsFolderChanged;
    public string Id => SourceId;

    public void SetLyricsOptions(string path, bool neteaseOnline, bool generalOnline)
    {
        var next = new LyricsOptions(path, neteaseOnline, generalOnline);
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
        int pollPlaybackOrLyrics = 0;
        long skipRecoveryUntil = 0;
        long? unavailableSince = null;
        bool recoveringRead = false;
        void Signal() => changes.Writer.TryWrite(true);
        using var clockStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task WakeClockAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(400));
            while (await timer.WaitForNextTickAsync(clockStop.Token))
            {
                if (Environment.TickCount64 < Interlocked.Read(ref skipRecoveryUntil))
                    SignalMetadata();
                else if (Volatile.Read(ref pollPlaybackOrLyrics) != 0)
                    Signal();
            }
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
        string album = "";
        ReadOnlyMemory<byte> artwork = default;
        IReadOnlyList<LyricLine> lyrics = [];
        string? localLyricsKey = null;
        string? player = null;
        string? subtitle = null;
        string? requestKey = null;
        CancellationTokenSource? lyricCancellation = null;
        Task<IReadOnlyList<LyricLine>>? pendingLyrics = null;
        int lookupAttempts = 0;
        long lookupStarted = 0;
        void CancelLyrics()
        {
            lyricCancellation?.Cancel();
            lyricCancellation?.Dispose();
            lyricCancellation = null;
            pendingLyrics = null;
            requestKey = null;
            lookupAttempts = 0;
        }
        bool WaitForMedia()
        {
            var now = Environment.TickCount64;
            unavailableSince ??= now;
            if (now >= Interlocked.Read(ref skipRecoveryUntil) && now - unavailableSince.Value >= 2200) return false;
            Volatile.Write(ref pollPlaybackOrLyrics, 1);
            return true;
        }

        void BeginSkipRecovery()
        {
            Interlocked.Exchange(ref skipRecoveryUntil, Environment.TickCount64 + 4200);
            SignalMetadata();
        }

        try
        {
            LyricsFolderChanged += SignalMetadata;
            manager.CurrentSessionChanged += OnCurrentChanged;
            manager.SessionsChanged += OnSessionsChanged;
            Signal();
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var current = manager.GetCurrentSession();
                    if (current is null || current.GetPlaybackInfo()?.PlaybackStatus != PlaybackStatus.Playing)
                    {
                        current = manager.GetSessions()
                            .Where(candidate => candidate.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
                            .OrderByDescending(candidate => IdentifyPlayer(candidate.SourceAppUserModelId) is not null)
                            .FirstOrDefault() ?? current;
                    }
                    if (current is null && (session is not null || unavailableSince.HasValue) && WaitForMedia())
                        continue;
                    if (current is not null && Environment.TickCount64 < Interlocked.Read(ref skipRecoveryUntil) && session is not null)
                    {
                        // 切歌间隙 Windows 可能暂时把系统首选会话切到另一个应用。
                        current = manager.GetSessions().FirstOrDefault(candidate =>
                            candidate.SourceAppUserModelId == session.SourceAppUserModelId) ?? current;
                    }
                    if (!Equals(current, session))
                    {
                        Unsubscribe();
                        CancelLyrics();
                        session = current;
                        artwork = default;
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
                        Volatile.Write(ref pollPlaybackOrLyrics, 0);
                        neteaseClock = null;
                        unavailableSince = null;
                        await publisher.PublishAsync(Status("暂无播放会话"), cancellationToken);
                        continue;
                    }
                    if (Interlocked.Exchange(ref readMetadata, 0) == 1)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(2));
                        GlobalSystemMediaTransportControlsSessionMediaProperties? metadata = null;
                        try { metadata = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                        if (string.IsNullOrWhiteSpace(metadata?.Title) && WaitForMedia())
                        {
                            Interlocked.Exchange(ref readMetadata, 1);
                            continue;
                        }
                        var nextTitle = string.IsNullOrWhiteSpace(metadata?.Title) ? "未知曲目" : metadata.Title;
                        var nextArtist = metadata?.Artist ?? "";
                        var nextAlbum = metadata?.AlbumTitle ?? "";
                        if (artwork.IsEmpty || nextTitle != title || nextArtist != artist || nextAlbum != album)
                        {
                            try { artwork = await LoadArtworkAsync(metadata?.Thumbnail, deadline.Token); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { artwork = default; }
                        }
                        title = nextTitle;
                        artist = nextArtist;
                        album = nextAlbum;
                        player = IdentifyPlayer(session.SourceAppUserModelId);
                        subtitle = metadata?.Subtitle;
                    }
                    var capability = session.GetPlaybackInfo();
                    if (capability?.Controls is null)
                    {
                        Volatile.Write(ref pollPlaybackOrLyrics, 1);
                        Interlocked.Exchange(ref readMetadata, 1);
                        continue;
                    }
                    unavailableSince = null;
                    recoveringRead = false;
                    var timeline = session.GetTimelineProperties();
                    var duration = timeline.EndTime - timeline.StartTime;
                    var position = timeline.Position - timeline.StartTime;
                    bool netease = player == "网易云音乐";
                    bool hasTimeline = (!netease || timeline.LastUpdatedTime > DateTimeOffset.UnixEpoch) &&
                        (duration > TimeSpan.Zero || position > TimeSpan.Zero);
                    bool? playingHint = capability.PlaybackStatus switch
                    {
                        PlaybackStatus.Playing => true, PlaybackStatus.Paused or PlaybackStatus.Stopped => false, _ => null
                    };
                    var localClock = netease ? (neteaseClock ??= new NeteaseClock()).Read(title, playingHint) : null;
                    if (netease && playingHint == true && position <= TimeSpan.Zero &&
                        DateTimeOffset.UtcNow - timeline.LastUpdatedTime > TimeSpan.FromSeconds(10))
                        hasTimeline = false;
                    bool useLocalClock = localClock is { HasPosition: true } && (!hasTimeline || position <= TimeSpan.Zero);
                    string? trackMetadata = subtitle;
                    if (localClock is { } local)
                    {
                        if (useLocalClock || duration <= TimeSpan.Zero) duration = local.Duration;
                        if (LocalLyrics.GetNeteaseId(subtitle) is null && local.Id.Length is > 0 and <= 20 && local.Id.All(char.IsAsciiDigit))
                            trackMetadata = $"orpheus://song/{local.Id}";
                    }
                    var options = Volatile.Read(ref _options);
                    var localKey = $"{options.Folder}\n{title}\n{artist}\n{trackMetadata}";
                    if (localLyricsKey != localKey)
                    {
                        lyrics = LocalLyrics.Find(options.Folder, title, artist, trackMetadata, player);
                        localLyricsKey = localKey;
                    }
                    bool retryLookup = false;
                    if ((options.GeneralOnline || options.NeteaseOnline && netease) && lyrics.Count == 0)
                    {
                        var id = LocalLyrics.GetNeteaseId(trackMetadata);
                        var trackKey = id is null ? $"{title}\n{artist}\n{album}\n{Math.Round(duration.TotalSeconds)}" : $"id:{id}";
                        var key = $"{options.NeteaseOnline}:{options.GeneralOnline}:{trackKey}";
                        if (requestKey != key)
                        {
                            CancelLyrics();
                            requestKey = key;
                        }
                        retryLookup = pendingLyrics is { IsCompletedSuccessfully: true } previous &&
                            previous.Result.Count == 0 && lookupAttempts < 3;
                        if (pendingLyrics is null || (retryLookup && Environment.TickCount64 - lookupStarted >= 15000))
                        {
                            lyricCancellation?.Dispose();
                            lyricCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            lookupStarted = Environment.TickCount64;
                            lookupAttempts++;
                            pendingLyrics = FindOnlineLyricsAsync(options, player, title, artist, album, trackMetadata, duration, lyricCancellation.Token);
                            _ = pendingLyrics.ContinueWith(_ => Signal(), TaskScheduler.Default);
                        }
                    }
                    else if (pendingLyrics is not null)
                    {
                        CancelLyrics();
                    }
                    Volatile.Write(ref pollPlaybackOrLyrics, retryLookup || netease &&
                        (!hasTimeline || useLocalClock || playingHint == true && position <= TimeSpan.Zero) ? 1 : 0);
                    var activeLyrics = pendingLyrics is { IsCompletedSuccessfully: true } lookup && lyrics.Count == 0
                        ? lookup.Result : lyrics;
                    bool playing = capability.PlaybackStatus == PlaybackStatus.Playing ||
                        (capability.PlaybackStatus is PlaybackStatus.Closed or PlaybackStatus.Opened or PlaybackStatus.Changing
                            && useLocalClock && localClock!.Value.Playing);
                    var rate = capability.PlaybackRate ?? 1;
                    var active = session;
                    var snapshot = new MediaSnapshot(artwork, useLocalClock ? localClock!.Value.Position : position,
                        duration, useLocalClock || timeline.LastUpdatedTime <= DateTimeOffset.UnixEpoch
                            ? DateTimeOffset.UtcNow : timeline.LastUpdatedTime, playing,
                        double.IsFinite(rate) ? rate : 1,
                        capability.Controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero
                            ? (position, token) => SeekAsync(active, position, duration, token, cancellationToken)
                            : netease && (hasTimeline || useLocalClock) && duration > TimeSpan.Zero
                                ? (position, token) => NeteaseSeek.SetAsync(position, duration, token) : null,
                        activeLyrics, player, hasTimeline || useLocalClock);
                    await publisher.PublishAsync(new IslandEvent(Id, IslandEventType.Media, title, artist, "music",
                        BuildActions(session, capability.Controls, playing, cancellationToken, BeginSkipRecovery), media: snapshot),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException ||
                    ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // 会话交接时 COM 对象可能短暂失效。保留现有画面并重新读取会话。
                    if (!recoveringRead)
                    {
                        recoveringRead = true;
                        unavailableSince = Environment.TickCount64;
                    }
                    Interlocked.Exchange(ref readMetadata, 1);
                    Volatile.Write(ref pollPlaybackOrLyrics, 1);
                }
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

    private async Task<IReadOnlyList<LyricLine>> FindOnlineLyricsAsync(LyricsOptions options, string? player,
        string title, string artist, string album, string? metadata, TimeSpan duration, CancellationToken token)
    {
        if (player == "网易云音乐")
        {
            var native = await _neteaseLyrics.FindAsync(title, artist, metadata, duration, token);
            if (native.Count > 0 || !options.GeneralOnline) return native;
        }
        else if (player is "QQ 音乐" or "酷狗音乐")
        {
            var native = await PlayerLyrics.FindAsync(player, title, artist, duration, token);
            if (native.Count > 0) return native;
        }
        token.ThrowIfCancellationRequested();
        var synced = await _lrcLibLyrics.FindAsync(title, artist, album, duration, token);
        if (synced.Count > 0 || player == "网易云音乐") return synced;
        token.ThrowIfCancellationRequested();
        return await _neteaseLyrics.FindByTrackAsync(title, artist, duration, token);
    }

    internal static string? IdentifyPlayer(string source)
    {
        var name = source.ToLowerInvariant();
        if (name.Contains("cloudmusic") || name.Contains("netease") || name.Contains("网易云")) return "网易云音乐";
        if (name.Contains("qqmusic") || name.Contains("qq音乐")) return "QQ 音乐";
        if (name.Contains("kugou") || name.Contains("酷狗")) return "酷狗音乐";
        if (name.Contains("qishui") || name.Contains("luna") || name.Contains("汽水")) return "汽水音乐";
        return null;
    }

    private static async ValueTask SeekAsync(Session session, TimeSpan position, TimeSpan duration,
        CancellationToken token, CancellationToken lifetime)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        canceled.Token.ThrowIfCancellationRequested();
        var timeline = session.GetTimelineProperties();
        var min = timeline.MinSeekTime;
        var max = timeline.MaxSeekTime;
        if (max <= min)
        {
            min = timeline.StartTime;
            max = timeline.EndTime > min ? timeline.EndTime : min + duration;
        }
        if (max <= min || !await session.TryChangePlaybackPositionAsync(
            Math.Clamp((timeline.StartTime + position).Ticks, min.Ticks, max.Ticks)).AsTask(canceled.Token))
        {
            throw new InvalidOperationException("播放器未接受进度设置。");
        }
    }

    private static IEnumerable<IslandAction> BuildActions(Session session,
        GlobalSystemMediaTransportControlsSessionPlaybackControls controls, bool playing, CancellationToken lifetime,
        Action refreshAfterSkip)
    {
        IslandAction Action(string title, Func<IAsyncOperation<bool>> command, bool skip = false) => new(title, async token =>
        {
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            canceled.Token.ThrowIfCancellationRequested();
            if (skip) canceled.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                if (!await command().AsTask(canceled.Token))
                    throw new InvalidOperationException("播放器未接受命令。");
            }
            finally
            {
                if (skip && !lifetime.IsCancellationRequested) refreshAfterSkip();
            }
        });

        if (controls.IsPreviousEnabled)
        {
            yield return Action("上一首", session.TrySkipPreviousAsync, skip: true);
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
            yield return Action("下一首", session.TrySkipNextAsync, skip: true);
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
