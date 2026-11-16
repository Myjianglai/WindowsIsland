using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class CompactBatterySettings : StackPanel
{
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "自动选择", "手动选择设备" } };
    private readonly CheckBox _numbers = new();
    private readonly ComboBox _warning;
    private readonly ComboBox _critical;
    private readonly StackPanel _preview = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _rows = new();
    private readonly Action _changed;
    private IReadOnlyList<DeviceBattery> _devices = [];
    private List<BatteryDeviceChoice>? _selected;
    private readonly HashSet<BatteryDeviceChoice> _expandedHidden;
    private bool _updating;
    private IslandSkin _skin;

    public CompactBatterySettings(IslandPreferences settings, Action changed, CheckBox compactEnabled)
    {
        _changed = changed;
        _selected = settings.CompactBatteryDevices?.ToList();
        _expandedHidden = settings.ExpandedBatteryHidden.ToHashSet();
        _skin = settings.Skin;
        IslandTheme.Apply(Resources, _skin);
        Children.Add(SettingsRows.ResetButton(Reset));
        SettingsRows.Section(this, "收起时");
        var pill = new Border { Child = _preview, CornerRadius = new CornerRadius(23), Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, IslandTheme.Surface);
        Children.Add(new Border { Child = pill, CornerRadius = new CornerRadius(8), Height = 84, Margin = new Thickness(0, 8, 0, 8),
            BorderBrush = SettingsRows.Rule, BorderThickness = new Thickness(1), Background = SettingsRows.Brush("#ECEEF2") });
        var enabled = new CheckBox();
        enabled.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked))
        {
            Source = compactEnabled, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        Children.Add(SettingsRows.Row("收起时显示设备电量", enabled));
        _numbers.IsChecked = settings.CompactBatteryNumbers;
        Children.Add(SettingsRows.Row("显示数字", _numbers));
        Children.Add(SettingsRows.Row("选择设备的方式", _mode));
        _mode.SelectedIndex = settings.CompactBatteryDevices is null ? 0 : 1;

        SettingsRows.Section(this, "电量阈值");
        _warning = Threshold(settings.BatteryWarning);
        _critical = Threshold(settings.BatteryCritical);
        Children.Add(SettingsRows.Row("预警阈值", _warning));
        Children.Add(SettingsRows.Row("告警阈值", _critical));
        SettingsRows.Section(this, "设备");
        Children.Add(_rows);

        _mode.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            if (_mode.SelectedIndex == 1 && _selected is null)
                _selected = BatteryDeviceChoice.Select(_devices, null).Select(BatteryDeviceChoice.From).ToList();
            RenderRows();
            Notify();
        };
        _numbers.Click += (_, _) => Notify();
        _warning.SelectionChanged += (_, _) => ThresholdChanged(_critical, Math.Min(Warning, Critical));
        _critical.SelectionChanged += (_, _) => ThresholdChanged(_warning, Math.Max(Warning, Critical));
        RenderRows();
        RenderPreview();
    }

    public BatteryDeviceChoice[]? Selection => _mode.SelectedIndex == 1 ? _selected?.ToArray() ?? [] : null;
    public BatteryDeviceChoice[] ExpandedHidden => _expandedHidden.ToArray();
    public bool ShowNumbers => _numbers.IsChecked == true;
    public int Warning => (int)_warning.SelectedValue;
    public int Critical => (int)_critical.SelectedValue;

    public void SetSkin(IslandSkin skin)
    {
        if (_skin == skin) return;
        _skin = skin;
        IslandTheme.Apply(Resources, skin);
    }

    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices)
    {
        if (_devices.SequenceEqual(devices)) return;
        _devices = devices;
        RenderRows();
        RenderPreview();
    }

    private void ThresholdChanged(ComboBox other, int limit)
    {
        if (_updating) return;
        _updating = true;
        other.SelectedValue = limit;
        _updating = false;
        Notify();
    }

    private void Reset()
    {
        var defaults = new IslandPreferences();
        _updating = true;
        _selected = null;
        _expandedHidden.Clear();
        _mode.SelectedIndex = 0;
        _numbers.IsChecked = defaults.CompactBatteryNumbers;
        _warning.SelectedValue = defaults.BatteryWarning;
        _critical.SelectedValue = defaults.BatteryCritical;
        _updating = false;
        RenderRows();
        Notify();
    }

    private void Notify()
    {
        RenderPreview();
        _changed();
    }

    private void RenderPreview()
    {
        _preview.Children.Clear();
        var battery = BatteryDeviceChoice.Select(_devices, Selection).FirstOrDefault()
            ?? new DeviceBattery("示例设备", "75%", false, 75);
        var color = DeviceBatteryView.Tone(battery, Warning, Critical);
        _preview.Children.Add(DeviceBatteryView.Gauge(battery, color, 25));
        var label = ExpandedTabs.Label(battery.Name + (ShowNumbers ? $"  {battery.Level}" : ""), 12, color);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(6, 0, 0, 0);
        _preview.Children.Add(label);
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        _rows.Children.Add(Row(Cell("名称"), Cell("收起时"), Cell("展开页")));
        var connected = _devices.DistinctBy(BatteryDeviceChoice.From).ToDictionary(BatteryDeviceChoice.From);
        var choices = connected.Keys.Concat(_selected ?? []).Concat(_expandedHidden).Distinct()
            .OrderBy(device => device.Name, StringComparer.CurrentCulture).ThenBy(device => device.Connection, StringComparer.CurrentCulture);
        var compact = BatteryDeviceChoice.Select(_devices, Selection).Select(BatteryDeviceChoice.From).ToHashSet();
        foreach (var key in choices)
        {
            var battery = connected.GetValueOrDefault(key);
            var text = new StackPanel
            {
                Children =
                {
                    Cell(key.Name, 13, SettingsRows.Ink),
                    Cell(string.Join(" · ", new[] { key.Connection, battery?.Level ?? "未连接" }.Where(value => value.Length > 0)))
                }
            };
            var collapsed = new CheckBox { IsChecked = _mode.SelectedIndex == 1 ? _selected?.Contains(key) == true : compact.Contains(key) };
            SettingsRows.StyleToggle(collapsed, $"收起时显示 {key.Name}");
            collapsed.Click += (_, _) =>
            {
                if (_mode.SelectedIndex == 0)
                {
                    _selected = compact.ToList();
                    _updating = true;
                    _mode.SelectedIndex = 1;
                    _updating = false;
                }
                _selected ??= [];
                if (collapsed.IsChecked == true)
                {
                    if (!_selected.Contains(key)) _selected.Add(key);
                }
                else _selected.Remove(key);
                Notify();
            };
            var expanded = new CheckBox { IsChecked = !_expandedHidden.Contains(key) };
            SettingsRows.StyleToggle(expanded, $"展开页显示 {key.Name}");
            expanded.Click += (_, _) =>
            {
                if (expanded.IsChecked == true) _expandedHidden.Remove(key);
                else _expandedHidden.Add(key);
                Notify();
            };
            _rows.Children.Add(Row(text, collapsed, expanded));
        }
        if (_rows.Children.Count == 1)
            _rows.Children.Add(Cell("连接设备后，它会出现在这里。"));
    }

    private static TextBlock Cell(string text, double size = 11, System.Windows.Media.Brush? color = null) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? SettingsRows.Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Border Row(FrameworkElement name, FrameworkElement collapsed, FrameworkElement expanded)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
        grid.Children.Add(name);
        foreach (var (element, index) in new[] { (collapsed, 1), (expanded, 2) })
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, index);
            grid.Children.Add(element);
        }
        return new Border { Child = grid, MinHeight = 46, BorderBrush = SettingsRows.Rule,
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 5, 0, 5) };
    }

    private static ComboBox Threshold(int value) => new()
    {
        ItemsSource = Enumerable.Range(0, 101).Select(percent => new ThresholdChoice(percent)).ToArray(),
        SelectedValuePath = nameof(ThresholdChoice.Percent), DisplayMemberPath = nameof(ThresholdChoice.Label), SelectedValue = value
    };
    private sealed record ThresholdChoice(int Percent)
    {
        public string Label => $"{Percent}%";
    }
}
