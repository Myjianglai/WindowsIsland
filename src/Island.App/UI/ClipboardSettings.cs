using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Island.App.Persistence;
using Island.App.Providers;

namespace Island.App.UI;

internal sealed record ClipboardOptions(bool Images = true, int Keep = 30, int Visible = 4,
    bool Separate = false, bool Shortcut = false, Key Key = Key.W,
    ModifierKeys Modifiers = ModifierKeys.Control | ModifierKeys.Shift, int ClearMinutes = 0, bool Shelf = true);

internal static class ClipboardSettings
{
    public static ClipboardOptions Options { get; private set; } = Load();
    public static bool Enabled { get; private set; } = SettingsStore.SourceEnabled(new SettingsStore().LoadSources(), ClipboardProvider.SourceId);
    public static event Action? Changed;
    public static Func<ClipboardOptions, bool>? BindShortcut;

    private static ClipboardOptions Load()
    {
        try { return LocalDataStore.ReadProtected<ClipboardOptions>("clipboard-options.bin") ?? new(); }
        catch (Exception) { return new(); }
    }

    public static FrameworkElement Build(SettingsStore store, Func<System.Collections.Generic.Dictionary<string, bool>, System.Threading.Tasks.Task> apply)
    {
        var panel = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Save(ClipboardOptions next)
        {
            var previous = Options;
            if ((next.Shortcut != previous.Shortcut || next.Key != previous.Key || next.Modifiers != previous.Modifiers) &&
                BindShortcut?.Invoke(next) == false)
            { status.Text = "这个快捷键无法注册"; return; }
            try
            {
                LocalDataStore.WriteProtected("clipboard-options.bin", next);
                Options = next;
                Changed?.Invoke();
                status.Text = "已保存";
            }
            catch (Exception) { BindShortcut?.Invoke(previous); status.Text = "无法保存剪贴板设置"; }
        }
        void Toggle(string title, bool value, Func<bool, ClipboardOptions> update)
        {
            var box = new CheckBox { IsChecked = value };
            box.Checked += (_, _) => Save(update(true));
            box.Unchecked += (_, _) => Save(update(false));
            panel.Children.Add(SettingsRows.Row(title, box));
        }
        void Stepper(string title, int min, int max, Func<int> read, Func<int, ClipboardOptions> update)
        {
            var value = new TextBlock { Text = read().ToString(), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (int delta in new[] { -1, 1 })
            {
                var button = new Button { Content = delta < 0 ? "−" : "＋", Width = 26 };
                button.Click += (_, _) => { Save(update(Math.Clamp(read() + delta, min, max))); value.Text = read().ToString(); };
                if (delta > 0) controls.Children.Add(value);
                controls.Children.Add(button);
            }
            panel.Children.Add(SettingsRows.Row(title, controls));
        }
        SettingsRows.Section(panel, "剪贴板与文件");
        var enabled = new CheckBox { IsChecked = SettingsStore.SourceEnabled(store.LoadSources(), ClipboardProvider.SourceId) };
        enabled.Click += async (_, _) =>
        {
            try
            {
                var sources = store.LoadSources();
                sources[ClipboardProvider.SourceId] = enabled.IsChecked == true;
                store.SaveSources(sources);
                await apply(sources);
                Enabled = enabled.IsChecked == true;
                Changed?.Invoke();
            }
            catch (Exception) { status.Text = "无法更新剪贴板数据源"; }
        };
        panel.Children.Add(SettingsRows.Row("启用剪贴板历史", enabled));
        SettingsRows.Section(panel, "剪贴板历史");
        Toggle("记录图片", Options.Images, x => Options with { Images = x });
        Stepper("最多保存几条", 1, 100, () => Options.Keep, x => Options with { Keep = x });
        Stepper("一次显示几条", 1, 10, () => Options.Visible, x => Options with { Visible = x });
        Toggle("用独立窗口打开剪贴板", Options.Separate, x => Options with { Separate = x });
        SettingsRows.Section(panel, "快捷键");
        Toggle("启用剪贴板快捷键", Options.Shortcut, x => Options with { Shortcut = x });
        string Caption() => $"{Options.Modifiers.ToString().Replace(", ", "+")}+{Options.Key}";
        var shortcut = new Button { Content = Caption() };
        bool capture = false;
        shortcut.Click += (_, _) => { capture = true; shortcut.Content = "按下组合键，Esc 取消"; };
        shortcut.LostKeyboardFocus += (_, _) => { capture = false; shortcut.Content = Caption(); };
        shortcut.PreviewKeyDown += (_, e) =>
        {
            if (!capture) return;
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.Escape)
            {
                if (key is >= Key.LeftShift and <= Key.RightAlt || Keyboard.Modifiers == ModifierKeys.None) return;
                Save(Options with { Key = key, Modifiers = Keyboard.Modifiers });
            }
            capture = false;
            shortcut.Content = Caption();
        };
        panel.Children.Add(SettingsRows.Row("展开快捷键", shortcut));
        SettingsRows.Section(panel, "文件中转");
        Toggle("启用文件中转", Options.Shelf, x => Options with { Shelf = x });
        int[] expiry = [0, 5, 30, 60, 1440];
        var clear = new ComboBox { ItemsSource = new[] { "从不", "5 分钟", "30 分钟", "1 小时", "1 天" },
            SelectedIndex = Math.Max(0, Array.IndexOf(expiry, Options.ClearMinutes)) };
        clear.SelectionChanged += (_, _) => Save(Options with { ClearMinutes = expiry[clear.SelectedIndex] });
        panel.Children.Add(SettingsRows.Row("自动清理", clear));
        panel.Children.Add(status);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
