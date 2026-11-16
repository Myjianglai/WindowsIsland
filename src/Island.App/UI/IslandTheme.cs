using System.Windows;
using System.Windows.Media;
using Island.App.Persistence;

namespace Island.App.UI;

internal static class IslandTheme
{
    public const string Ink = "IslandInk";
    public const string Muted = "IslandMuted";
    public const string Surface = "IslandSurface";
    public const string Edge = "IslandEdge";
    public const string Tile = "IslandTile";
    public const string Accent = "IslandAccent";
    public const string Alert = "IslandAlert";

    public static void Apply(ResourceDictionary resources, IslandSkin skin)
    {
        var colors = skin == IslandSkin.Cream
            ? new[] { "#30241B", "#68594C", "#FFFAEF", "#DED0BB", "#F3E9D7", "#825329", "#A33D32" }
            : new[] { "#F3F4F6", "#A5A8B1", "#111217", "#31323C", "#22242E", "#B5BEFF", "#FF9C91" };
        string[] keys = [Ink, Muted, Surface, Edge, Tile, Accent, Alert];
        for (int index = 0; index < keys.Length; index++)
        {
            resources[keys[index]] = Brush(colors[index]);
        }
        resources["AccountBlue"] = Brush(skin == IslandSkin.Cream ? "#245A83" : "#91CBFF");
        resources["AccountOrange"] = Brush(skin == IslandSkin.Cream ? "#854C17" : "#F3AE72");
        resources["AccountGreen"] = Brush(skin == IslandSkin.Cream ? "#32634A" : "#7FD99E");
        resources["AccountPurple"] = Brush(skin == IslandSkin.Cream ? "#694591" : "#CDAAFC");
    }

    public static string Account(string color) => color is "Orange" or "Green" or "Purple" ? "Account" + color : "AccountBlue";

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
