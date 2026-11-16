using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class SystemDashboard : Grid
{
    private readonly TextBlock _cpu = ExpandedTabs.Label("—", 25);
    private readonly UniformGrid _cores = new() { Rows = 1, Height = 54, Margin = new Thickness(0, 7, 0, 10) };
    private readonly TextBlock _memory = ExpandedTabs.Label("—", 12);
    private readonly ProgressBar _memoryBar = new() { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(7, 0, 0, 0) };
    private readonly TextBlock _download = ExpandedTabs.Label("↓ —", 12);
    private readonly TextBlock _upload = ExpandedTabs.Label("↑ —", 12);
    private readonly TextBlock _battery = ExpandedTabs.Label("", 11, IslandTheme.Muted);
    private readonly TextBlock _empty = ExpandedTabs.Label("在「设置 → 数据源」启用系统监控", 12, IslandTheme.Muted);
    private readonly StackPanel _ranking = new();
    private readonly Button _cpuSort = ExpandedTabs.MakeButton("CPU");
    private readonly Button _memorySort = ExpandedTabs.MakeButton("内存");
    private readonly Brush _blue = new SolidColorBrush(Color.FromRgb(62, 145, 249));
    private readonly Brush _cyan = new SolidColorBrush(Color.FromRgb(67, 201, 200));
    private SystemMetrics? _latest;
    private bool _byMemory;

    public SystemDashboard()
    {
        Margin = new Thickness(5, 6, 3, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        var cpuHeading = new StackPanel { Orientation = Orientation.Horizontal };
        cpuHeading.Children.Add(Badge("C", _blue));
        _cpu.FontWeight = FontWeights.SemiBold;
        _cpu.Margin = new Thickness(8, 0, 6, 0);
        cpuHeading.Children.Add(_cpu);
        var cpuName = ExpandedTabs.Label("CPU", 11, IslandTheme.Muted);
        cpuName.VerticalAlignment = VerticalAlignment.Center;
        cpuHeading.Children.Add(cpuName);
        left.Children.Add(cpuHeading);
        left.Children.Add(_cores);

        var memoryLine = new DockPanel();
        memoryLine.Children.Add(Badge("M", _cyan));
        _memory.Margin = new Thickness(7, 0, 0, 0);
        _memory.VerticalAlignment = VerticalAlignment.Center;
        memoryLine.Children.Add(_memory);
        _memoryBar.VerticalAlignment = VerticalAlignment.Center;
        _memoryBar.Foreground = _cyan;
        _memoryBar.SetResourceReference(ProgressBar.BackgroundProperty, IslandTheme.Edge);
        _memoryBar.SetResourceReference(StyleProperty, "QuotaProgress");
        memoryLine.Children.Add(_memoryBar);
        left.Children.Add(memoryLine);

        var network = new UniformGrid { Columns = 2, Margin = new Thickness(0, 13, 0, 0), Children = { _download, _upload } };
        left.Children.Add(network);
        _battery.Margin = new Thickness(0, 10, 0, 0);
        left.Children.Add(_battery);
        left.Children.Add(_empty);

        var sort = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 7), Children = { _cpuSort, _memorySort } };
        _cpuSort.Click += (_, _) => SortByMemory(false);
        _memorySort.Click += (_, _) => SortByMemory(true);
        var right = new StackPanel { Children = { sort, _ranking } };
        SetColumn(right, 1);
        Children.Add(left);
        Children.Add(right);
        SortByMemory(false);
    }

    private static Border Badge(string text, Brush brush)
    {
        var letter = ExpandedTabs.Label(text, 10);
        letter.Foreground = brush;
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        letter.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = letter, Width = 19, Height = 19, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), BorderBrush = brush, VerticalAlignment = VerticalAlignment.Center };
    }

    public void Update(SystemMetrics? metrics)
    {
        _latest = metrics;
        _empty.Visibility = metrics is null ? Visibility.Visible : Visibility.Collapsed;
        _cpu.Text = metrics?.Cpu is { } cpu ? $"{cpu:0}%" : "—";
        var cores = metrics?.Cores ?? [];
        if (_cores.Children.Count != cores.Count)
        {
            _cores.Children.Clear();
            for (int index = 0; index < cores.Count; index++)
            {
                var fill = new Border { Background = _blue, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom };
                var track = new Border { Child = fill, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(3) };
                track.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
                _cores.Children.Add(track);
            }
        }
        for (int index = 0; index < cores.Count; index++)
        {
            var track = (Border)_cores.Children[index];
            ((Border)track.Child).Height = Math.Max(2, _cores.Height * cores[index] / 100);
            track.ToolTip = $"逻辑处理器 {index + 1} · {cores[index]:0.#}%";
        }
        _memory.Text = metrics?.MemoryUsedBytes is { } used && metrics.MemoryTotalBytes is { } total
            ? $"{Bytes(used)} / {Bytes(total)}" : "内存 —";
        _memoryBar.Value = metrics?.Memory ?? 0;
        _download.Text = $"↓ {Rate(metrics?.Download)} · 本次 {Bytes(metrics?.DownloadTotal)}";
        _upload.Text = $"↑ {Rate(metrics?.Upload)} · 本次 {Bytes(metrics?.UploadTotal)}";
        _battery.Text = metrics?.BatteryPercent is { } percent ? $"电脑电量 {percent}%{(metrics.Charging ? " · 充电中" : "")}" : "";
        RefreshRanking();
    }

    private void SortByMemory(bool byMemory)
    {
        _byMemory = byMemory;
        _cpuSort.SetResourceReference(Control.BackgroundProperty, byMemory ? IslandTheme.Surface : IslandTheme.Tile);
        _memorySort.SetResourceReference(Control.BackgroundProperty, byMemory ? IslandTheme.Tile : IslandTheme.Surface);
        RefreshRanking();
    }

    private void RefreshRanking()
    {
        _ranking.Children.Clear();
        var entries = _latest?.Processes ?? [];
        var ranked = _byMemory ? entries.OrderByDescending(p => p.MemoryBytes)
            : entries.Where(p => p.Cpu.HasValue).OrderByDescending(p => p.Cpu);
        foreach (var process in ranked.Take(6))
        {
            var score = ExpandedTabs.Label(_byMemory ? Bytes(process.MemoryBytes) : $"{process.Cpu:0.#}%", 13);
            DockPanel.SetDock(score, Dock.Right);
            score.VerticalAlignment = VerticalAlignment.Center;
            score.Margin = new Thickness(8, 0, 2, 0);
            var name = ExpandedTabs.Label(process.Name, 13);
            name.Margin = new Thickness(7, 0, 0, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            var badge = Badge(string.IsNullOrWhiteSpace(process.Name) ? "?" : process.Name[..1].ToUpperInvariant(), _blue);
            DockPanel.SetDock(badge, Dock.Left);
            _ranking.Children.Add(new DockPanel { Height = 25, ToolTip = process.Name, Children = { score, badge, name } });
        }
        if (_ranking.Children.Count == 0)
            _ranking.Children.Add(ExpandedTabs.Label("暂无进程读数", 12, IslandTheme.Muted));
    }

    private static string Rate(double? bytes) => bytes is null ? "—" : Bytes(bytes) + "/s";
    private static string Bytes(double? amount) => amount switch
    {
        >= 1073741824 => $"{amount / 1073741824:0.#} GB",
        >= 1048576 => $"{amount / 1048576:0.#} MB",
        >= 1024 => $"{amount / 1024:0.#} KB",
        { } bytes => $"{bytes:0} B", _ => "—"
    };
}
