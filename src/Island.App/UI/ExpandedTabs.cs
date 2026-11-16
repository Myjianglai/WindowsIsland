using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Island.App.Persistence;
using Island.App.Providers;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class ExpandedTabs : DockPanel
{
    private const string Muted = IslandTheme.Muted;
    private readonly Border _page = new();
    private readonly SystemDashboard _home = new();
    private readonly StackPanel _music = new();
    private readonly ClipboardHistoryView _clipboard = new();
    private readonly Grid _clipboardPage = new();
    private readonly Border _shelfColumn = new();
    private readonly Border _shelfPage = new();
    private readonly StackPanel _quotas = new();
    private readonly DeviceBatteryView _devices = new();
    private readonly FileShelf _shelf = new();
    private readonly TodoView _todo = new();
    private readonly Dictionary<IslandPage, (Button Button, FrameworkElement Page)> _pages = new();
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _headerClock = Label("", 13, Muted);
    private readonly List<(TextBlock Label, DateTimeOffset? Reset)> _resets = [];
    private IslandPage[] _enabled = [];
    private readonly TextBlock _title = Label("请在设置中启用音乐", 14);
    private readonly TextBlock _artist = Label("", 12, Muted);
    private readonly Image _cover = new() { Width = 44, Height = 44, Stretch = Stretch.UniformToFill };
    private readonly Slider _progress = new() { Height = 20, Maximum = 1, IsMoveToPointEnabled = true, Focusable = false, ToolTip = "播放进度" };
    private readonly Slider _volume = new() { Height = 20, Maximum = 1, IsMoveToPointEnabled = true, Focusable = false, ToolTip = "系统音量" };
    private readonly TextBlock _volumeValue = Label("系统音量", 11, Muted);
    private readonly TextBlock _previousLyric = Label("", 13, Muted);
    private readonly TextBlock _lyric = Label("暂无同步歌词", 16);
    private readonly TextBlock _nextLyric = Label("", 13, Muted);
    private readonly TextBlock _time = Label("", 11, Muted);
    private readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MediaSnapshot? _media;
    private IReadOnlyList<IslandAction> _mediaActions = [];
    private IslandPreferences _preferences = new();
    private QuotaSnapshot _quotaSnapshot = new([]);
    private IslandPage _selectedPage = IslandPage.Quota;
    private MediaSnapshot? _seeking;
    private string? _seekTrack;
    private bool _volumeDragging;
    private long _lastVolumeRead;

    public bool IsEditing => _progress.IsMouseCaptureWithin || _volume.IsMouseCaptureWithin || _todo.IsEditing;
    public bool IsEditingTodo => _todo.IsEditing;
    public event Action<bool> TodoEditingChanged
    {
        add => _todo.EditingChanged += value;
        remove => _todo.EditingChanged -= value;
    }
    public void CheckTodoReminders(Func<IslandEvent, bool> publish) => _todo.CheckReminders(DateTimeOffset.Now, publish);
    public bool HasMusic => _media is not null;
    public bool Playing => _media is { IsPlaying: true };
    public string TrackTitle => _title.Text;
    public ImageSource? CoverArt => _cover.Source;
    public bool HasLyrics => _preferences.Pages.Any(page => page.Page == IslandPage.Music && page.Enabled)
        && _media?.Lyrics is { Count: > 0 };
    public string? CurrentLyric
    {
        get
        {
            if (_media is not { HasPlaybackPosition: true, Lyrics: { Count: > 0 } lines }) return null;
            int index = LocalLyrics.IndexAt(lines, PlaybackPosition(_media));
            return index >= 0 ? lines[index].Text : null;
        }
    }

    public ExpandedTabs(Action openSettings, Action? refreshQuota)
    {
        Margin = new Thickness(14, 9, 14, 10);
        var gripMark = new Border
        {
            Width = 30, Height = 3, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center
        };
        gripMark.SetResourceReference(Border.BackgroundProperty, IslandTheme.Edge);
        var grip = new Border
        {
            Height = 14,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "拖到屏幕边缘",
            Child = gripMark
        };
        SetDock(grip, Dock.Top);
        Children.Add(grip);

        AddPage(IslandPage.Overview, _home);
        AddPage(IslandPage.Music, _music);
        _clipboardPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });
        _clipboardPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
        _clipboardPage.Children.Add(_clipboard);
        _shelfColumn.Margin = new Thickness(12, 0, 0, 0);
        _shelfColumn.Padding = new Thickness(10, 0, 0, 0);
        _shelfColumn.BorderThickness = new Thickness(1, 0, 0, 0);
        _shelfColumn.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
        Grid.SetColumn(_shelfColumn, 1);
        _clipboardPage.Children.Add(_shelfColumn);
        AddPage(IslandPage.Clipboard, _clipboardPage);
        AddPage(IslandPage.Quota, _quotas);
        AddPage(IslandPage.Devices, _devices);
        AddPage(IslandPage.Shelf, _shelfPage);
        AddPage(IslandPage.Todo, _todo);
        var refresh = IconButton("刷新额度", PanelIcons.Refresh());
        refresh.IsEnabled = refreshQuota is not null;
        refresh.Click += (_, _) => refreshQuota?.Invoke();
        var settingsButton = IconButton("设置", PanelIcons.Settings());
        settingsButton.Click += (_, _) => openSettings();
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Children = { refresh, settingsButton } };
        var nav = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = _navigation };
        nav.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _headerClock.HorizontalAlignment = HorizontalAlignment.Center;
        _headerClock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumnSpan(_headerClock, 3);
        _headerClock.IsHitTestVisible = false;
        header.SizeChanged += (_, e) => _headerClock.Visibility = e.NewSize.Width >= 430 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(commands, 2);
        header.Children.Add(nav);
        header.Children.Add(_headerClock);
        header.Children.Add(commands);
        SetDock(header, Dock.Top);
        Children.Add(header);
        _page.Margin = new Thickness(2, 9, 2, 0);
        Children.Add(_page);

        var artwork = new Border
        {
            Width = 44, Height = 44, Child = _cover, CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 9, 0)
        };
        artwork.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        DockPanel.SetDock(artwork, Dock.Left);
        var transport = new StackPanel { Margin = new Thickness(0, 0, 17, 0) };
        transport.Children.Add(new DockPanel
        {
            Children = { artwork, new StackPanel { Children = { _title, _artist } } }
        });
        var timeline = new DockPanel { Children = { _time, _progress } };
        _progress.SetResourceReference(StyleProperty, "IslandSlider");
        _volume.SetResourceReference(StyleProperty, "IslandSlider");
        DockPanel.SetDock(_time, Dock.Right);
        _time.Margin = new Thickness(8, 1, 0, 0);
        timeline.Margin = new Thickness(0, 8, 0, 0);
        transport.Children.Add(timeline);
        transport.Children.Add(_controls);
        var volume = new DockPanel { Margin = new Thickness(0, 4, 0, 1), Children = { _volumeValue, _volume } };
        DockPanel.SetDock(_volumeValue, Dock.Right);
        transport.Children.Add(volume);
        _lyric.FontWeight = FontWeights.SemiBold;
        _lyric.Margin = new Thickness(0, 12, 0, 12);
        var verses = new StackPanel { Children = { _previousLyric, _lyric, _nextLyric }, VerticalAlignment = VerticalAlignment.Center };
        var lyrics = new Border { Padding = new Thickness(17, 6, 0, 6), BorderThickness = new Thickness(1, 0, 0, 0), Child = verses };
        lyrics.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
        var musicLayout = new Grid { Margin = new Thickness(4, 5, 4, 0) };
        musicLayout.ColumnDefinitions.Add(new ColumnDefinition());
        musicLayout.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(lyrics, 1);
        musicLayout.Children.Add(transport);
        musicLayout.Children.Add(lyrics);
        _music.Children.Add(musicLayout);
        _progress.PreviewMouseLeftButtonDown += (_, _) =>
        {
            _seeking = _media;
            _seekTrack = TrackLabel;
        };
        _progress.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || _seeking is not { } playback) return;
            _seeking = null;
            if (_seekTrack != TrackLabel || _media is not { Seek: { } seek } current) return;
            var position = TimeSpan.FromSeconds(_progress.Value * playback.Duration.TotalSeconds);
            try
            {
                await seek(position, default);
                if (ReferenceEquals(_media, current))
                {
                    _media = current with { Position = position, SampledAt = DateTimeOffset.UtcNow };
                    UpdateProgress();
                }
            }
            catch (Exception) { _artist.Text = "播放器暂时无法调整进度"; }
        }), true);
        _progress.LostMouseCapture += (_, _) => _seeking = null;
        _volume.PreviewMouseLeftButtonDown += (_, _) => _volumeDragging = true;
        _volume.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !_volumeDragging) return;
            if (!AudioVolume.Write(_volume.Value)) _volumeValue.Text = "音量不可用";
            _volumeDragging = false;
        }), true);
        _volume.LostMouseCapture += (_, _) => _volumeDragging = false;
        _volume.ValueChanged += (_, _) => _volumeValue.Text = $"系统音量 {_volume.Value * 100:0}%";
        _timer.Tick += (_, _) => RefreshVisiblePage();
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        _quotas.Children.Add(Label("请在设置中添加额度账号", 13, Muted));
        ApplyPreferences(_preferences);
    }

    private string TrackLabel => $"{_title.Text}\n{_artist.Text}";

    public void ApplyPreferences(IslandPreferences preferences)
    {
        bool redrawQuotas = preferences.ShowRemaining != _preferences.ShowRemaining || preferences.Use24HourClock != _preferences.Use24HourClock;
        _preferences = preferences;
        if (redrawQuotas) SetQuota(_quotaSnapshot);
        _navigation.Children.Clear();
        var enabled = preferences.Pages.Where(page => page.Enabled).ToArray();
        _enabled = enabled.Select(item => item.Page).ToArray();
        foreach (var item in enabled)
        {
            _navigation.Children.Add(_pages[item.Page].Button);
        }
        if (enabled.Length == 0)
        {
            _page.Child = Label("功能页面已关闭，可在托盘设置中重新启用。", 12, Muted);
            _timer.Stop();
            return;
        }
        SelectPage(enabled.Any(item => item.Page == _selectedPage) ? _selectedPage : enabled[0].Page);
    }

    public bool HasShelf => _enabled.Contains(IslandPage.Shelf);
    public int ShelfCount => _shelf.Count;
    public event Action ShelfChanged
    {
        add => _shelf.CountChanged += value;
        remove => _shelf.CountChanged -= value;
    }

    public void AcceptFiles(string[] paths)
    {
        _shelf.AddFiles(paths);
        if (_selectedPage != IslandPage.Clipboard) SelectPage(IslandPage.Shelf);
    }

    private void AddPage(IslandPage key, FrameworkElement page)
    {
        var title = GeneralSettings.PageLabel(key);
        var button = IconButton(title, PanelIcons.Page(key));
        System.Windows.Automation.AutomationProperties.SetName(button, title + "标签页");
        button.Click += (_, _) => SelectPage(key);
        _pages.Add(key, (button, page));
    }

    private void SelectPage(IslandPage key)
    {
        if (key != IslandPage.Todo) _todo.CancelEditing();
        _selectedPage = key;
        _shelfColumn.Child = null;
        _shelfPage.Child = null;
        if (key == IslandPage.Clipboard && HasShelf)
            _shelfColumn.Child = _shelf;
        else if (key == IslandPage.Shelf)
            _shelfPage.Child = _shelf;
        _clipboardPage.ColumnDefinitions[1].Width = HasShelf ? new GridLength(42, GridUnitType.Star) : new GridLength(0);
        var selected = _pages[key];
        _page.Child = selected.Page;
        foreach (Button button in _navigation.Children)
        {
            button.SetResourceReference(Button.ForegroundProperty, ReferenceEquals(button, selected.Button) ? IslandTheme.Accent : Muted);
            button.SetResourceReference(BackgroundProperty, ReferenceEquals(button, selected.Button) ? IslandTheme.Surface : IslandTheme.Tile);
            if (button.Content is System.Windows.Shapes.Path icon)
                icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                    ReferenceEquals(button, selected.Button) ? IslandTheme.Accent : Muted);
        }
        UpdateTimer();
    }

    public void SetSystemMetrics(SystemMetrics? metrics) => _home.Update(metrics);

    public static string MetricsText(SystemMetrics metrics) =>
        $"CPU {Percent(metrics.Cpu)} · 内存 {Percent(metrics.Memory)} · ↓{Rate(metrics.Download)} ↑{Rate(metrics.Upload)}";

    private static string Percent(double? value) => value is { } known ? $"{known:0}%" : "—";
    private static string Rate(double? value) => value switch
    {
        >= 1048576 => $"{value / 1048576:0.#}M/s", >= 1024 => $"{value / 1024:0.#}K/s",
        { } known => $"{known:0}B/s", _ => "—"
    };

    public void SetDevices(IslandEvent message) => _devices.Update(message);

    public void SetClipboard(IslandEvent message) => _clipboard.Update(message);

    public void SetQuota(QuotaSnapshot snapshot)
    {
        _quotaSnapshot = snapshot;
        _quotas.Children.Clear();
        _resets.Clear();
        if (snapshot.Accounts.Count == 0)
        {
            _quotas.Children.Add(Label("请在设置中添加额度账号", 13, Muted));
            return;
        }
        var rows = new StackPanel();
        foreach (var account in snapshot.Accounts)
        {
            var heading = Label(account.Name, 19);
            heading.FontWeight = FontWeights.SemiBold;
            var accountHeader = new StackPanel { Orientation = Orientation.Horizontal, Children = { heading } };
            string? plan = account.Details.FirstOrDefault(detail => detail.StartsWith("套餐 ", StringComparison.Ordinal));
            if (plan is not null)
            {
                var pill = new Border { CornerRadius = new CornerRadius(5), Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center, Child = Label(plan[3..], 11, Muted) };
                pill.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
                accountHeader.Children.Add(pill);
            }
            rows.Children.Add(accountHeader);
            var columns = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 7, 0, 4) };
            foreach (var quota in account.Metrics ?? [])
            {
                var value = QuotaDisplay.Value(quota.Used, _preferences.ShowRemaining);
                var column = new StackPanel { Margin = new Thickness(0, 0, 22, 12) };
                column.Children.Add(Label(quota.Label, 13, Muted));
                var metric = new DockPanel { Margin = new Thickness(0, 4, 0, 5) };
                var reset = Label(ResetText(quota.Reset), 11, Muted);
                reset.VerticalAlignment = VerticalAlignment.Bottom;
                reset.Margin = new Thickness(4, 0, 0, 3);
                DockPanel.SetDock(reset, Dock.Right);
                metric.Children.Add(reset);
                _resets.Add((reset, quota.Reset));
                var amount = Label(value is { } number ? $"{number:0.#}%" : "—", 29);
                amount.FontWeight = FontWeights.SemiBold;
                var unit = Label(value is null ? "未知" : _preferences.ShowRemaining ? "剩余" : "已用", 11, Muted);
                unit.Margin = new Thickness(4, 0, 0, 4);
                unit.VerticalAlignment = VerticalAlignment.Bottom;
                metric.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { amount, unit } });
                column.Children.Add(metric);
                if (value is { } known)
                {
                    var bar = new ProgressBar { Height = 5, Maximum = 100, Value = Math.Clamp(known, 0, 100), BorderThickness = new Thickness(0) };
                    bar.SetResourceReference(StyleProperty, "QuotaProgress");
                    bar.SetResourceReference(ProgressBar.ForegroundProperty, IslandTheme.Account(account.Color));
                    bar.SetResourceReference(ProgressBar.BackgroundProperty, IslandTheme.Edge);
                    column.Children.Add(bar);
                }
                columns.Children.Add(column);
            }
            if (columns.Children.Count > 0) rows.Children.Add(columns);
            foreach (var detail in account.Details.Where(detail => detail != plan))
                rows.Children.Add(Label(detail, 12, Muted));
            var divider = new Border { Height = 1, Margin = new Thickness(0, 7, 6, 12) };
            divider.SetResourceReference(Border.BackgroundProperty, IslandTheme.Edge);
            rows.Children.Add(divider);
        }
        _quotas.Children.Add(Scrollable(rows, 210));
    }

    private static string ResetText(DateTimeOffset? reset)
    {
        if (reset is null) return "重置未知";
        var left = reset.Value - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "等待更新";
        return left.TotalDays >= 1 ? $"{left.Days} 天 {left.Hours} 小时后"
            : left.TotalHours >= 1 ? $"{left.Hours} 时 {left.Minutes} 分后" : $"{Math.Max(1, left.Minutes)} 分后";
    }


    private static ScrollViewer Scrollable(Panel content, double height) => new()
    {
        Content = content, MaxHeight = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    public void SetMedia(IslandEvent message)
    {
        _title.Text = message.Title;
        _title.ToolTip = message.Title;
        _artist.Text = string.Join(" · ", new[] { message.Subtitle, message.Media?.Player }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var artwork = message.Media?.Artwork ?? default;
        if (_media is null || !_media.Artwork.Equals(artwork))
        {
            _cover.Source = DecodeArtwork(artwork);
        }
        _media = message.Media;
        bool rebuildControls = !_mediaActions.Select(action => action.Label).SequenceEqual(message.Actions.Select(action => action.Label));
        _mediaActions = message.Actions;
        if (rebuildControls) BuildMediaControls();
        UpdateTimer();
    }

    private void BuildMediaControls()
    {
        _controls.Children.Clear();
        for (int index = 0; index < _mediaActions.Count; index++)
        {
            int slot = index;
            var action = _mediaActions[slot];
            var symbol = action.Label switch { "上一首" => "⏮", "下一首" => "⏭", "播放" => "▶", "暂停" => "Ⅱ", _ => action.Label };
            var button = MakeButton(symbol);
            button.ToolTip = action.Label;
            System.Windows.Automation.AutomationProperties.SetName(button, action.Label);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    await _mediaActions[slot].ExecuteAsync(default);
                }
                catch (Exception)
                {
                    _artist.Text = "播放器暂时无法执行这个操作";
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            _controls.Children.Add(button);
        }
    }

    private void UpdateTimer()
    {
        RefreshVisiblePage();
        _timer.IsEnabled = IsLoaded;
    }

    private void RefreshVisiblePage()
    {
        var currentTime = DateTime.Now;
        var clockFormat = _preferences.Use24HourClock ? "HH:mm" : "h:mm tt";
        if (_selectedPage == IslandPage.Todo)
            clockFormat = _preferences.Use24HourClock ? "M月d日 ddd  HH:mm:ss" : "M月d日 ddd  h:mm:ss tt";
        _headerClock.Text = currentTime.ToString(clockFormat, CultureInfo.GetCultureInfo("zh-CN"));
        if (ReferenceEquals(_page.Child, _quotas))
            foreach (var (label, reset) in _resets) label.Text = ResetText(reset);
        if (!ReferenceEquals(_page.Child, _music))
        {
            return;
        }
        UpdateProgress();
        if (!_volumeDragging && System.Diagnostics.Stopwatch.GetElapsedTime(_lastVolumeRead) >= TimeSpan.FromSeconds(2))
        {
            _lastVolumeRead = System.Diagnostics.Stopwatch.GetTimestamp();
            var current = AudioVolume.Read();
            _volume.IsEnabled = current.HasValue;
            if (current.HasValue)
            {
                _volume.Value = current.Value;
                _volumeValue.Text = $"系统音量 {current * 100:0}%";
            }
            else _volumeValue.Text = "音量不可用";
        }
    }

    private void UpdateProgress()
    {
        if (_media is not { Duration.Ticks: > 0, HasPlaybackPosition: true } media)
        {
            _progress.Visibility = Visibility.Collapsed;
            _time.Text = "";
        }
        else
        {
            var position = PlaybackPosition(media);
            _progress.Visibility = Visibility.Visible;
            _progress.IsEnabled = media.Seek is not null;
            if (_seeking is null)
            {
                _progress.Value = position.TotalSeconds / media.Duration.TotalSeconds;
            }
            _time.Text = $"{FormatTime(position)} / {FormatTime(media.Duration)}";
        }
        var lines = _media?.Lyrics;
        if (_media is { HasPlaybackPosition: true } && lines is { Count: > 0 })
        {
            int index = LocalLyrics.IndexAt(lines, PlaybackPosition(_media));
            _previousLyric.Text = index > 0 ? lines[index - 1].Text : "";
            _lyric.Text = index >= 0 ? lines[index].Text : "";
            _nextLyric.Text = index + 1 < lines.Count ? lines[index + 1].Text : "";
        }
        else
        {
            _previousLyric.Text = _nextLyric.Text = "";
            _lyric.Text = _media is { HasPlaybackPosition: false, Lyrics.Count: > 0 }
                ? "暂未读到网易云播放进度" : _media?.Player == "网易云音乐" && !_preferences.NeteaseOnlineLyrics
                ? "可在设置中启用网易云自动获取歌词" : "暂无同步歌词";
        }
    }

    private static TimeSpan PlaybackPosition(MediaSnapshot media)
    {
        var seconds = media.Position.TotalSeconds;
        if (media.IsPlaying)
        {
            seconds += Math.Max(0, (DateTimeOffset.UtcNow - media.SampledAt).TotalSeconds) * media.PlaybackRate;
        }
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, media.Duration.Ticks > 0 ? media.Duration.TotalSeconds : TimeSpan.FromDays(7).TotalSeconds));
    }

    internal static BitmapImage? DecodeArtwork(ReadOnlyMemory<byte> artwork)
    {
        if (artwork.IsEmpty)
        {
            return null;
        }
        try
        {
            using var stream = new MemoryStream(artwork.ToArray(), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 100;
            image.StreamSource = stream;
            image.EndInit();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    internal static TextBlock Label(string text, double size, string color = IslandTheme.Ink)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, color);
        return label;
    }

    internal static Button MakeButton(string text)
    {
        var button = new Button { Content = text, Focusable = false, Margin = new Thickness(0, 0, 6, 0) };
        button.SetResourceReference(StyleProperty, "IslandButton");
        return button;
    }

    private static Button IconButton(string title, FrameworkElement icon)
    {
        var button = MakeButton("");
        button.Content = icon;
        button.Width = 29;
        button.Height = 29;
        button.Padding = new Thickness(5);
        button.Margin = new Thickness(1, 0, 1, 0);
        button.ToolTip = title;
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        return button;
    }
}
