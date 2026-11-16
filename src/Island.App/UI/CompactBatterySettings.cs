using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class CompactBatterySettings : StackPanel
{
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "自动选择", "手动选择设备" } };
    private readonly StackPanel _rows = new();
    private IReadOnlyList<DeviceBattery> _devices = [];
    private List<BatteryDeviceChoice>? _selected;

    public CompactBatterySettings(BatteryDeviceChoice[]? selected)
    {
        _selected = selected?.ToList();
        SettingsRows.Section(this, "收起时显示的电量");
        Children.Add(SettingsRows.Row("选择设备的方式", _mode));
        Children.Add(_rows);
        _mode.SelectedIndex = selected is null ? 0 : 1;
        _mode.SelectionChanged += (_, _) =>
        {
            if (_mode.SelectedIndex == 1 && _selected is null)
                _selected = BatteryDeviceChoice.Select(_devices, null).Select(BatteryDeviceChoice.From).ToList();
            Render();
        };
        Render();
    }

    public BatteryDeviceChoice[]? Selection => _mode.SelectedIndex == 1 ? _selected?.ToArray() ?? [] : null;

    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices)
    {
        var oldChoices = _devices.Select(BatteryDeviceChoice.From).ToHashSet();
        _devices = devices;
        if (!oldChoices.SetEquals(devices.Select(BatteryDeviceChoice.From))) Render();
    }

    public void Reset()
    {
        _selected = null;
        _mode.SelectedIndex = 0;
    }

    private void Render()
    {
        _rows.Children.Clear();
        _rows.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_rows.Visibility != Visibility.Visible) return;
        var connected = _devices.Select(BatteryDeviceChoice.From).ToHashSet();
        var choices = connected.Concat(_selected ?? []).Distinct().OrderBy(device => device.Name, StringComparer.CurrentCulture)
            .ThenBy(device => device.Connection, StringComparer.CurrentCulture).ToArray();
        if (choices.Length == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "连接设备后，这里会列出可选择的设备。",
                Foreground = SettingsRows.Muted, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var device in choices)
        {
            var toggle = new CheckBox { IsChecked = _selected?.Contains(device) == true };
            string label = device.Name + (device.Connection.Length == 0 ? "" : $" · {device.Connection}")
                + (connected.Contains(device) ? "" : "（未连接）");
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true)
                    (_selected ??= []).Add(device);
                else
                    _selected?.Remove(device);
            };
            _rows.Children.Add(SettingsRows.Row(label, toggle));
        }
    }
}
