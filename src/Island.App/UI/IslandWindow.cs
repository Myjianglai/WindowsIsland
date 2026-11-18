using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Island.App.Presentation;
using Island.App.Persistence;
using Island.App.Providers;
using Island.Core.Events;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Island.App.UI;

internal sealed class IslandWindow : Window
{
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(175);
    private const int ExtendedStyle = -20;
    private const int NoActivateStyle = 0x08000000;
    private const uint MoveOnlyWithoutActivation = 0x0015;
    private const double StripLength = 100;
    private const double StripThickness = 5;
    private const double BaseWidth = 454;
    private const double BaseHeight = 424;
    private const double PanelWidth = 650;
    private const double PanelHeight = 290;
    private readonly Border _surface;
    private readonly Grid _host;
    private readonly Path _wings = new() { IsHitTestVisible = false };
    private System.Windows.Rect _lastWingBounds;
    private DockEdge _lastWingEdge;
    private readonly ExpandedTabs _tabs;
    private IntPtr _handle;
    private IntPtr _beforeTodoEditing;
    private bool _webKeyboard;
    private System.Drawing.Point _dragStart;
    private System.Drawing.Point _windowStart;
    private bool _dragged;
    private int _revision;
    private DockEdge _edge;
    private IslandPreferences _preferences = new();
    private int _shift;
    private int _shiftIndex;
    private long _lastShift = Stopwatch.GetTimestamp();
    private long _checkedAvoidance;
    private IntPtr _foreground;
    private string? _foregroundName;
    private bool _avoided;
    private bool _fullscreen;
    private bool _maximizedStrip;
    private Dictionary<(Guid, string), double> _lastUsed = new();
    private IslandEvent? _banner;
    private TextBlock? _compactLyrics;
    private TextBlock? _compactMetrics;
    private TextBlock? _compactClock;
    private long _clockSecond = -1;
    private Image? _compactCover;
    private Rectangle[] _rhythm = [];
    private double _peak;
    private long _peakAt;
    private bool _samplingAudio;
    private bool _inTaskbar;
    private double _taskbarThickness;
    private SystemMetrics? _metrics;
    private DeviceBattery[] _deviceLevels = [];
    private TodoPreview? _todoPreview;
    private IslandState _state;
    private QuotaAccountStatus[] _pinned = [];

    public event Action? IslandClicked;
    public event Action<IReadOnlyList<DeviceBattery>>? DevicesChanged;
    public IReadOnlyList<DeviceBattery> Devices => _deviceLevels;
    public event Action<IslandPreferences>? DockChanged;
    public bool IsDragging => _surface.IsMouseCaptured || _tabs.IsEditing;
    public bool IsEditingTodo => _tabs.IsEditingTodo;
    public void OpenClipboard() => _tabs.OpenClipboard();
    private (string Title, string Source, string State, int Count)? _activeAgent;
    private IslandEvent? _compactMessage;

    public void RecordMessage(IslandEvent message)
    {
        _tabs.Activity.Record(message);
    }

    public void CheckAgentActivity(Func<IslandEvent, bool> publish)
    {
        _tabs.Activity.Poll(publish);
        var running = _tabs.Activity.Running;
        (string Title, string Source, string State, int Count)? next = ActivityPanel.Options.Compact && running.Length > 0
            ? (running[0].Title, running[0].Source, running[0].State, running.Length) : null;
        var message = _tabs.Activity.CompactMessage;
        if (next == _activeAgent && ReferenceEquals(message, _compactMessage)) return;
        _activeAgent = next;
        _compactMessage = message;
        RefreshCompact();
    }
    public void CheckTodoReminders(Func<IslandEvent, bool> publish)
    {
        _tabs.ExpireShelf();
        var now = DateTimeOffset.Now;
        if (ActivityPanel.Options.Todo) _tabs.CheckTodoReminders(now, publish);
        UpdateTodoPreview(now);
    }
    public bool IsAvoided => _avoided || _fullscreen;

