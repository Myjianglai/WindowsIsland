namespace Island.Core.Events;

public enum IslandEventType
{
    Media,
    Notification,
    Clipboard,
    FileChange,
    Mail,
    Task,
    Quota,
    SystemMetrics,
    DeviceBattery
}

/// <summary>按钮只在本进程执行，回调由发送事件的 Provider 提供。</summary>
public sealed record IslandAction(string Label, Func<CancellationToken, ValueTask> ExecuteAsync);

/// <summary>播放器报告的时间轴及封面，进度可根据采样时刻在本机推算。</summary>
public sealed record LyricLine(TimeSpan At, string Text);

public sealed record MediaSnapshot(
    ReadOnlyMemory<byte> Artwork, TimeSpan Position, TimeSpan Duration,
    DateTimeOffset SampledAt, bool IsPlaying, double PlaybackRate,
    Func<TimeSpan, CancellationToken, ValueTask>? Seek = null, IReadOnlyList<LyricLine>? Lyrics = null,
    string? Player = null, bool HasPlaybackPosition = true);

public enum ClipboardKind { Text, Image, Files }
public sealed record ClipboardPreview(string Preview, bool IsPinned, IslandAction Copy, IslandAction TogglePin,
    ClipboardKind Kind = ClipboardKind.Text, ReadOnlyMemory<byte> Image = default, Guid Id = default, DateTimeOffset? AddedAt = null);
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardPreview> Entries, IslandAction Clear);

public sealed record QuotaMetric(string Key, string Label, double? Used, DateTimeOffset? Reset);
public sealed record QuotaAccountStatus(
    string Service, string Name, string Color, bool Pinned, string Summary, IReadOnlyList<string> Details,
    IReadOnlyList<QuotaMetric>? Metrics = null, IReadOnlyList<string>? RingKeys = null, Guid Id = default);
public sealed record QuotaSnapshot(IReadOnlyList<QuotaAccountStatus> Accounts);
public sealed record ProcessMetric(string Name, double? Cpu, long MemoryBytes);
public sealed record SystemMetrics(double? Cpu, uint? Memory, double? Download, double? Upload,
    IReadOnlyList<double>? Cores = null, IReadOnlyList<ProcessMetric>? Processes = null,
    ulong? MemoryUsedBytes = null, ulong? MemoryTotalBytes = null, double? DownloadTotal = null,
    double? UploadTotal = null, int? BatteryPercent = null, bool Charging = false);
public sealed record DeviceBattery(string Name, string Level, bool Low, int? Percent = null, string? Connection = null);

/// <summary>供界面消费的事件快照，图标键由界面解析为资源。</summary>
public sealed class IslandEvent
{
    public IslandEvent(
        string source,
        IslandEventType type,
        string title,
        string? subtitle = null,
        string? iconKey = null,
        IEnumerable<IslandAction>? actions = null,
        TimeSpan? timeout = null,
        MediaSnapshot? media = null,
        ClipboardSnapshot? clipboard = null,
        QuotaSnapshot? quota = null,
        SystemMetrics? metrics = null,
        IReadOnlyList<DeviceBattery>? devices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var displayTime = timeout ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(displayTime, TimeSpan.Zero);

        Source = source;
        Type = type;
        Title = title;
        Subtitle = subtitle;
        IconKey = iconKey;
        Actions = Array.AsReadOnly(actions?.ToArray() ?? []);
        Timeout = displayTime;
        Media = media;
        Clipboard = clipboard;
        Quota = quota;
        Metrics = metrics;
        Devices = devices;
    }

    public string Source { get; }
    public IslandEventType Type { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public string? IconKey { get; }
    public IReadOnlyList<IslandAction> Actions { get; }
    public TimeSpan Timeout { get; }
    public MediaSnapshot? Media { get; }
    public ClipboardSnapshot? Clipboard { get; }
    public QuotaSnapshot? Quota { get; }
    public SystemMetrics? Metrics { get; }
    public IReadOnlyList<DeviceBattery>? Devices { get; }
}
