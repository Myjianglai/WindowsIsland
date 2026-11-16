using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class DeviceBatteryView : DockPanel
{
    private readonly UniformGrid _cards = new() { Columns = 2 };
    private readonly TextBlock _empty = ExpandedTabs.Label("", 13, IslandTheme.Muted);
    private readonly ScrollViewer _scroll;
    private IReadOnlyList<DeviceBattery> _devices = [];
    private string _status = "";
    private int _warning = 25;
    private int _critical = 15;
    private BatteryDeviceChoice[] _hidden = [];

    public DeviceBatteryView()
    {
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _cards };
        _scroll.SizeChanged += (_, e) =>
        {
            _cards.Columns = Math.Clamp((int)(e.NewSize.Width / 220), 1, 3);
        };
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(new Grid { Children = { _scroll, _empty } });
        Update(new IslandEvent("device-battery", IslandEventType.DeviceBattery, "正在读取已连接设备的电量"));
    }

    public void Update(IslandEvent message)
    {
        var devices = message.Devices ?? [];
        if (_status == message.Title && _devices.SequenceEqual(devices)) return;
        _status = message.Title;
        _devices = devices;
        Render();
    }

    public void ApplyPreferences(IslandPreferences preferences)
    {
        if (_warning == preferences.BatteryWarning && _critical == preferences.BatteryCritical &&
            _hidden.SequenceEqual(preferences.ExpandedBatteryHidden)) return;
        _warning = preferences.BatteryWarning;
        _critical = preferences.BatteryCritical;
        _hidden = preferences.ExpandedBatteryHidden;
        Render();
    }

    private void Render()
    {
        var devices = _devices.Where(device => !_hidden.Contains(BatteryDeviceChoice.From(device))).ToArray();
        _empty.Text = _devices.Count == 0 ? _status : "没有选择在展开页显示的设备";
        _empty.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = devices.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _cards.Children.Clear();
        foreach (var device in devices) _cards.Children.Add(CreateCard(device, _warning, _critical));
    }

    public static string Tone(DeviceBattery device, int warning, int critical, string normal = IslandTheme.Ink)
    {
        if (device.Connection == "本机" && device.Level.Contains("充电中", StringComparison.Ordinal)) return "AccountGreen";
        if (device.Percent is not { } value) return device.Low ? IslandTheme.Alert : normal;
        return value <= critical ? IslandTheme.Alert : value <= warning ? "AccountOrange" : normal;
    }

    public static Viewbox Gauge(DeviceBattery device, string color, double size)
    {
        var ring = new Canvas { Width = 126, Height = 126 };
        var outline = new Path { Data = new EllipseGeometry(new Point(63, 63), 57, 57), StrokeThickness = 8 };
        outline.SetResourceReference(Shape.StrokeProperty, IslandTheme.Edge);
        ring.Children.Add(outline);
        if (device.Percent is > 0 and <= 100)
        {
            var amount = new Path { Data = QuotaDisplay.Arc(63, 57, device.Percent.Value / 100d), StrokeThickness = 8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            amount.SetResourceReference(Shape.StrokeProperty, color);
            ring.Children.Add(amount);
        }
        var icon = PanelIcons.Page(Persistence.IslandPage.Devices);
        icon.Width = 34;
        icon.Height = 34;
        icon.SetResourceReference(Shape.StrokeProperty, color);
        Canvas.SetLeft(icon, 46);
        Canvas.SetTop(icon, 46);
        ring.Children.Add(icon);
        return new Viewbox { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Child = ring };
    }

    private static Border CreateCard(DeviceBattery device, int warning, int critical)
    {
        string color = Tone(device, warning, critical);
        var percent = ExpandedTabs.Label(device.Level, device.Level.Length > 8 ? 14 : 23, color);
        percent.FontWeight = FontWeights.SemiBold;
        var name = ExpandedTabs.Label(device.Name, 13);
        var connection = ExpandedTabs.Label(device.Connection ?? "", 11, IslandTheme.Muted);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0),
            Children = { percent, name, connection } };
        var gauge = Gauge(device, color, 60);
        DockPanel.SetDock(gauge, Dock.Left);
        var card = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(4), Padding = new Thickness(10),
            ToolTip = $"{device.Name} · {device.Level}{(device.Connection is { Length: > 0 } source ? $" · {source}" : "")}",
            Child = new DockPanel { Children = { gauge, text } } };
        card.SetResourceReference(BackgroundProperty, IslandTheme.Tile);
        return card;
    }
}
