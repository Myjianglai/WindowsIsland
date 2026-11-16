using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Island.App.Persistence;

internal enum DockEdge { Top, Left, Right, Bottom }
internal enum IslandPage { Overview, Music, Clipboard, Quota, Devices, Shelf, Todo }
internal enum IslandSkin { Dark, Cream }
internal enum IslandShape { Capsule, Notch, Indicator }
internal enum MusicPreview { Off, Cover, Rhythm, Track, Lyric }
internal enum NotchItem { Quotas, Music, System, Clock, Devices, Shelf }
internal sealed record PagePreference(IslandPage Page, bool Enabled);

internal sealed record IslandPreferences
{
    private static readonly string PathOnDisk = Path.Combine(LocalDataStore.DirectoryPath, "appearance.json");
    public DockEdge Edge { get; init; } = DockEdge.Top;
    public int Scale { get; init; } = 100;
    public int Position { get; init; } = 50;
    public string? Monitor { get; init; }
    public bool PixelShift { get; init; }
    public bool Minimal { get; init; }
    public IslandSkin Skin { get; init; }
    public IslandShape Shape { get; init; } = IslandShape.Notch;
    public bool InTaskbar { get; init; }
    public bool AlwaysOnTop { get; init; } = true;
    public bool HideForFullscreen { get; init; } = true;
    public bool IndicatorWhenMaximized { get; init; } = true;
    public bool QuotaWarnings { get; init; } = true;
    public bool MultipleRings { get; init; }
    public bool ShowRemaining { get; init; }
    public bool RingInitial { get; init; }
    public bool CompactClock { get; init; }
    public NotchItem[] NotchOrder { get; init; } = Enum.GetValues<NotchItem>();
    public bool CompactDevices { get; init; }
    public bool CompactShelfCount { get; init; }
    public bool ShowMusicCover { get; init; } = true;
    public bool ShowMusicRhythm { get; init; }
    public MusicPreview CompactMusic { get; init; } = MusicPreview.Lyric;
    public bool Use24HourClock { get; init; } = true;
    public string[] AvoidApps { get; init; } = [];
    public bool CompactLyrics { get; init; } = true;
    public string LyricsFolder { get; init; } = "";
    public bool NeteaseOnlineLyrics { get; init; }
    public PagePreference[] Pages { get; init; } =
        Enum.GetValues<IslandPage>().Select(page => new PagePreference(page, true)).ToArray();

    public static IslandPreferences Load()
    {
        try
        {
            return (JsonSerializer.Deserialize<IslandPreferences>(File.ReadAllBytes(PathOnDisk)) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save() => LocalDataStore.WriteAtomic(PathOnDisk, JsonSerializer.SerializeToUtf8Bytes(Normalize()));

    public IslandPreferences Normalize()
    {
        var pages = (Pages ?? []).Where(page => page is not null && Enum.IsDefined(page.Page))
            .DistinctBy(page => page.Page).ToList();
        foreach (var page in Enum.GetValues<IslandPage>())
        {
            if (!pages.Any(item => item.Page == page))
            {
                pages.Add(new PagePreference(page, true));
            }
        }
        return this with
        {
            Edge = Enum.IsDefined(Edge) ? Edge : DockEdge.Top,
            Skin = Enum.IsDefined(Skin) ? Skin : IslandSkin.Dark,
            Shape = Enum.IsDefined(Shape) ? Shape : IslandShape.Capsule,
            CompactMusic = Enum.IsDefined(CompactMusic) ? CompactMusic : MusicPreview.Off,
            NotchOrder = (NotchOrder ?? []).Where(item => Enum.IsDefined(item))
                .Concat(Enum.GetValues<NotchItem>()).Distinct().ToArray(),
            Scale = Math.Clamp(Scale, 80, 140),
            Position = Math.Clamp(Position, 0, 100),
            Pages = pages.ToArray(),
            AvoidApps = (AvoidApps ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray(),
            LyricsFolder = LyricsFolder ?? ""
        };
    }
}
