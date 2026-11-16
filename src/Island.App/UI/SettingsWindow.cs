using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class SettingsWindow : Window
{
    private readonly GeneralSettings _general;

    public void UpdateDevices(IReadOnlyList<DeviceBattery> devices) => _general.UpdateDevices(devices);

    public SettingsWindow(SettingsStore store, Func<Dictionary<string, bool>, Task> applySources,
        QuotaAccountStore quotaAccounts, Action refreshQuotas, IslandPreferences preferences, Action<IslandPreferences> applyPreferences,
        Func<Guid, IReadOnlyList<QuotaMetric>> metricsFor)
    {
        Title = "灵动岛设置";
        Width = Math.Min(1090, SystemParameters.WorkArea.Width * .95);
        Height = Math.Min(780, SystemParameters.WorkArea.Height * .9);
        MinWidth = 600;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        UseLayoutRounding = true;
        Background = SettingsRows.Canvas;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Island;component/UI/SettingsChrome.xaml", UriKind.Relative)
        });

        var content = new StackPanel();
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        var startupStatus = new TextBlock { Margin = new Thickness(0, 10, 0, 0), Foreground = SettingsRows.Muted };
        var startup = new CheckBox { Content = "登录 Windows 时启动", Margin = new Thickness(0, 0, 0, 18) };
        try
        {
            startup.IsChecked = store.IsStartupEnabled();
            startup.Click += (_, _) =>
            {
                var enabled = startup.IsChecked == true;
                try
                {
                    store.SetStartupEnabled(enabled);
                    startupStatus.Text = enabled ? "已添加启动项。" : "已移除启动项。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
                {
                    startup.IsChecked = !enabled;
                    startupStatus.Text = "无法修改当前用户启动项。";
                }
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            startup.IsEnabled = false;
            startupStatus.Text = "无法读取当前用户的启动项。";
        }
        var startupPage = new StackPanel { Children = { SettingsRows.Row("开机自动启动", startup), startupStatus } };
        var sources = store.LoadSources();
        var toggles = new Dictionary<string, CheckBox>();
        foreach (var (id, label) in new (string, string)[]
        {
            ("music", "音乐"), ("clipboard", "剪贴板"), ("notifications", "微信及企业微信通知"),
            ("files", "文件变动"), ("mail", "邮件"), ("tasks", "微软待办"),
            ("system-monitor", "系统监控（收起时常驻）"), ("device-battery", "设备电量")
        })
        {
            var toggle = new CheckBox
            {
                IsChecked = SettingsStore.SourceEnabled(sources, id),
                Content = label,
                Margin = new Thickness(0, 0, 0, 9)
            };
            toggles.Add(id, toggle);
            content.Children.Add(SettingsRows.Row(label, toggle));
        }
        var save = new Button
        {
            Content = "保存数据源设置",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 8, 0, 0)
        };
        save.Click += async (_, _) =>
        {
            foreach (var (id, toggle) in toggles)
            {
                sources[id] = toggle.IsChecked == true;
            }
            try
            {
                store.SaveSources(sources);
                await applySources(sources);
                status.Text = "已保存到本机。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "保存失败，请检查本机配置目录的写入权限。";
            }
        };
        content.Children.Add(save);
        content.Children.Add(status);
        var general = _general = new GeneralSettings(preferences, applyPreferences);
        var sidebar = new StackPanel { Margin = new Thickness(10, 24, 10, 16) };
        var brand = new TextBlock { Text = "灵动岛", FontSize = 28, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(11, 0, 0, 23), Foreground = SettingsRows.Ink };
        sidebar.Children.Add(brand);
        var navigation = new List<Button>();
        var heading = new TextBlock { FontSize = 27, FontWeight = FontWeights.SemiBold,
            Foreground = SettingsRows.Ink, Margin = new Thickness(0, 0, 0, 19) };
        var page = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        var main = new DockPanel { Margin = new Thickness(35, 31, 30, 22), MaxWidth = 870 };
        DockPanel.SetDock(heading, Dock.Top);
        main.Children.Add(heading);
        main.Children.Add(page);
        void Category(string label) => sidebar.Children.Add(new TextBlock
        {
            Text = label, Foreground = SettingsRows.Muted, FontSize = 11, Margin = new Thickness(11, 15, 0, 5)
        });
        void AddPage(string title, string glyph, FrameworkElement body, string? section = null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 24, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } }
            };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            button.SetResourceReference(StyleProperty, "SettingsNav");
            button.Click += (_, _) =>
            {
                foreach (var item in navigation) item.Tag = item == button ? "selected" : null;
                heading.Text = title;
                page.Content = body;
                if (section is not null) general.ShowSection(section);
            };
            navigation.Add(button);
            sidebar.Children.Add(button);
        }
        Category("基础设置");
        AddPage("常规", "\uE713", startupPage);
        AddPage("收起外观", "\uE8A9", general, "岛体外观");
        AddPage("展开外观", "\uE740", general, "展开外观");
        AddPage("显示器与时钟", "\uE7F4", general, "岛体显示");
        AddPage("窗口行为", "\uE737", general, "窗口行为");
        Category("功能与连接");
        AddPage("数据源", "\uE90F", new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AddPage("AI 额度", "\uE9D9", new QuotaSettingsPanel(quotaAccounts, refreshQuotas, metricsFor, general.QuotaAppearance));
        Category("关于");
        AddPage("版本与更新", "\uE895", new ReleaseSettings());
        var rail = new Border { Width = 192, BorderBrush = SettingsRows.Rule, BorderThickness = new Thickness(0, 0, 1, 0),
            Background = SettingsRows.Brush("#EFF1F5"),
            Child = new ScrollViewer { Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        DockPanel.SetDock(rail, Dock.Left);
        Content = new DockPanel
        {
            Background = SettingsRows.Canvas,
            Children = { rail, main }
        };
        navigation[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
