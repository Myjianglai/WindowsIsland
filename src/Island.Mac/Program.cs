using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Island.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--agent-hook", var source])
        {
            AgentHooks.Receive(source);
            return;
        }
        AppBuilder.Configure<IslandApplication>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class IslandApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new IslandWindow();
            desktop.MainWindow = window;
            using var iconStream = AssetLoader.Open(new Uri("avares://IslandMac/Island.png"));
            var icon = new WindowIcon(iconStream);
            window.Icon = icon;
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示灵动岛");
            show.Click += (_, _) => window.Reveal();
            var settings = new NativeMenuItem("设置");
            settings.Click += (_, _) => window.Reveal("设置");
            var quit = new NativeMenuItem("退出");
            quit.Click += (_, _) => desktop.Shutdown();
            menu.Items.Add(show);
            menu.Items.Add(settings);
            menu.Items.Add(quit);
            TrayIcon.SetIcons(this, new TrayIcons { new TrayIcon { Icon = icon, ToolTipText = "灵动岛", Menu = menu } });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
