using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class ClipboardHistoryView : DockPanel
{
    private readonly Button _all = ExpandedTabs.MakeButton("全部");
    private readonly Button _pinned = ExpandedTabs.MakeButton("常用");
    private readonly Button _clear = ExpandedTabs.MakeButton("清空");
    private readonly TextBlock _status = ExpandedTabs.Label("", 11, IslandTheme.Muted);
    private readonly StackPanel _rows = new();
    private readonly Dictionary<Guid, ImageSource> _thumbnails = new();
    private ClipboardSnapshot? _snapshot;
    private bool _onlyPinned;

    public ClipboardHistoryView()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_clear, Dock.Right);
        header.Children.Add(_clear);
        var title = ExpandedTabs.Label("剪贴板", 15);
        title.Margin = new Thickness(0, 0, 12, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, _all, _pinned } });
        Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);
        Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _all.Click += (_, _) => { _onlyPinned = false; Render(); };
        _pinned.Click += (_, _) => { _onlyPinned = true; Render(); };
        _clear.Click += async (_, _) =>
        {
            if (_snapshot is { } data) await RunAsync(data.Clear);
        };
        Update(new IslandEvent("clipboard", IslandEventType.Clipboard, "请在设置中启用剪贴板"));
    }

    public void Update(IslandEvent message)
    {
        _snapshot = message.Clipboard;
        _status.Text = message.Title == "剪贴板" ? "" : message.Title;
        var active = _snapshot?.Entries.Select(item => item.Id).ToHashSet() ?? [];
        foreach (var key in _thumbnails.Keys.Where(key => !active.Contains(key)).ToArray()) _thumbnails.Remove(key);
        Render();
    }

    private void Render()
    {
        _all.SetResourceReference(BackgroundProperty, _onlyPinned ? IslandTheme.Surface : IslandTheme.Tile);
        _pinned.SetResourceReference(BackgroundProperty, _onlyPinned ? IslandTheme.Tile : IslandTheme.Surface);
        _clear.IsEnabled = _snapshot is not null;
        _rows.Children.Clear();
        foreach (var item in _snapshot?.Entries.Where(item => !_onlyPinned || item.IsPinned) ?? [])
        {
            var pin = ExpandedTabs.MakeButton(item.IsPinned ? "★" : "☆");
            pin.ToolTip = item.TogglePin.Label;
            pin.Click += async (_, _) => await RunAsync(item.TogglePin);
            DockPanel.SetDock(pin, Dock.Right);
            var label = ExpandedTabs.Label(item.Preview, 12);
            label.VerticalAlignment = VerticalAlignment.Center;
            var preview = new DockPanel { LastChildFill = true };
            if (item.Kind == ClipboardKind.Image)
            {
                if (!_thumbnails.TryGetValue(item.Id, out var thumb) && ExpandedTabs.DecodeArtwork(item.Image) is { } image)
                {
                    thumb = image;
                    _thumbnails[item.Id] = image;
                }
                var picture = new Image { Source = thumb, Width = 36, Height = 24, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 7, 0) };
                preview.Children.Add(picture);
            }
            else if (item.Kind == ClipboardKind.Files)
            {
                var file = PanelIcons.Page(Persistence.IslandPage.Shelf);
                file.Margin = new Thickness(0, 0, 6, 0);
                preview.Children.Add(file);
            }
            if (item.AddedAt is { } time)
            {
                var date = ExpandedTabs.Label(time.ToLocalTime().ToString(time.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "MM-dd"), 10, IslandTheme.Muted);
                date.VerticalAlignment = VerticalAlignment.Center;
                date.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(date, Dock.Right);
                preview.Children.Add(date);
            }
            preview.Children.Add(label);
            var copy = ExpandedTabs.MakeButton("");
            copy.Content = preview;
            copy.Margin = new Thickness(0);
            copy.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            copy.Click += async (_, _) => await RunAsync(item.Copy, "已复制");
            copy.ToolTip = item.Preview;
            _rows.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 4), Children = { pin, copy } });
        }
        if (_rows.Children.Count == 0 && _snapshot is not null)
            _rows.Children.Add(ExpandedTabs.Label(_onlyPinned ? "暂无固定条目" : "复制文字、图片或文件后会显示在这里", 12, IslandTheme.Muted));
    }

    private async Task RunAsync(IslandAction action, string? success = null)
    {
        try
        {
            await action.ExecuteAsync(default);
            if (success is not null) _status.Text = success;
        }
        catch (Exception) { _status.Text = "操作失败，请重试"; }
    }
}
