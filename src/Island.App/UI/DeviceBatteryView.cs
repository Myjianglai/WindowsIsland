using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class DeviceBatteryView : DockPanel
{
    private readonly ComboBox _selector = new() { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 150 };
    private readonly ContentControl _surface = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private IReadOnlyList<DeviceBattery> _devices = [];
    private string? _current;

    public DeviceBatteryView()
    {
        DockPanel.SetDock(_selector, Dock.Bottom);
        Children.Add(_selector);
        Children.Add(_surface);
        _selector.Margin = new Thickness(0, 0, 0, 3);
        _selector.SelectionChanged += (_, _) =>
        {
            if (_selector.SelectedIndex >= 0 && _selector.SelectedIndex < _devices.Count)
                Show(_devices[_selector.SelectedIndex]);
        };
        Update(new IslandEvent("device-battery", IslandEventType.DeviceBattery, "正在读取已连接设备的电量"));
    }

    public void Update(IslandEvent message)
    {
        _devices = message.Devices ?? [];
        _selector.Visibility = _devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _selector.ItemsSource = _devices.Select(item => item.Name).ToArray();
        if (_devices.Count == 0)
        {
            _surface.Content = ExpandedTabs.Label(message.Title, 13, IslandTheme.Muted);
            return;
        }
        _selector.SelectedIndex = Math.Max(0, _devices.ToList().FindIndex(item => item.Name == _current));
        Show(_devices[_selector.SelectedIndex]);
    }

    private void Show(DeviceBattery device)
    {
        _current = device.Name;
        string color = device.Low ? IslandTheme.Alert : IslandTheme.Ink;
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
        var percent = ExpandedTabs.Label(device.Level, device.Level.Length > 8 ? 23 : 36, color);
        percent.FontWeight = FontWeights.SemiBold;
        var name = ExpandedTabs.Label(device.Name, 16);
        var connection = ExpandedTabs.Label(device.Connection ?? "", 12, IslandTheme.Muted);
        var text = new StackPanel { MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(21, 0, 0, 0), Children = { percent, name, connection } };
        _surface.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { ring, text } };
    }
}
