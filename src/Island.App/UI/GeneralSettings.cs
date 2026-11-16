using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Island.App.Persistence;
using Forms = System.Windows.Forms;

namespace Island.App.UI;

internal sealed class GeneralSettings : ScrollViewer
{
    private readonly Dictionary<string, StackPanel> _groups = new();
    private sealed record MonitorChoice(string? Id, string Label);

    public GeneralSettings(IslandPreferences settings, Action<IslandPreferences> apply)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var body = new StackPanel();
        Content = body;
        Section(body, "岛体外观");
        var edge = new ComboBox { ItemsSource = new[] { "顶部", "左侧", "右侧", "底部" }, SelectedIndex = (int)settings.Edge };
        var position = Number(body, "沿停靠边的位置", 0, 100, settings.Position);
        var scale = Number(body, "岛体大小", 80, 140, settings.Scale);
        var oled = Check("OLED 像素偏移", settings.PixelShift);
        var minimal = Check("极简模式（收起时只显示细线）", settings.Minimal);
        body.Children.Insert(1, Label("停靠方向"));
        body.Children.Insert(2, edge);
        var skin = new ComboBox { ItemsSource = new[] { "经典黑", "奶油" }, SelectedIndex = (int)settings.Skin };
        var shape = new ComboBox { ItemsSource = new[] { "胶囊", "刘海", "指示条" }, SelectedIndex = (int)settings.Shape };
        body.Children.Add(Label("皮肤"));
        body.Children.Add(skin);
        body.Children.Add(Label("形态"));
        body.Children.Add(shape);
        var multipleRings = Check("每个账号使用多环（最多三个）", settings.MultipleRings);
        var ringInitial = Check("配额环内显示账号首字母", settings.RingInitial);
        var quotaWarnings = Check("额度达到 70%／90% 时提醒", settings.QuotaWarnings);
        var quotaMode = new ComboBox { ItemsSource = new[] { "显示已用额度", "显示剩余额度" }, SelectedIndex = settings.ShowRemaining ? 1 : 0 };
        var preview = new ComboBox { ItemsSource = new[] { "不显示文字", "歌名", "当前歌词" },
            SelectedIndex = settings.CompactMusic is MusicPreview.Track ? 1 : settings.CompactMusic is MusicPreview.Lyric ? 2 : 0 };
        var cover = Check("显示专辑封面", settings.ShowMusicCover || settings.CompactMusic == MusicPreview.Cover);
        var rhythm = Check("显示随声音变化的律动条", settings.ShowMusicRhythm || settings.CompactMusic == MusicPreview.Rhythm);
        body.Children.Add(multipleRings);
        body.Children.Add(ringInitial);
        body.Children.Add(quotaWarnings);
        body.Children.Add(Label("额度百分比"));
        body.Children.Add(quotaMode);
        body.Children.Add(Label("收起时的音乐预览"));
        body.Children.Add(preview);
        body.Children.Add(cover);
        body.Children.Add(rhythm);
        body.Children.Add(oled);
        body.Children.Add(minimal);
        Section(body, "岛体显示");
        var monitors = Forms.Screen.AllScreens.Select((screen, index) => new MonitorChoice(screen.DeviceName,
            $"显示器 {index + 1} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? "（主屏）" : "")}")).ToList();
        if (settings.Monitor is { } id && monitors.All(item => item.Id != id))
        {
            monitors.Add(new MonitorChoice(id, $"{id}（已断开）"));
        }
        monitors.Insert(0, new MonitorChoice(null, "跟随主显示器"));
        var monitor = new ComboBox { ItemsSource = monitors, DisplayMemberPath = nameof(MonitorChoice.Label) };
        monitor.SelectedIndex = Math.Max(0, monitors.FindIndex(item => item.Id == settings.Monitor));
        body.Children.Add(Label("显示器"));
        body.Children.Add(monitor);
        body.Children.Add(Label("自动避让应用（进程名，用逗号分隔）"));
        var avoid = new TextBox { Text = string.Join(", ", settings.AvoidApps), ToolTip = "例如 chrome.exe, obs64.exe" };
        body.Children.Add(avoid);
        var clock = new ComboBox { ItemsSource = new[] { "12 小时制", "24 小时制" }, SelectedIndex = settings.Use24HourClock ? 1 : 0 };
        body.Children.Add(Label("时钟格式"));
        body.Children.Add(clock);
        var compactClock = Check("收起时显示时钟", settings.CompactClock);
        var compactDevices = Check("收起时显示设备电量", settings.CompactDevices);
        var compactShelfCount = Check("收起时显示置物架文件数", settings.CompactShelfCount);
        body.Children.Add(compactClock);
        body.Children.Add(compactDevices);
        body.Children.Add(compactShelfCount);
        body.Children.Add(Label("刘海收起时的内容顺序"));
        var notchList = new ListBox { MaxHeight = 134 };
        var notchOrder = new ObservableCollection<NotchItem>(settings.NotchOrder);
        string NotchLabel(NotchItem item) => item switch
        {
            NotchItem.Quotas => "配额环", NotchItem.Music => "音乐预览", NotchItem.System => "系统读数",
            NotchItem.Clock => "日期与时钟", NotchItem.Devices => "设备电量", _ => "置物架文件数"
        };
        void ShowNotchOrder(int selected)
        {
            notchList.ItemsSource = notchOrder.Select(NotchLabel).ToArray();
            notchList.SelectedIndex = selected;
        }
        void MoveNotchItem(int direction)
        {
            int index = notchList.SelectedIndex;
            int target = index + direction;
            if (index < 0 || target < 0 || target >= notchOrder.Count) return;
            notchOrder.Move(index, target);
            ShowNotchOrder(target);
        }
        var up = new Button { Content = "上移", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(7, 2, 7, 2) };
        var down = new Button { Content = "下移", Padding = new Thickness(7, 2, 7, 2) };
        up.Click += (_, _) => MoveNotchItem(-1);
        down.Click += (_, _) => MoveNotchItem(1);
        ShowNotchOrder(0);
        body.Children.Add(notchList);
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { up, down }
        });
        body.Children.Add(Label("本地 LRC 歌词文件夹"));
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
        body.Children.Add(new DockPanel { Children = { browse, folder } });
        var autoLyrics = Check("网易云自动获取歌词（联网）", settings.NeteaseOnlineLyrics);
        autoLyrics.ToolTip = "直连网易云音乐。优先发送歌曲 ID，必要时用歌名和歌手搜索。";
        body.Children.Add(autoLyrics);
        Section(body, "展开外观");
        body.Children.Add(Label("功能页面（勾选显示，拖动排序）"));
        var pageList = new StackPanel();
        var pages = new ObservableCollection<PagePreference>(settings.Pages);
        void BuildPages()
        {
            pageList.Children.Clear();
            foreach (var item in pages)
            {
                var row = new DockPanel { Background = Brushes.Transparent, AllowDrop = true };
                var grip = new TextBlock { Text = "≡", Cursor = Cursors.SizeAll, Padding = new Thickness(7, 2, 9, 3) };
                grip.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        DragDrop.DoDragDrop(grip, item.Page.ToString(), DragDropEffects.Move);
                    }
                };
                row.DragOver += (_, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
                row.Drop += (_, e) =>
                {
                    if (e.Data.GetData(DataFormats.StringFormat) is string value && Enum.TryParse<IslandPage>(value, out var key))
                    {
                        int from = pages.IndexOf(pages.FirstOrDefault(p => p.Page == key)!);
                        int to = pages.IndexOf(pages.First(p => p.Page == item.Page));
                        if (from >= 0 && from != to)
                        {
                            pages.Move(from, to);
                            BuildPages();
                        }
                        e.Handled = true;
                    }
                };
                var check = new CheckBox { Content = PageLabel(item.Page), IsChecked = item.Enabled, VerticalAlignment = VerticalAlignment.Center };
                check.Click += (_, _) =>
                {
                    var index = pages.IndexOf(pages.First(page => page.Page == item.Page));
                    pages[index] = item with { Enabled = check.IsChecked == true };
                };
                row.Children.Add(grip);
                row.Children.Add(check);
                pageList.Children.Add(row);
            }
        }
        BuildPages();
        body.Children.Add(pageList);
        Section(body, "窗口行为");
        var taskbar = Check("收起时放入 Windows 任务栏区域", settings.InTaskbar);
        var topmost = Check("收起和展开时均置顶", settings.AlwaysOnTop);
        var fullscreen = Check("全屏应用覆盖时隐藏小岛", settings.HideForFullscreen);
        var maximized = Check("最大化窗口覆盖时收成指示条", settings.IndicatorWhenMaximized);
        body.Children.Add(taskbar);
        body.Children.Add(topmost);
        body.Children.Add(fullscreen);
        body.Children.Add(maximized);
        var status = Label("");
        var save = new Button { Content = "应用常规设置", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 12, 0, 0) };
        save.Click += (_, _) =>
        {
            var lyrics = folder.Text.Trim().Trim('"');
            if (lyrics.Length > 0 && !Directory.Exists(lyrics))
            {
                status.Text = "请选择存在的歌词文件夹。";
                return;
            }
            var next = settings with
            {
                Edge = (DockEdge)edge.SelectedIndex, Position = (int)position.Value, Scale = (int)scale.Value,
                Skin = (IslandSkin)skin.SelectedIndex, Shape = (IslandShape)shape.SelectedIndex,
                InTaskbar = taskbar.IsChecked == true, AlwaysOnTop = topmost.IsChecked == true,
                HideForFullscreen = fullscreen.IsChecked == true, IndicatorWhenMaximized = maximized.IsChecked == true,
                QuotaWarnings = quotaWarnings.IsChecked == true,
                MultipleRings = multipleRings.IsChecked == true, RingInitial = ringInitial.IsChecked == true,
                ShowRemaining = quotaMode.SelectedIndex == 1, CompactClock = compactClock.IsChecked == true,
                CompactDevices = compactDevices.IsChecked == true, CompactShelfCount = compactShelfCount.IsChecked == true,
                ShowMusicCover = cover.IsChecked == true, ShowMusicRhythm = rhythm.IsChecked == true,
                CompactMusic = preview.SelectedIndex switch { 1 => MusicPreview.Track, 2 => MusicPreview.Lyric, _ => MusicPreview.Off },
                CompactLyrics = true,
                PixelShift = oled.IsChecked == true, Minimal = minimal.IsChecked == true,
                Monitor = (monitor.SelectedItem as MonitorChoice)?.Id,
                AvoidApps = avoid.Text.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Use24HourClock = clock.SelectedIndex == 1,
                LyricsFolder = lyrics, NeteaseOnlineLyrics = autoLyrics.IsChecked == true,
                Pages = pages.ToArray(), NotchOrder = notchOrder.ToArray()
            };
            try
            {
                next.Save();
                apply(next);
                status.Text = "已应用。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "无法保存常规设置。";
            }
        };
        body.Children.Add(save);
        body.Children.Add(status);
        var controls = body.Children.Cast<UIElement>().Where(item => item != save && item != status).ToArray();
        body.Children.Clear();
        StackPanel? group = null;
        for (int i = 0; i < controls.Length; i++)
        {
            var item = controls[i];
            if (item is TextBlock { Tag: "section" } header)
            {
                group = new StackPanel();
                _groups.Add(header.Text, group);
                body.Children.Add(group);
                continue;
            }
            if (item is CheckBox { Content: string caption } check)
                group!.Children.Add(SettingsRows.Row(caption, check));
            else if (item is TextBlock label && i + 1 < controls.Length
                && controls[i + 1] is FrameworkElement input && input is ComboBox or TextBox or Slider or DockPanel)
            {
                var row = SettingsRows.Row(label.Text, input);
                if (input is Slider slider)
                    slider.ValueChanged += (_, _) => ((TextBlock)((Grid)row.Child).Children[0]).Text = label.Text;
                group!.Children.Add(row);
                i++;
            }
            else
                group!.Children.Add(item);
        }
        body.Children.Add(save);
        body.Children.Add(status);
        ShowSection("岛体外观");
    }

    public void ShowSection(string section)
    {
        foreach (var (name, group) in _groups)
            group.Visibility = name == section ? Visibility.Visible : Visibility.Collapsed;
        ScrollToTop();
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3) };
    private static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 2) };
    private static void Section(Panel panel, string text) => panel.Children.Add(new TextBlock
    {
        Text = text, Tag = "section", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3)
    });

    private static Slider Number(Panel panel, string label, int min, int max, int value)
    {
        var caption = Label($"{label}  {value}%");
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, _) => caption.Text = $"{label}  {slider.Value:0}%";
        panel.Children.Add(caption);
        panel.Children.Add(slider);
        return slider;
    }

    public static string PageLabel(IslandPage page) => page switch
    {
        IslandPage.Music => "音乐", IslandPage.Clipboard => "剪贴板", IslandPage.Quota => "AI 额度",
        IslandPage.Devices => "设备", IslandPage.Shelf => "置物架", IslandPage.Todo => "待办清单", _ => "系统监控"
    };
}