    public IslandWindow(Action openSettings, Action? refreshQuota = null, Action? openMessageSettings = null)
    {
        Title = "Windows 灵动岛";
        Width = BaseWidth;
        Height = BaseHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Island;component/UI/IslandChrome.xaml", UriKind.Relative)
        });
        IslandTheme.Apply(Resources, _preferences.Skin);

        _tabs = new ExpandedTabs(openSettings, refreshQuota);
        _tabs.Activity.OpenMessageSettings = openMessageSettings ?? openSettings;
        _tabs.Activity.UnreadChanged += () =>
        {
            _compactMessage = _tabs.Activity.CompactMessage;
            RefreshCompact();
        };
        _tabs.CompactLyricsToggled += () =>
        {
            bool enabled = _preferences.ShowCompactMusic && _preferences.CompactLyrics && _preferences.CompactMusic == MusicPreview.Lyric;
            DockChanged?.Invoke(_preferences with
            {
                ShowCompactMusic = true, CompactLyrics = true, CompactMusic = enabled ? MusicPreview.Track : MusicPreview.Lyric
            });
        };
        _tabs.TodoEditingChanged += SetTodoKeyboardFocus;
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) _tabs.ReleaseTodoEditorFocus();
        }), handledEventsToo: true);
        Deactivated += (_, _) => _tabs.ReleaseTodoEditorFocus(outsideWindow: true);
        _tabs.Xiaohongshu.KeyboardRequested += () =>
        {
            if (_webKeyboard) return;
            _webKeyboard = true;
            SetTodoKeyboardFocus(true);
        };
        _tabs.Xiaohongshu.Hidden += () =>
        {
            if (!_webKeyboard) return;
            _webKeyboard = false;
            SetTodoKeyboardFocus(false);
        };
        _tabs.ExpandedLayoutChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state == IslandState.Expanded) Display(IslandState.Expanded, null);
        }));
        _todoPreview = _tabs.GetTodoPreview(DateTimeOffset.Now);
        _tabs.TodoItemsChanged += () => UpdateTodoPreview(DateTimeOffset.Now);
        _tabs.ShelfChanged += () =>
        {
            if (_preferences.CompactShelfCount) RefreshCompact();
        };
        _surface = new Border
        {
            Width = StripLength,
            Height = StripThickness,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(19),
            ClipToBounds = true,
        };
        _surface.SetResourceReference(Border.BackgroundProperty, IslandTheme.Surface);
        _surface.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
        _surface.AllowDrop = true;
        _surface.PreviewDragOver += (_, e) =>
        {
            e.Effects = _tabs.HasShelf && !_avoided && _state != IslandState.Banner && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? e.AllowedEffects & DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        _surface.PreviewDrop += (_, e) =>
        {
            if (_tabs.HasShelf && !_avoided && _state != IslandState.Banner && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                _tabs.AcceptFiles(paths);
                IslandClicked?.Invoke();
                e.Effects = e.AllowedEffects & DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        _wings.SetResourceReference(Shape.FillProperty, IslandTheme.Surface);
        _host = new Grid { Children = { _wings, _surface } };
        Content = _host;
        _surface.LayoutUpdated += (_, _) => UpdateNotchWings();
        _surface.PreviewMouseLeftButtonDown += BeginDrag;
        _surface.PreviewMouseMove += MoveDrag;
        _surface.PreviewMouseLeftButtonUp += EndDrag;
        _surface.MouseRightButtonUp += (_, e) =>
        {
            if (_state != IslandState.Expanded || !_preferences.RightClickSettings) return;
            openSettings();
            e.Handled = true;
        };
        _surface.MouseWheel += (_, e) =>
        {
            if (_state != IslandState.Expanded || _preferences.WheelAction == IslandWheelAction.None || e.Delta == 0) return;
            _tabs.HandleWheel(_preferences.WheelAction, Math.Sign(e.Delta));
            e.Handled = true;
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _tabs.Xiaohongshu.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(_handle, ExtendedStyle, GetWindowLong(_handle, ExtendedStyle) | NoActivateStyle);
    }

    private void SetTodoKeyboardFocus(bool editing)
    {
        if (_handle == IntPtr.Zero) return;
        int style = GetWindowLong(_handle, ExtendedStyle);
        if (editing)
        {
            _beforeTodoEditing = GetForegroundWindow();
            SetWindowLong(_handle, ExtendedStyle, style & ~NoActivateStyle);
            Activate();
        }
        else
        {
            SetWindowLong(_handle, ExtendedStyle, style | NoActivateStyle);
            if (GetForegroundWindow() == _handle && _beforeTodoEditing != IntPtr.Zero && _beforeTodoEditing != _handle)
                SetForegroundWindow(_beforeTodoEditing);
            _beforeTodoEditing = IntPtr.Zero;
        }
    }

    public void ResetPosition()
    {
        DockChanged?.Invoke(_preferences with { Edge = DockEdge.Top, Position = 50, Monitor = null });
    }

    public void ApplyPreferences(IslandPreferences preferences)
    {
        if (_preferences.Skin != preferences.Skin)
        {
            IslandTheme.Apply(Resources, preferences.Skin);
        }
        _preferences = preferences.Normalize();
        _todoPreview = _preferences.CompactTodo ? _tabs.GetTodoPreview(DateTimeOffset.Now) : null;
        var scale = _preferences.Scale / 100d;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        _surface.LayoutTransform = new ScaleTransform(scale, scale);
        _tabs.ApplyPreferences(_preferences);
        _shiftIndex = _shift = 0;
        _lastShift = Stopwatch.GetTimestamp();
        _checkedAvoidance = 0;
        _foreground = IntPtr.Zero;
        PlaceDock(_state == IslandState.Compact && !_awaitingCollapse);
        if (!_awaitingCollapse)
        {
            Display(_state, _banner);
        }
    }

    public bool IsPointerNear(double margin)
    {
        if (!IsVisible || !_surface.IsLoaded || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
        {
            return false;
        }
        var cursor = Forms.Cursor.Position;
        if (_state == IslandState.Compact && !_preferences.AlwaysOnTop && !_inTaskbar && WindowFromPoint(cursor) != _handle)
            return false;
        Point point = _surface.PointFromScreen(new Point(cursor.X, cursor.Y));
        double bridge = _state == IslandState.Expanded && _preferences.InTaskbar ? _taskbarThickness : 0;
        return point.X >= -margin - (_edge == DockEdge.Left ? bridge : 0)
            && point.X <= _surface.ActualWidth + margin + (_edge == DockEdge.Right ? bridge : 0)
            && point.Y >= -margin - (_edge == DockEdge.Top ? bridge : 0)
            && point.Y <= _surface.ActualHeight + margin + (_edge == DockEdge.Bottom ? bridge : 0);
    }

    public void SetMedia(IslandEvent message)
    {
        bool hadPreview = ShowsMusic;
        bool hadLyrics = UsesCompactLyrics;
        var previousTitle = _tabs.TrackTitle;
        bool hadCover = _tabs.CoverArt is not null;
        _tabs.SetMedia(message);
        if (hadPreview != ShowsMusic || hadLyrics != UsesCompactLyrics
            || (ShowsMusic && (hadCover != (_tabs.CoverArt is not null)
                || (ShowsMusicText && !UsesCompactLyrics && previousTitle != _tabs.TrackTitle))))
        {
            RefreshCompact();
        }
        if (_compactCover is not null) _compactCover.Source = _tabs.CoverArt;
    }
    public void SetClipboard(IslandEvent message) => _tabs.SetClipboard(message);
    public void SetDevices(IslandEvent message)
    {
        _tabs.SetDevices(message);
        var devices = message.Devices?.ToArray() ?? [];
        if (_deviceLevels.SequenceEqual(devices)) return;
        _deviceLevels = devices;
        DevicesChanged?.Invoke(devices);
        if (_preferences.CompactDevices) RefreshCompact();
    }

    private void UpdateTodoPreview(DateTimeOffset now)
    {
        if (!_preferences.CompactTodo) return;
        var next = _tabs.GetTodoPreview(now);
        if (next == _todoPreview) return;
        _todoPreview = next;
        RefreshCompact();
    }
    public void SetSystemMetrics(SystemMetrics? metrics)
    {
        bool changed = (_metrics is null) != (metrics is null);
        _metrics = metrics;
        _tabs.SetSystemMetrics(metrics);
        if (changed)
        {
            RefreshCompact();
        }
        else if (metrics is not null && _compactMetrics is not null)
        {
            _compactMetrics.Text = ExpandedTabs.MetricsText(metrics);
        }
    }

    public IslandEvent? SetQuota(QuotaSnapshot snapshot)
    {
        _tabs.SetQuota(snapshot);
        _pinned = snapshot.Accounts.Where(account => account.Pinned).Take(3).ToArray();
        RefreshCompact();
        var used = new Dictionary<(Guid, string), double>();
        (QuotaAccountStatus Account, QuotaMetric Metric, int Threshold)? warning = null;
        foreach (var account in _pinned.Where(account => account.Id != Guid.Empty))
        {
            foreach (var metric in account.Metrics ?? [])
            {
                if (metric.Used is not { } current) continue;
                var key = (account.Id, metric.Key);
                used[key] = current;
                if (!_lastUsed.TryGetValue(key, out double previous)) continue;
                int threshold = current >= 90 && previous < 90 ? 90 : current >= 70 && previous < 70 ? 70 : 0;
                if (threshold > (warning?.Threshold ?? 0))
                    warning = (account, metric, threshold);
            }
        }
        _lastUsed = used;
        return _preferences.QuotaWarnings && warning is { } notice
            ? new IslandEvent("AI 额度", IslandEventType.Notification, $"{notice.Account.Name} 已使用 {notice.Metric.Used:0.#}%",
                $"{notice.Metric.Label} · {notice.Threshold}% 阈值", timeout: TimeSpan.FromSeconds(5))
            : null;
    }

    private void RefreshCompact()
    {
        if (_state != IslandState.Compact) return;
        if (_awaitingCollapse) { _compactRefreshPending = true; return; }
        Display(IslandState.Compact, null);
    }

    private bool _awaitingCollapse;
    private bool _compactRefreshPending;

    public void Display(IslandState state, IslandEvent? message, Action? completed = null)
    {
        var previousState = _state;
        _state = state;
        _awaitingCollapse = completed is not null;
        _banner = message;
        var visualState = _avoided ? IslandState.Compact : state;
        if (visualState != IslandState.Expanded || previousState != IslandState.Expanded || !_tabs.IsWebPage)
            _tabs.Xiaohongshu.SetPanelReady(false);
        if (visualState == IslandState.Expanded && previousState == IslandState.Compact)
            _tabs.SelectOpeningPage();
        bool returnToTaskbar = visualState == IslandState.Compact && (previousState != IslandState.Compact || completed is not null);
        double panelWidth = ExpandedWidth();
        double panelHeight = visualState == IslandState.Expanded
            ? _tabs.IsWebPage ? ExpandedWebHeight() : _tabs.IsTodoPage ? PanelHeight + 65 : PanelHeight
            : PanelHeight;
        if (visualState == IslandState.Expanded)
        {
            Height = Math.Max(BaseHeight, panelHeight + 24) * _preferences.Scale / 100d;
            Width = Math.Max(Width, (panelWidth + 24) * _preferences.Scale / 100d);
        }
        PlaceDock(visualState == IslandState.Compact && !returnToTaskbar);
        if (visualState != IslandState.Compact || _inTaskbar || _preferences.AlwaysOnTop)
            Topmost = true;
        var compact = visualState == IslandState.Compact ? CompactContent() : null;
        compact?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool vertical = _edge is DockEdge.Left or DockEdge.Right;
        double limit = BaseWidth - 24;
        bool fixedSize = visualState == IslandState.Compact && !_preferences.AutoCompactWidth &&
            !_preferences.Minimal && !_avoided && !_maximizedStrip && _preferences.Shape != IslandShape.Indicator;
        double desiredLength = fixedSize ? _preferences.CompactWidth : (compact?.DesiredSize.Width ?? 0) + 18;
        if ((fixedSize || compact is not null) && DockScreen() is { } screen)
        {
            var work = screen.WorkingArea;
            var dpi = GetTargetDpi(new System.Drawing.Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
            var scale = _preferences.Scale / 100d;
            limit = Math.Max(StripLength, (vertical ? work.Height : work.Width) * 96 / (dpi * scale) - 26);
            double needed = (Math.Min(desiredLength, limit) + 24) * scale;
            if (vertical && needed > Height)
            {
                Height = needed;
                PlaceDock(!returnToTaskbar);
            }
            else if (!vertical && needed > Width)
            {
                Width = needed;
                PlaceDock(!returnToTaskbar);
            }
        }
        double length = compact is null && !fixedSize ? StripLength : Math.Clamp(desiredLength, StripLength, limit);
        double thickness = compact is null && !fixedSize ? StripThickness : _preferences.CompactHeight;
        if (_inTaskbar) thickness = Math.Min(thickness, _taskbarThickness);
        var (width, height) = visualState switch
        {
            IslandState.Expanded => (panelWidth, panelHeight),
            IslandState.Banner => (390d, 144d),
            _ when vertical => (thickness, Math.Min(length, Height / (_preferences.Scale / 100d))),
            _ => (length, thickness)
        };
        _surface.SetResourceReference(Border.BackgroundProperty, visualState == IslandState.Compact && compact is null
            && (_preferences.Shape == IslandShape.Indicator || _maximizedStrip) ? IslandTheme.Edge : IslandTheme.Surface);
        double radius = visualState == IslandState.Compact ? thickness / 2 : 23;
        _surface.CornerRadius = _preferences.Shape != IslandShape.Notch ? new CornerRadius(radius) : _edge switch
        {
            DockEdge.Left => new CornerRadius(0, radius, radius, 0),
            DockEdge.Right => new CornerRadius(radius, 0, 0, radius),
            DockEdge.Bottom => new CornerRadius(radius, radius, 0, 0),
            _ => new CornerRadius(0, 0, radius, radius)
        };
        int revision = ++_revision;
        void ApplyCompactContent()
        {
            if (compact is not null && vertical)
                compact.LayoutTransform = new RotateTransform(_edge == DockEdge.Left ? -90 : 90);
            _surface.Child = compact is null
                ? (_preferences.Shape == IslandShape.Indicator || _maximizedStrip ? IndicatorFill() : null)
                : new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = compact };
        }
        bool updateCompactNow = visualState == IslandState.Compact && previousState == IslandState.Compact && !returnToTaskbar;
        if (updateCompactNow) ApplyCompactContent();
        if (visualState != IslandState.Compact)
        {
            _compactLyrics = null;
            _compactMetrics = null;
            _compactClock = null;
            _compactCover = null;
            _rhythm = [];
            _surface.Child = visualState == IslandState.Expanded
                ? _tabs
                : BannerContent(message!);
        }

        Animate(FrameworkElement.WidthProperty, width);
        Animate(FrameworkElement.HeightProperty, height, () =>
        {
            if (_revision == revision && visualState == IslandState.Expanded && _tabs.IsWebPage)
                _tabs.Xiaohongshu.SetPanelReady(true);
            if (_revision == revision && visualState == IslandState.Compact)
            {
                if (!updateCompactNow) ApplyCompactContent();
                _awaitingCollapse = false;
                Topmost = _preferences.AlwaysOnTop || _inTaskbar;
                bool refresh = _compactRefreshPending;
                _compactRefreshPending = false;
                if (refresh || returnToTaskbar && !IsAvoided && _taskbarThickness > 0)
                    Display(IslandState.Compact, null);
            }
            completed?.Invoke();
        });
    }

    private StackPanel? CompactContent()
    {
        _compactLyrics = null;
        _compactMetrics = null;
        _compactClock = null;
        _compactCover = null;
        _rhythm = [];
        if (_preferences.Minimal || IsAvoided || _maximizedStrip || _preferences.Shape == IslandShape.Indicator)
        {
            return null;
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_tabs.Activity.CompactMessage is { } message)
        {
            AddCompactSummary(content, message.Source, "你收到了一条消息", 0, "AccountGreen");
            return content;
        }
        if (_activeAgent is { } agent)
        {
            AddCompactSummary(content, agent.Source, agent.Title, agent.Count, IslandTheme.Accent,
                agent.State == "待确认");
            return content;
        }
        bool notch = _preferences.Shape == IslandShape.Notch;
        var groups = new Dictionary<NotchItem, StackPanel>();
        Panel Target(NotchItem item)
        {
            if (!notch) return content;
            if (!groups.TryGetValue(item, out var panel))
            {
                panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                groups.Add(item, panel);
            }
            return panel;
        }
        if (_preferences.CompactQuotas)
            foreach (var account in _pinned)
                Target(NotchItem.Quotas).Children.Add(QuotaDisplay.Badge(account, _preferences));
        if (ShowsMusic)
        {
            if (_preferences.ShowMusicCover || _preferences.CompactMusic == MusicPreview.Cover)
            {
                FrameworkElement artwork;
                if (_tabs.CoverArt is { } source)
                    artwork = _compactCover = new Image { Source = source, Width = 27, Height = 27, Stretch = Stretch.UniformToFill };
                else
                    artwork = new TextBlock { Text = "♫", FontSize = 14, TextAlignment = TextAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Resources[IslandTheme.Ink] };
                var cover = new Border { Child = artwork, Width = 27, Height = 27, CornerRadius = new CornerRadius(5), Margin = new Thickness(5, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = _tabs.TrackTitle };
                cover.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
                Target(NotchItem.Music).Children.Insert(0, cover);
            }
            if (_preferences.ShowMusicRhythm || _preferences.CompactMusic == MusicPreview.Rhythm)
            {
                var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 5, 0), ToolTip = _tabs.TrackTitle };
                _rhythm = Enumerable.Range(0, 5).Select(index =>
                {
                    var bar = new Rectangle { Width = 2.5, Height = 4, RadiusX = 1.25, RadiusY = 1.25,
                        Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
                    bar.SetResourceReference(Shape.FillProperty, IslandTheme.Accent);
                    bars.Children.Add(bar);
                    return bar;
                }).ToArray();
                Target(NotchItem.Music).Children.Add(bars);
            }
            if (ShowsMusicText)
            {
                _compactLyrics = Text(MusicPreviewText(), 11);
                if (notch && !UsesCompactLyrics)
                    _compactLyrics.MaxWidth = 260;
                else
                    _compactLyrics.Width = notch ? 220 : 108;
                _compactLyrics.Margin = new Thickness(6, 0, 6, 0);
                _compactLyrics.VerticalAlignment = VerticalAlignment.Center;
                Target(NotchItem.Music).Children.Add(_compactLyrics);
            }
        }
        if (_preferences.CompactSystem && _metrics is not null)
        {
            var metricsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 5, 0) };
            // 保留 BrimDeck PowerIcons.cs 的闪电图形（Apache-2.0）。
            var icon = new Path { Data = Geometry.Parse("M9.2,1.5 L3.8,9 H7.6 L7,14.5 L12.2,7 H8.4 Z"),
                Width = 9, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Shape.FillProperty, IslandTheme.Accent);
            metricsPanel.Children.Add(icon);
            _compactMetrics = Text(ExpandedTabs.MetricsText(_metrics), 10, true);
            if (notch)
                _compactMetrics.MinWidth = 190;
            else
            {
                _compactMetrics.Width = 121;
                _compactMetrics.TextWrapping = TextWrapping.Wrap;
            }
            metricsPanel.Children.Add(_compactMetrics);
            Target(NotchItem.System).Children.Add(metricsPanel);
        }
        if (_preferences.CompactClock)
        {
            _compactClock = Text(ClockText(DateTime.Now), 11);
            if (notch)
                _compactClock.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI");
            _compactClock.Margin = new Thickness(6, 0, 4, 0);
            _compactClock.VerticalAlignment = VerticalAlignment.Center;
            Target(NotchItem.Clock).Children.Add(_compactClock);
        }
        if (_preferences.CompactDevices)
        {
            var devices = BatteryDeviceChoice.Select(_deviceLevels, _preferences.CompactBatteryDevices);
            foreach (var battery in devices)
            {
                var color = DeviceBatteryView.Tone(battery, _preferences.BatteryWarning, _preferences.BatteryCritical, IslandTheme.Muted);
                var name = ExpandedTabs.Label(battery.Name, 10, color);
                name.MaxWidth = 110;
                var level = ExpandedTabs.Label(battery.Level, 10, color);
                level.Margin = new Thickness(4, 0, 0, 0);
                var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 0, 4, 0), ToolTip = $"{battery.Name} · {battery.Level}",
                    Children = { DeviceBatteryView.Gauge(battery, color, 17), name } };
                name.Margin = new Thickness(4, 0, 0, 0);
                if (_preferences.CompactBatteryNumbers) label.Children.Add(level);
                Target(NotchItem.Devices).Children.Add(label);
            }
            if (_preferences.CompactBatteryDevices is null)
            {
                var known = _deviceLevels.Where(device => device.Level != "未知").DistinctBy(BatteryDeviceChoice.From).ToArray();
                if (known.Length > devices.Length)
                {
                    var more = ExpandedTabs.Label($"+{known.Length - devices.Length}", 10, IslandTheme.Muted);
                    more.VerticalAlignment = VerticalAlignment.Center;
                    more.ToolTip = string.Join(Environment.NewLine, known.Select(device => $"{device.Name} · {device.Level}"));
                    Target(NotchItem.Devices).Children.Add(more);
                }
            }
        }
        if (_preferences.CompactShelfCount && _tabs.HasShelf && _tabs.ShelfCount > 0)
        {
            var label = ExpandedTabs.Label($"{_tabs.ShelfCount} 个文件", 10, IslandTheme.Muted);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(6, 0, 3, 0);
            Target(NotchItem.Shelf).Children.Add(label);
        }
        if (ActivityPanel.Options.Todo && _preferences.CompactTodo && _todoPreview is { } todo)
        {
            var label = ExpandedTabs.Label(todo.Text, 11, todo.Overdue ? IslandTheme.Alert : IslandTheme.Ink);
            label.MaxWidth = 200;
            label.Margin = new Thickness(7, 0, 5, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.ToolTip = todo.Description;
            Target(NotchItem.Todo).Children.Add(label);
        }
        if (notch)
        {
            foreach (var item in _preferences.NotchOrder)
            {
                if (groups.TryGetValue(item, out var group))
                    content.Children.Add(group);
            }
        }
        if (_tabs.Activity.UnreadCount is > 0 and int unread)
        {
            var badge = CompactCountBadge(unread);
            badge.Margin = new Thickness(content.Children.Count > 0 ? 7 : 0, 0, 0, 0);
            badge.ToolTip = $"{unread} 条未读消息";
            System.Windows.Automation.AutomationProperties.SetName(badge, $"{unread} 条未读消息");
            content.Children.Add(badge);
        }
        return content.Children.Count > 0 ? content : null;
    }

    private static void AddCompactSummary(StackPanel content, string source, string title, int count, string accent,
        bool waiting = false)
    {
        var dot = ExpandedTabs.Label("●", 11, accent);
        if (waiting) dot.Foreground = SettingsRows.Brush("#F2A742");
        var glyph = ExpandedTabs.Label(source switch { "Codex" => "CX", "Claude" => "CC", "微信" => "微",
            "QQ" => "QQ", "邮箱" => "✉", _ => "◈" }, 10);
        glyph.ToolTip = source;
        var text = ExpandedTabs.Label(title, 13);
        text.MaxWidth = 205;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.FontWeight = FontWeights.SemiBold;
        FrameworkElement sourceIcon = source is "Codex" or "Claude" or "微信" or "QQ" or "邮箱" or "企业微信"
            ? ActivityPanel.SourceIcon(source) : glyph;
        foreach (var label in new FrameworkElement[] { dot, sourceIcon, text })
        {
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 7, 0);
            content.Children.Add(label);
        }
        var divider = ExpandedTabs.Label("╌╌", 12, IslandTheme.Muted);
        divider.Margin = new Thickness(7, 0, 7, 0);
        divider.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(divider);
        if (count > 0) content.Children.Add(CompactCountBadge(count));
    }

    private static Border CompactCountBadge(int count)
    {
        var number = ExpandedTabs.Label(count.ToString(), 11);
        number.Foreground = Brushes.White;
        number.FontWeight = FontWeights.SemiBold;
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = number, MinWidth = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = SettingsRows.Brush("#224777") };
    }

    private FrameworkElement IndicatorFill()
    {
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, IslandTheme.Accent);
        if (_edge is DockEdge.Top or DockEdge.Bottom)
        {
            fill.Width = 37;
            fill.HorizontalAlignment = HorizontalAlignment.Left;
        }
        else
        {
            fill.Height = 37;
            fill.VerticalAlignment = VerticalAlignment.Top;
        }
        return fill;
    }

    private void UpdateNotchWings()
    {
        var dimensions = _surface.RenderSize;
        if (_preferences.Shape != IslandShape.Notch || _inTaskbar
            || Math.Min(dimensions.Width, dimensions.Height) < 18 || !_surface.IsLoaded)
        {
            if (_wings.Data is not null) _wings.Data = null;
            return;
        }
        var bounds = _surface.TransformToAncestor(_host).TransformBounds(new System.Windows.Rect(dimensions));
        if (_wings.Data is not null && _lastWingBounds == bounds && _lastWingEdge == _edge)
            return;
        _lastWingBounds = bounds;
        _lastWingEdge = _edge;
        double length = _edge is DockEdge.Top or DockEdge.Bottom ? bounds.Width : bounds.Height;
        double span = 12 * _preferences.Scale / 100d;
        Point PointAt(double along, double inside) => _edge switch
        {
            DockEdge.Left => new Point(bounds.Left + inside, bounds.Top + along),
            DockEdge.Right => new Point(bounds.Right - inside, bounds.Top + along),
            DockEdge.Bottom => new Point(bounds.Left + along, bounds.Bottom - inside),
            _ => new Point(bounds.Left + along, bounds.Top + inside)
        };
        var shape = new StreamGeometry();
        using (var drawing = shape.Open())
        {
            drawing.BeginFigure(PointAt(-span, 0), true, true);
            drawing.LineTo(PointAt(1, 0), true, false);
            drawing.LineTo(PointAt(1, span), true, false);
            drawing.BezierTo(PointAt(0, span * .45), PointAt(-span * .12, 0), PointAt(-span, 0), true, false);
            drawing.BeginFigure(PointAt(length + span, 0), true, true);
            drawing.LineTo(PointAt(length - 1, 0), true, false);
            drawing.LineTo(PointAt(length - 1, span), true, false);
            drawing.BezierTo(PointAt(length, span * .45), PointAt(length + span * .12, 0), PointAt(length + span, 0), true, false);
        }
        shape.Freeze();
        _wings.Data = shape;
    }

    private bool ShowsMusic => _preferences.ShowCompactMusic && _tabs.HasMusic
        && (_preferences.ShowMusicCover || _preferences.ShowMusicRhythm || ShowsMusicText
            || _preferences.CompactMusic is MusicPreview.Cover or MusicPreview.Rhythm);

    private bool ShowsMusicText => _preferences.ShowCompactMusic && _preferences.CompactMusic is (MusicPreview.Track or MusicPreview.Lyric);

    private bool UsesCompactLyrics => _preferences.ShowCompactMusic && _preferences.CompactMusic == MusicPreview.Lyric
        && _preferences.CompactLyrics && _edge is (DockEdge.Top or DockEdge.Bottom) && _tabs.HasLyrics;

    private string MusicPreviewText() => UsesCompactLyrics
        ? _tabs.CurrentLyric ?? _tabs.TrackTitle : _tabs.TrackTitle;

    private string ClockText(DateTime now)
    {
        bool notch = _preferences.Shape == IslandShape.Notch;
        string format = (notch, _preferences.Use24HourClock) switch
        {
            (true, true) => "yyyy-MM-dd HH:mm:ss",
            (true, false) => "yyyy-MM-dd tt h:mm:ss",
            (false, true) => "HH:mm",
            _ => "h:mm tt"
        };
        return now.ToString(format, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    }

    public void TickDisplay(bool idle)
    {
        if (_compactLyrics is not null)
        {
            var line = MusicPreviewText();
            if (_compactLyrics.Text != line)
            {
                _compactLyrics.Text = line;
            }
        }
        if (_compactClock is not null)
        {
            var now = DateTime.Now;
            long second = now.Ticks / TimeSpan.TicksPerSecond;
            if (_clockSecond != second)
            {
                _clockSecond = second;
                _compactClock.Text = ClockText(now);
            }
        }
        if (_rhythm.Length > 0 && Stopwatch.GetElapsedTime(_peakAt) >= TimeSpan.FromMilliseconds(125))
        {
            _peakAt = Stopwatch.GetTimestamp();
            if (_tabs.Playing && !_samplingAudio)
                SampleAudio();
            else if (!_tabs.Playing)
                _peak = 0;
            for (int index = 0; index < _rhythm.Length; index++)
            {
                double phase = .55 + .45 * Math.Abs(Math.Sin(_peakAt / (double)Stopwatch.Frequency * 7 + index * 1.15));
                _rhythm[index].Height = 3 + 17 * _peak * phase;
            }
        }
        if (Stopwatch.GetElapsedTime(_checkedAvoidance) >= TimeSpan.FromMilliseconds(300))
        {
            _checkedAvoidance = Stopwatch.GetTimestamp();
            var (avoid, hide, strip) = ForegroundBehavior();
            if (avoid != _avoided || hide != _fullscreen || strip != _maximizedStrip)
            {
                _avoided = avoid;
                _fullscreen = hide;
                _maximizedStrip = strip;
                Opacity = hide ? 0 : 1;
                _surface.IsHitTestVisible = !hide;
                if (!_awaitingCollapse)
                {
                    Display(_state, _banner);
                }
            }
        }
        if (!idle || !_preferences.PixelShift)
        {
            _lastShift = Stopwatch.GetTimestamp();
        }
        else if (Stopwatch.GetElapsedTime(_lastShift) >= TimeSpan.FromMinutes(1))
        {
            int[] offsets = [0, 1, 2, 3, 2, 1, 0, -1, -2, -3, -2, -1];
            _shiftIndex = (_shiftIndex + 1) % offsets.Length;
            _shift = offsets[_shiftIndex];
            _lastShift = Stopwatch.GetTimestamp();
            PlaceDock(_state == IslandState.Compact);
        }
    }

    private async void SampleAudio()
    {
        _samplingAudio = true;
        try
        {
            var peak = await System.Threading.Tasks.Task.Run(AudioVolume.Peak);
            _peak = peak is { } level ? Math.Clamp(level, 0, 1) : 0;
        }
        catch (Exception) { _peak = 0; }
        finally { _samplingAudio = false; }
    }

    private (bool Avoid, bool Hide, bool Indicator) ForegroundBehavior()
    {
        if (IsDragging || !GetWindowRect(_handle, out var island))
        {
            return default;
        }
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _handle || foreground == GetShellWindow())
        {
            return default;
        }
        if (foreground != _foreground)
        {
            GetWindowThreadProcessId(foreground, out uint id);
            if (id == Environment.ProcessId)
                return default;
            _foreground = foreground;
            _foregroundName = null;
            try
            {
                using var process = Process.GetProcessById((int)id);
                _foregroundName = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        bool matches = _preferences.AvoidApps.Any(name => string.Equals(
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name,
            _foregroundName, StringComparison.OrdinalIgnoreCase));
        if (DwmGetWindowAttribute(foreground, 9, out var app, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(foreground, out app))
        {
            return default;
        }
        var screen = Forms.Screen.FromHandle(_handle).Bounds;
        bool overlaps = island.Left < app.Right && app.Left < island.Right && island.Top < app.Bottom && app.Top < island.Bottom;
        bool fullscreen = app.Left <= screen.Left + 2 && app.Top <= screen.Top + 2
            && app.Right >= screen.Right - 2 && app.Bottom >= screen.Bottom - 2;
        return (matches && overlaps, _preferences.HideForFullscreen && fullscreen,
            _preferences.IndicatorWhenMaximized && overlaps && IsZoomed(foreground));
    }

    private void Animate(DependencyProperty property, double target, Action? completed = null)
    {
        double current = (double)_surface.GetValue(property);
        _surface.SetValue(property, target);
        var animation = new DoubleAnimation(current, target, TransitionDuration)
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }
        _surface.BeginAnimation(property, animation);
    }

    private static FrameworkElement BannerContent(IslandEvent message)
    {
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var action in message.Actions)
        {
            var button = ExpandedTabs.MakeButton(action.Label);
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    await action.ExecuteAsync(default);
                }
                catch (Exception ex)
                {
                    // 只记录异常类型，避免把事件内容写到诊断输出。
                    Trace.TraceWarning("Island action failed ({0}).", ex.GetType().Name);
                    button.Content = "操作失败";
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            actions.Children.Add(button);
        }
        var title = Text(message.Title, 15);
        title.ToolTip = message.Title;
        var subtitle = Text(message.Subtitle ?? string.Empty, 12, true);
        subtitle.ToolTip = message.Subtitle;
        return new StackPanel
        {
            Margin = new Thickness(19, 14, 19, 10),
            Children = { Text(message.Source == "demo" ? "演示通知" : message.Source, 11, true), title, subtitle, actions }
        };
    }

    private static TextBlock Text(string value, double size, bool secondary = false)
    {
        var text = ExpandedTabs.Label(value, size, secondary ? IslandTheme.Muted : IslandTheme.Ink);
        text.Margin = secondary ? new Thickness(0, 3, 0, 0) : default;
        return text;
    }

    private void BeginDrag(object sender, MouseButtonEventArgs e)
    {
        for (var target = e.OriginalSource as Visual; target is not null; target = VisualTreeHelper.GetParent(target) as Visual)
        {
            if (target is System.Windows.Controls.Primitives.ButtonBase or TextBox or ComboBox or ScrollViewer or Slider or XiaohongshuPage)
            {
                return;
            }
        }
        if (GetWindowRect(_handle, out var rect) && _surface.CaptureMouse())
        {
            _dragStart = Forms.Cursor.Position;
            _windowStart = new System.Drawing.Point(rect.Left, rect.Top);
            _dragged = false;
            e.Handled = true;
        }
    }

    private void MoveDrag(object sender, MouseEventArgs e)
    {
        if (!_surface.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = Forms.Cursor.Position;
        int dx = point.X - _dragStart.X;
        int dy = point.Y - _dragStart.Y;
        _dragged |= Math.Abs(dx) >= 5 || Math.Abs(dy) >= 5;
        if (_dragged)
        {
            MoveTo(_windowStart.X + dx, _windowStart.Y + dy);
        }
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (_surface.IsMouseCaptured)
        {
            _surface.ReleaseMouseCapture();
            e.Handled = true;
            if (_dragged)
            {
                var cursor = Forms.Cursor.Position;
                var screen = Forms.Screen.FromPoint(cursor);
                var work = screen.WorkingArea;
                var edge = new[] { (DockEdge.Top, Math.Abs(cursor.Y - work.Top)),
                    (DockEdge.Left, Math.Abs(cursor.X - work.Left)), (DockEdge.Right, Math.Abs(cursor.X - work.Right)),
                    (DockEdge.Bottom, Math.Abs(cursor.Y - work.Bottom)) }
                    .MinBy(pair => pair.Item2).Item1;
                if (_preferences.InTaskbar && TaskbarDock.Find(screen) is { } taskbar)
                    edge = taskbar.Edge;
                var (width, height) = PixelSize(work);
                int position = edge is DockEdge.Top or DockEdge.Bottom
                    ? Percent(cursor.X - work.Left - width / 2, work.Width - width)
                    : Percent(cursor.Y - work.Top - height / 2, work.Height - height);
                DockChanged?.Invoke(_preferences with { Edge = edge, Position = position,
                    Monitor = screen.Primary && _preferences.Monitor is null ? null : screen.DeviceName });
            }
            else
            {
                IslandClicked?.Invoke();
            }
        }
    }

    private static int Percent(int distance, int span) => span <= 0 ? 50 : Math.Clamp((int)Math.Round(100d * distance / span), 0, 100);

    private Forms.Screen? DockScreen() => Forms.Screen.AllScreens.FirstOrDefault(candidate => candidate.DeviceName == _preferences.Monitor)
        ?? Forms.Screen.PrimaryScreen;

    private double ExpandedWidth()
    {
        if (DockScreen() is not { } screen) return PanelWidth;
        var work = screen.WorkingArea;
        double dpi = GetTargetDpi(new System.Drawing.Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
        return Math.Clamp(work.Width * 9600d / (dpi * _preferences.Scale) - 28, 300, PanelWidth);
    }

    private double ExpandedWebHeight()
    {
        if (DockScreen() is not { } screen) return 445;
        var work = screen.WorkingArea;
        double dpi = GetTargetDpi(new System.Drawing.Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
        return Math.Clamp(work.Height * 9600d / (dpi * _preferences.Scale) - 30, 200, 445);
    }

    private void PlaceDock(bool compact)
    {
        var screen = DockScreen();
        if (_handle == IntPtr.Zero || screen is null)
        {
            return;
        }
        var work = screen.WorkingArea;
        var taskbar = _preferences.InTaskbar ? TaskbarDock.Find(screen) : null;
        _edge = taskbar?.Edge ?? _preferences.Edge;
        _inTaskbar = compact && taskbar.HasValue;
        _taskbarThickness = 0;
        if (taskbar is { } bar)
        {
            var dpi = GetTargetDpi(new System.Drawing.Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
            int thickness = _edge is DockEdge.Top or DockEdge.Bottom ? bar.Bounds.Height : bar.Bounds.Width;
            _taskbarThickness = thickness * 9600d / (dpi * _preferences.Scale);
        }
        _surface.HorizontalAlignment = _edge switch
        {
            DockEdge.Left => HorizontalAlignment.Left,
            DockEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
        _surface.VerticalAlignment = _edge switch
        {
            DockEdge.Top => VerticalAlignment.Top, DockEdge.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Center
        };
        double inset = !_inTaskbar && _preferences.Shape == IslandShape.Capsule ? 6 * _preferences.Scale / 100d : 0;
        _surface.Margin = _edge switch
        {
            DockEdge.Left => new Thickness(inset, 0, 0, 0), DockEdge.Right => new Thickness(0, 0, inset, 0),
            DockEdge.Bottom => new Thickness(0, 0, 0, inset), _ => new Thickness(0, inset, 0, 0)
        };
        var (width, height) = PixelSize(work);
        var box = _inTaskbar ? taskbar!.Value.Bounds : work;
        int roomX = Math.Max(0, box.Width - width);
        int roomY = Math.Max(0, box.Height - height);
        int x = box.Left + Math.Clamp((int)Math.Round(roomX * _preferences.Position / 100d) + _shift, 0, roomX);
        int y = box.Top + Math.Clamp((int)Math.Round(roomY * _preferences.Position / 100d) + _shift, 0, roomY);
        MoveTo(_edge switch { DockEdge.Left => box.Left, DockEdge.Right => box.Right - width, _ => x },
            _edge switch { DockEdge.Top => box.Top, DockEdge.Bottom => box.Bottom - height, _ => y });
    }

    private (int Width, int Height) PixelSize(System.Drawing.Rectangle work)
    {
        var dpi = GetTargetDpi(new System.Drawing.Point(work.Left + work.Width / 2, work.Top + work.Height / 2));
        return ((int)Math.Round(Width * dpi / 96), (int)Math.Round(Height * dpi / 96));
    }

    private double GetTargetDpi(System.Drawing.Point anchor)
    {
        var monitor = MonitorFromPoint(anchor, 2);
        return GetDpiForMonitor(monitor, 0, out uint x, out _) == 0
            ? x : VisualTreeHelper.GetDpi(this).PixelsPerInchX;
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsVisible)
            {
                PlaceDock(_state == IslandState.Compact && !_awaitingCollapse);
            }
        });
    }

    private void MoveTo(int x, int y) => SetWindowPos(_handle, IntPtr.Zero, x, y, 0, 0, MoveOnlyWithoutActivation);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out Rect value, int size);
}
