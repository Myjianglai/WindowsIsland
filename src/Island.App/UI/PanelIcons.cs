using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.App.Persistence;

namespace Island.App.UI;

internal static class PanelIcons
{
    public static Path Page(IslandPage page) => Draw(page switch
    {
        IslandPage.Quota => "M3,16 H16 M5,13 V10 M9,13 V7 M13,13 V4 M3,7 L7,4 L10,5 L14,2",
        IslandPage.Agent => "M4,6 H14 V14 H4 Z M9,6 V3 M2,9 V12 M16,9 V12 M7,9 V11 M11,9 V11",
        IslandPage.Music => "M7,13 V4 L15,2 V11 M7,13 A2,2 0 1 1 3,13 A2,2 0 1 1 7,13 M15,11 A2,2 0 1 1 11,11 A2,2 0 1 1 15,11",
        IslandPage.Clipboard => "M6,4 H3 V16 H15 V4 H12 M6,2 H12 V6 H6 Z M6,9 H12 M6,12 H12",
        IslandPage.Devices => "M3,5 H14 V13 H3 Z M16,7 V11 M6,7 V11 M9,7 V11",
        IslandPage.Shelf => "M2,6 H16 V15 H2 Z M4,6 L6,3 H12 L14,6 M7,9 H11",
        IslandPage.Todo => "M3,4 L4.5,5.5 L7,2.5 M9,4 H16 M3,9 L4.5,10.5 L7,7.5 M9,9 H16 M3,14 L4.5,15.5 L7,12.5 M9,14 H16",
        IslandPage.Messages => "M3,13 L5,10 V7 A4,4 0 0 1 13,7 V10 L15,13 Z M7,15 Q9,18 11,15",
        IslandPage.Xiaohongshu => "M3,3 H15 V16 H3 Z M6,6 H12 M6,9 H12 M6,12 H10",
        _ => "M1,9 H5 L7,4 L10,14 L12,9 H17"
    });

    public static Path Refresh() => Draw("M15,7 A6,6 0 0 0 4,5 L2,7 M2,3 V7 H6 M3,11 A6,6 0 0 0 14,13 L16,11 M16,15 V11 H12");

    public static Path Settings() => Draw("M7,2 H11 L12,5 L15,5 L17,8 L15,10 L15,13 L12,14 L11,17 H7 L6,14 L3,13 L3,10 L1,8 L3,5 H6 Z M11.5,9 A2.5,2.5 0 1 1 6.5,9 A2.5,2.5 0 1 1 11.5,9");

    public static Path? Playback(string action)
    {
        string? data = action switch
        {
            "上一首" => "M0,8 L10,0 V16 Z M10,8 L20,0 V16 Z",
            "下一首" => "M0,0 L10,8 L0,16 Z M10,0 L20,8 L10,16 Z",
            "播放" => "M3,0 L17,8 L3,16 Z",
            "暂停" => "M3,0 H8 V16 H3 Z M12,0 H17 V16 H12 Z",
            _ => null
        };
        if (data is null) return null;
        var icon = new Path { Data = Geometry.Parse(data), Width = 22, Height = 17, Stretch = Stretch.Uniform,
            IsHitTestVisible = false };
        icon.SetResourceReference(Shape.FillProperty, IslandTheme.Ink);
        return icon;
    }

    private static Path Draw(string data)
    {
        var icon = new Path
        {
            Data = Geometry.Parse(data), Width = 16, Height = 16, Stretch = Stretch.Uniform,
            StrokeThickness = 1.35, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        icon.SetResourceReference(Shape.StrokeProperty, IslandTheme.Muted);
        return icon;
    }
}
