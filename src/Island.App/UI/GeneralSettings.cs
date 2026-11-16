using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Island.App.Persistence;
using Island.Core.Events;
using Forms = System.Windows.Forms;

namespace Island.App.UI;

internal sealed class GeneralSettings : ScrollViewer
{
    public StackPanel QuotaAppearance { get; } = new();
    private readonly CompactBatterySettings _batterySettings;
    private readonly Dictionary<string, StackPanel> _groups = new();
    private sealed record MonitorChoice(string? Id, string Label);

    public GeneralSettings(IslandPreferences settings, Action<IslandPreferences> apply)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var body = new StackPanel();
        Content = body;
        var compact = Group("岛体外观");
        var display = Group("岛体显示");
        var expanded = Group("展开外观");
        var behavior = Group("窗口行为");
        StackPanel Group(string name)
        {
            var group = new StackPanel();
            _groups.Add(name, group);
            body.Children.Add(group);
            return group;
        }

        var dockEdge = settings.Edge;
        var shape = settings.Shape;
        var skin = new ComboBox { ItemsSource = new[] { "经典黑", "奶油" }, SelectedIndex = (int)settings.Skin };
        var oled = Check(settings.PixelShift);
        var minimal = Check(settings.Minimal);
        var compactQuotas = Check(settings.CompactQuotas);
        var compactSystem = Check(settings.CompactSystem);
        var compactMusic = Check(settings.ShowCompactMusic);
        var compactClock = Check(settings.CompactClock);
        var compactDevices = Check(settings.CompactDevices);
        var compactShelfCount = Check(settings.CompactShelfCount);
        var compactTodo = Check(settings.CompactTodo);
        SettingsRows.Section(compact, "收起样式");
        var shapeOrder = new[] { IslandShape.Notch, IslandShape.Capsule, IslandShape.Indicator };
        var compactContent = new StackPanel();
        var dimensions = new StackPanel();
        var contentHeading = Label("");
        void SelectShape(IslandShape value)
        {
            shape = value;
            contentHeading.Text = value == IslandShape.Notch ? "显示内容 · 刘海（可拖动排序）" : "显示内容 · 胶囊";
            compactContent.Visibility = value == IslandShape.Indicator ? Visibility.Collapsed : Visibility.Visible;
            dimensions.Visibility = compactContent.Visibility;
        }
        var shapeCards = AppearanceSettings.ShapeCards(shapeOrder, shape, SelectShape);
        compact.Children.Add(shapeCards);
        compactContent.Children.Add(contentHeading);
        compact.Children.Add(compactContent);
        SelectShape(shape);

