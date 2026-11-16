using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Island.Core.Events;

namespace Island.App.Persistence;

internal enum DockEdge { Top, Left, Right, Bottom }
internal enum IslandPage { Overview, Music, Clipboard, Quota, Devices, Shelf, Todo }
internal enum IslandSkin { Dark, Cream }
internal enum IslandShape { Capsule, Notch, Indicator }
internal enum MusicPreview { Off, Cover, Rhythm, Track, Lyric }
internal enum NotchItem { Quotas, Music, System, Clock, Devices, Shelf, Todo }
internal enum IslandWheelAction { None, Pages, Volume }
internal sealed record PagePreference(IslandPage Page, bool Enabled);
internal sealed record BatteryDeviceChoice(string Name, string Connection)
{
    public static BatteryDeviceChoice From(DeviceBattery device) =>
        new(device.Name, device.Connection == "蓝牙 LE" ? "蓝牙" : device.Connection ?? "");

    public static DeviceBattery[] Select(IReadOnlyList<DeviceBattery> devices, BatteryDeviceChoice[]? selected)
    {
        var known = devices.Where(device => device.Level != "未知").DistinctBy(From).ToArray();
        if (selected is not null)
        {
            var byChoice = known.ToDictionary(From);
            return selected.Where(byChoice.ContainsKey).Select(choice => byChoice[choice]).ToArray();
        }
        return known.Where(device => device.Connection != "本机").Take(2)
            .Concat(known.Where(device => device.Connection == "本机").Take(1)).ToArray();
    }
}

internal sealed record IslandPreferences
{
    private static readonly string PathOnDisk = Path.Combine(LocalDataStore.DirectoryPath, "appearance.json");
    public DockEdge Edge { get; init; } = DockEdge.Top;
    public int Scale { get; init; } = 100;
    public int Position { get; init; } = 50;
    public bool AutoCompactWidth { get; init; } = true;
    public int CompactWidth { get; init; } = 230;
    public int CompactHeight { get; init; } = 38;
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
    public bool CompactQuotas { get; init; } = true;
    public bool CompactSystem { get; init; } = true;
    public bool ShowCompactMusic { get; init; } = true;
    public IslandPage? ExpandPage { get; init; }
    public bool RightClickSettings { get; init; }
    public IslandWheelAction WheelAction { get; init; }
    public NotchItem[] NotchOrder { get; init; } = Enum.GetValues<NotchItem>();
    public bool CompactDevices { get; init; }
    // null 使用自动选择，空数组表示没有勾选任何设备。
    public BatteryDeviceChoice[]? CompactBatteryDevices { get; init; }
    public BatteryDeviceChoice[] ExpandedBatteryHidden { get; init; } = [];
    public bool CompactBatteryNumbers { get; init; } = true;
    public int BatteryWarning { get; init; } = 25;
    public int BatteryCritical { get; init; } = 15;
    public bool CompactShelfCount { get; init; }
    public bool CompactTodo { get; init; } = true;
    public bool ShowMusicCover { get; init; } = true;
    public bool ShowMusicRhythm { get; init; }
    public MusicPreview CompactMusic { get; init; } = MusicPreview.Lyric;
    public bool Use24HourClock { get; init; } = true;
    public string[] AvoidApps { get; init; } = [];
    public bool CompactLyrics { get; init; } = true;
    public string LyricsFolder { get; init; } = "";
    public bool NeteaseOnlineLyrics { get; init; }
    public bool GeneralOnlineLyrics { get; init; }
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
            ExpandPage = ExpandPage is { } opening && Enum.IsDefined(opening) ? opening : null,
            WheelAction = Enum.IsDefined(WheelAction) ? WheelAction : IslandWheelAction.None,
            CompactWidth = Math.Clamp(CompactWidth, 72, 720),
            CompactHeight = Math.Clamp(CompactHeight, 24, 64),
            CompactBatteryDevices = CompactBatteryDevices?.Where(device => device is not null &&
                !string.IsNullOrWhiteSpace(device.Name) && device.Connection is not null).Distinct().ToArray(),
            ExpandedBatteryHidden = (ExpandedBatteryHidden ?? []).Where(device => device is not null &&
                !string.IsNullOrWhiteSpace(device.Name) && device.Connection is not null).Distinct().ToArray(),
            BatteryWarning = Math.Clamp(BatteryWarning, 0, 100),
            BatteryCritical = Math.Clamp(BatteryCritical, 0, Math.Clamp(BatteryWarning, 0, 100)),
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
