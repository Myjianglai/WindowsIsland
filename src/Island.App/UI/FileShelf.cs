using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Island.App.UI;

internal sealed class FileShelf : StackPanel
{
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, DateTimeOffset> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _rows = new();
    private readonly StackPanel _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
    private readonly TextBlock _status = ExpandedTabs.Label("把文件拖到小岛，可临时收集或拖出。", 12, IslandTheme.Muted);
    public event Action? CountChanged;
    public int Count => _paths.Count;

    public FileShelf()
    {
        var title = ExpandedTabs.Label("文件中转", 15);
        title.Margin = new Thickness(0, 0, 0, 9);
        Children.Add(title);
        var icon = PanelIcons.Page(Persistence.IslandPage.Shelf);
        icon.Width = icon.Height = 32;
        icon.Margin = new Thickness(0, 0, 0, 10);
        _empty.Children.Add(icon);
        _empty.Children.Add(ExpandedTabs.Label("把文件拖到这里", 13, IslandTheme.Muted));
        Children.Add(_empty);
        _status.Visibility = Visibility.Collapsed;
        Children.Add(_status);
        Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 176, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (!ClipboardSettings.Options.Shelf) return;
        foreach (var path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            _paths.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            _paths.Insert(0, path);
            _added[path] = DateTimeOffset.UtcNow;
        }
        Refresh();
    }

    public void Expire()
    {
        int minutes = ClipboardSettings.Options.ClearMinutes;
        if (minutes <= 0) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-minutes);
        if (_paths.RemoveAll(path => _added[path] <= cutoff && _added.Remove(path)) > 0) Refresh();
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        _empty.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = _paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = $"{_paths.Count} 个文件";
        foreach (var path in _paths)
        {
            var line = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            var file = ExpandedTabs.MakeButton("");
            file.HorizontalContentAlignment = HorizontalAlignment.Left;
            file.Content = ExpandedTabs.Label(Path.GetFileName(path), 12);
            file.ToolTip = path;
            file.Click += (_, _) => Open(path, reveal: false);
            Point start = default;
            file.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(file);
            file.PreviewMouseMove += (_, e) =>
            {
                Point current = e.GetPosition(file);
                if (e.LeftButton == MouseButtonState.Pressed && file.IsMouseCaptured &&
                    (Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance) && File.Exists(path))
                {
                    DragDrop.DoDragDrop(file, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
                }
            };
            var reveal = ExpandedTabs.MakeButton("定位");
            reveal.Click += (_, _) => Open(path, reveal: true);
            var remove = ExpandedTabs.MakeButton("移除");
            remove.Click += (_, _) => { _paths.Remove(path); _added.Remove(path); Refresh(); };
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(reveal, Dock.Right);
            line.Children.Add(remove);
            line.Children.Add(reveal);
            line.Children.Add(file);
            _rows.Children.Add(line);
        }
        CountChanged?.Invoke();
    }

    private void Open(string path, bool reveal)
    {
        if (!File.Exists(path))
        {
            _status.Text = "原文件已移动或删除。";
            return;
        }
        try
        {
            var start = reveal
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }
                : new ProcessStartInfo(path) { UseShellExecute = true };
            using var launched = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = "无法打开原文件。";
        }
    }
}