        var notchOrder = new ObservableCollection<NotchItem>(settings.NotchOrder);
        var compactOrder = new StackPanel();
        var contentOptions = new Dictionary<NotchItem, (string Title, CheckBox Toggle)>
        {
            [NotchItem.Quotas] = ("配额环", compactQuotas), [NotchItem.Music] = ("正在播放的音乐", compactMusic),
            [NotchItem.System] = ("系统监控", compactSystem), [NotchItem.Shelf] = ("文件中转", compactShelfCount),
            [NotchItem.Devices] = ("设备与电脑电量", compactDevices), [NotchItem.Clock] = ("时间", compactClock),
            [NotchItem.Todo] = ("待办提醒", compactTodo)
        };
        compactContent.Children.Add(compactOrder);
        void BuildNotchRows()
        {
            compactOrder.Children.Clear();
            foreach (var item in notchOrder)
            {
                var (title, toggle) = contentOptions[item];
                compactOrder.Children.Add(AppearanceSettings.OrderRow(title, toggle, item, notchOrder, BuildNotchRows,
                    () => shape == IslandShape.Notch));
            }
        }
        BuildNotchRows();
        _batterySettings = new CompactBatterySettings(settings.CompactBatteryDevices);
        _batterySettings.SetBinding(IsEnabledProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = compactDevices });
        compactContent.Children.Add(_batterySettings);
        SettingsRows.Section(compact, "尺寸与位置");
        var edges = new[] { DockEdge.Top, DockEdge.Bottom, DockEdge.Left, DockEdge.Right };
        var edgeChoices = SettingsRows.Choices(Array.IndexOf(edges, dockEdge),
            index => dockEdge = edges[index], "顶部", "底部", "左侧", "右侧");
        compact.Children.Add(SettingsRows.Row("贴靠位置", edgeChoices));
        var autoWidth = Check(settings.AutoCompactWidth);
        dimensions.Children.Add(SettingsRows.Row("自适应宽度", autoWidth));
        var compactWidth = AppearanceSettings.Number(dimensions, "宽度（沿停靠边）", 72, 720, settings.CompactWidth, " DIP");
        var compactHeight = AppearanceSettings.Number(dimensions, "高度（垂直停靠边）", 24, 64, settings.CompactHeight, " DIP");
        compactWidth.IsEnabled = !settings.AutoCompactWidth;
        autoWidth.Checked += (_, _) => compactWidth.IsEnabled = false;
        autoWidth.Unchecked += (_, _) => compactWidth.IsEnabled = true;
        compact.Children.Add(dimensions);
        var scale = AppearanceSettings.Number(compact, "岛体大小", 80, 140, settings.Scale);
        var position = AppearanceSettings.Number(compact, "沿停靠边的位置", 0, 100, settings.Position);
        compact.Children.Add(SettingsRows.Row("皮肤", skin));
        compact.Children.Add(SettingsRows.Row("OLED 像素偏移", oled));
        compact.Children.Add(SettingsRows.Row("极简模式", minimal));

        var ringInitial = Check(settings.RingInitial);
        var quotaWarnings = Check(settings.QuotaWarnings);
        bool showRemaining = settings.ShowRemaining;
        var preview = new ComboBox { ItemsSource = new[] { "不显示文字", "歌名", "当前歌词" },
            SelectedIndex = settings.CompactMusic is MusicPreview.Track ? 1 : settings.CompactMusic is MusicPreview.Lyric ? 2 : 0 };
        var cover = Check(settings.ShowMusicCover || settings.CompactMusic == MusicPreview.Cover);
        var rhythm = Check(settings.ShowMusicRhythm || settings.CompactMusic == MusicPreview.Rhythm);
        SettingsRows.Section(compactContent, "音乐预览");
        compactContent.Children.Add(SettingsRows.Row("预览文字", preview));
        compactContent.Children.Add(SettingsRows.Row("显示专辑封面", cover));
        compactContent.Children.Add(SettingsRows.Row("显示律动条", rhythm));
        SettingsRows.Section(QuotaAppearance, "提示");
        QuotaAppearance.Children.Add(SettingsRows.Row("额度达到 70%／90% 时提醒", quotaWarnings));
        SettingsRows.Section(QuotaAppearance, "配额显示");
        bool useMultipleRings = settings.MultipleRings;
        var ringChoices = SettingsRows.Choices(useMultipleRings ? 1 : 0, index => useMultipleRings = index == 1, "单环", "多环");
        var quotaChoices = SettingsRows.Choices(showRemaining ? 1 : 0, index => showRemaining = index == 1, "已用额度", "剩余额度");
        QuotaAppearance.Children.Add(SettingsRows.Row("环的样式", ringChoices));
        QuotaAppearance.Children.Add(SettingsRows.Row("显示已用或剩余", quotaChoices));
        QuotaAppearance.Children.Add(SettingsRows.Row("环内显示首字母", ringInitial));

        SettingsRows.Section(display, "显示器与时钟");
        var monitors = Forms.Screen.AllScreens.Select((screen, index) => new MonitorChoice(screen.DeviceName,
            $"显示器 {index + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? "（主屏）" : "")}")).ToList();
        if (settings.Monitor is { } id && monitors.All(item => item.Id != id))
        {
            monitors.Add(new MonitorChoice(id, $"{id}（已断开）"));
        }
        monitors.Insert(0, new MonitorChoice(null, "跟随主显示器"));
        var monitor = new ComboBox { ItemsSource = monitors, DisplayMemberPath = nameof(MonitorChoice.Label) };
        monitor.SelectedIndex = Math.Max(0, monitors.FindIndex(item => item.Id == settings.Monitor));
        display.Children.Add(SettingsRows.Row("显示器", monitor));
        var avoid = new TextBox { Text = string.Join(", ", settings.AvoidApps), ToolTip = "例如 chrome.exe, obs64.exe" };
        display.Children.Add(SettingsRows.Row("自动避让应用（进程名，用逗号分隔）", avoid));
        var clock = new ComboBox { ItemsSource = new[] { "12 小时制", "24 小时制" }, SelectedIndex = settings.Use24HourClock ? 1 : 0 };
        display.Children.Add(SettingsRows.Row("时钟格式", clock));
        SettingsRows.Section(display, "歌词来源");
        var folder = new TextBox { Text = settings.LyricsFolder };
        var browse = new Button { Content = "选择…", Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2) };
        browse.Click += (_, _) =>
        {
            using var dialog = new Forms.FolderBrowserDialog();
            dialog.SelectedPath = folder.Text;
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
            {
                folder.Text = dialog.SelectedPath;
            }
        };
        DockPanel.SetDock(browse, Dock.Right);
        display.Children.Add(SettingsRows.Row("本地 LRC 歌词文件夹", new DockPanel { Children = { browse, folder } }));
        var autoLyrics = Check(settings.NeteaseOnlineLyrics);
        autoLyrics.ToolTip = "直连网易云音乐。优先发送歌曲 ID，必要时用歌名和歌手搜索。";
        display.Children.Add(SettingsRows.Row("网易云自动获取歌词（联网）", autoLyrics));

        SettingsRows.Section(expanded, "显示的页面（可拖动排序）");
        var pageList = new StackPanel();
        expanded.Children.Add(pageList);
        var pages = new ObservableCollection<IslandPage>(settings.Pages.Select(item => item.Page));
        var pageToggles = settings.Pages.ToDictionary(item => item.Page, item => Check(item.Enabled));
        void BuildPages()
        {
            pageList.Children.Clear();
            foreach (var page in pages)
                pageList.Children.Add(AppearanceSettings.OrderRow("显示" + PageLabel(page), pageToggles[page], page, pages, BuildPages));
        }
        BuildPages();
        SettingsRows.Section(expanded, "通用");
        var startPage = new ComboBox { ItemsSource = new[] { "上次停留的页面" }.Concat(Enum.GetValues<IslandPage>().Select(PageLabel)),
            SelectedIndex = settings.ExpandPage is { } configuredPage ? (int)configuredPage + 1 : 0 };
        var rightClick = Check(settings.RightClickSettings);
        var wheel = settings.WheelAction;
        expanded.Children.Add(SettingsRows.Row("每次打开时显示", startPage));
        expanded.Children.Add(SettingsRows.Row("右键点击打开设置", rightClick));
        var wheelChoices = SettingsRows.Choices((int)wheel,
            index => wheel = (IslandWheelAction)index, "不做任何事", "切换页面", "调整音量");
        expanded.Children.Add(SettingsRows.Row("鼠标滚轮", wheelChoices));

        var defaults = new IslandPreferences();
        compact.Children.Insert(0, SettingsRows.ResetButton(() =>
        {
            ((RadioButton)shapeCards.Children[Array.IndexOf(shapeOrder, defaults.Shape)]).IsChecked = true;
            SettingsRows.SelectChoice(edgeChoices, Array.IndexOf(edges, defaults.Edge));
            autoWidth.IsChecked = defaults.AutoCompactWidth;
            compactWidth.Value = defaults.CompactWidth;
            compactHeight.Value = defaults.CompactHeight;
            scale.Value = defaults.Scale;
            position.Value = defaults.Position;
            skin.SelectedIndex = (int)defaults.Skin;
            oled.IsChecked = defaults.PixelShift;
            minimal.IsChecked = defaults.Minimal;
            compactQuotas.IsChecked = defaults.CompactQuotas;
            compactSystem.IsChecked = defaults.CompactSystem;
            compactMusic.IsChecked = defaults.ShowCompactMusic;
            compactClock.IsChecked = defaults.CompactClock;
            compactDevices.IsChecked = defaults.CompactDevices;
            _batterySettings.Reset();
            compactShelfCount.IsChecked = defaults.CompactShelfCount;
            compactTodo.IsChecked = defaults.CompactTodo;
            cover.IsChecked = defaults.ShowMusicCover;
            rhythm.IsChecked = defaults.ShowMusicRhythm;
            preview.SelectedIndex = 2;
            notchOrder.Clear();
            foreach (var item in defaults.NotchOrder) notchOrder.Add(item);
            BuildNotchRows();
        }));
        expanded.Children.Insert(0, SettingsRows.ResetButton(() =>
        {
            pages.Clear();
            foreach (var page in defaults.Pages)
            {
                pages.Add(page.Page);
                pageToggles[page.Page].IsChecked = page.Enabled;
            }
            BuildPages();
            startPage.SelectedIndex = 0;
            rightClick.IsChecked = defaults.RightClickSettings;
            SettingsRows.SelectChoice(wheelChoices, (int)defaults.WheelAction);
        }));
        QuotaAppearance.Children.Insert(0, SettingsRows.ResetButton(() =>
        {
            quotaWarnings.IsChecked = defaults.QuotaWarnings;
            ringInitial.IsChecked = defaults.RingInitial;
            SettingsRows.SelectChoice(ringChoices, defaults.MultipleRings ? 1 : 0);
            SettingsRows.SelectChoice(quotaChoices, defaults.ShowRemaining ? 1 : 0);
        }));

        SettingsRows.Section(behavior, "窗口行为");
        var taskbar = Check(settings.InTaskbar);
        var topmost = Check(settings.AlwaysOnTop);
        var fullscreen = Check(settings.HideForFullscreen);
        var maximized = Check(settings.IndicatorWhenMaximized);
        behavior.Children.Add(SettingsRows.Row("收起时放入 Windows 任务栏区域", taskbar));
        behavior.Children.Add(SettingsRows.Row("收起和展开时均置顶", topmost));
        behavior.Children.Add(SettingsRows.Row("全屏应用覆盖时隐藏小岛", fullscreen));
        behavior.Children.Add(SettingsRows.Row("最大化窗口覆盖时收成指示条", maximized));
        var status = Label("");
        var quotaStatus = Label("");
        var applied = settings;
        var save = SaveButton("应用外观设置");
        var saveQuota = SaveButton("应用配额显示设置");
        save.Click += (_, _) => Save();
        saveQuota.Click += (_, _) => Commit(applied with
        {
            QuotaWarnings = quotaWarnings.IsChecked == true, MultipleRings = useMultipleRings,
            RingInitial = ringInitial.IsChecked == true, ShowRemaining = showRemaining
        }, quotaStatus);
        void Save()
        {
            var lyrics = folder.Text.Trim().Trim('"');
            if (lyrics.Length > 0 && lyrics != settings.LyricsFolder && !Directory.Exists(lyrics))
            {
                status.Text = "请选择存在的歌词文件夹。";
                return;
            }
            var next = applied with
            {
                Edge = dockEdge, Position = (int)position.Value, Scale = (int)scale.Value,
                AutoCompactWidth = autoWidth.IsChecked == true, CompactWidth = (int)compactWidth.Value,
                CompactHeight = (int)compactHeight.Value,
                Skin = (IslandSkin)skin.SelectedIndex, Shape = shape,
                InTaskbar = taskbar.IsChecked == true, AlwaysOnTop = topmost.IsChecked == true,
                HideForFullscreen = fullscreen.IsChecked == true, IndicatorWhenMaximized = maximized.IsChecked == true,
                CompactClock = compactClock.IsChecked == true,
                CompactDevices = compactDevices.IsChecked == true, CompactShelfCount = compactShelfCount.IsChecked == true,
                CompactBatteryDevices = _batterySettings.Selection,
                CompactTodo = compactTodo.IsChecked == true,
                ShowMusicCover = cover.IsChecked == true, ShowMusicRhythm = rhythm.IsChecked == true,
                CompactMusic = preview.SelectedIndex switch { 1 => MusicPreview.Track, 2 => MusicPreview.Lyric, _ => MusicPreview.Off },
                CompactQuotas = compactQuotas.IsChecked == true, CompactSystem = compactSystem.IsChecked == true,
                ShowCompactMusic = compactMusic.IsChecked == true,
                ExpandPage = startPage.SelectedIndex > 0 ? (IslandPage)(startPage.SelectedIndex - 1) : null,
                RightClickSettings = rightClick.IsChecked == true, WheelAction = wheel,
                PixelShift = oled.IsChecked == true, Minimal = minimal.IsChecked == true,
                Monitor = (monitor.SelectedItem as MonitorChoice)?.Id,
                AvoidApps = avoid.Text.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Use24HourClock = clock.SelectedIndex == 1,
                LyricsFolder = lyrics, NeteaseOnlineLyrics = autoLyrics.IsChecked == true,
                Pages = pages.Select(page => new PagePreference(page, pageToggles[page].IsChecked == true)).ToArray(), NotchOrder = notchOrder.ToArray()
            };
            Commit(next, status);
        }
        void Commit(IslandPreferences next, TextBlock feedback)
        {
            try
            {
                next.Save();
                applied = next;
                apply(next);
                feedback.Text = "已应用。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                feedback.Text = "无法保存常规设置。";
            }
        }
        body.Children.Add(save);
        body.Children.Add(status);
        QuotaAppearance.Children.Add(saveQuota);
        QuotaAppearance.Children.Add(quotaStatus);
        ShowSection("岛体外观");
    }

    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices) => _batterySettings.UpdateDevices(devices);

    public void ShowSection(string section)
    {
        foreach (var (name, group) in _groups)
            group.Visibility = name == section ? Visibility.Visible : Visibility.Collapsed;
        ScrollToTop();
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3) };
    private static CheckBox Check(bool value) => new() { IsChecked = value };

    private static Button SaveButton(string label) => new()
    {
        Content = label, HorizontalAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 12, 0, 0)
    };

    public static string PageLabel(IslandPage page) => page switch
    {
        IslandPage.Music => "音乐", IslandPage.Clipboard => "剪贴板", IslandPage.Quota => "AI 额度",
        IslandPage.Devices => "设备电量", IslandPage.Shelf => "文件中转", IslandPage.Todo => "待办清单", _ => "系统监控"
    };
}
