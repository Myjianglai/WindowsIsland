using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Island.App.Persistence;
using Island.App.Presentation;
using Island.App.Providers;
using Island.App.UI;
using Island.Core.Events;
using Forms = System.Windows.Forms;

namespace Island.App;

public partial class App : Application
{
    private readonly IslandEventBus _events = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SettingsStore _store = new();
    private readonly QuotaAccountStore _quotaAccounts = new();
    private readonly QuotaProvider _quotas;
    private Forms.NotifyIcon? _tray;
    private Icon? _appIcon;
    private SettingsWindow? _settings;
    private ProviderHost? _music;
    private ProviderHost? _clipboard;
    private ProviderHost? _systemMonitor;
    private ProviderHost? _deviceBattery;
    private Mutex? _instance;
    private IslandPreferences _preferences = IslandPreferences.Load();
    private readonly DispatcherTimer _savePreferences = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly MusicProvider _musicProvider = new();
    private const int ClipboardHotkey = 0x517;
    private IntPtr _hotkeyWindow;
    private ClipboardOptions? _boundShortcut;
    private ChatWindowAlerts? _chatAlerts;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    private bool BindClipboardShortcut(ClipboardOptions options)
    {
        if (_hotkeyWindow == IntPtr.Zero) return false;
        var old = _boundShortcut;
        UnregisterHotKey(_hotkeyWindow, ClipboardHotkey);
        bool Register(ClipboardOptions value) => RegisterHotKey(_hotkeyWindow, ClipboardHotkey,
            (uint)value.Modifiers | 0x4000, (uint)KeyInterop.VirtualKeyFromKey(value.Key));
        if (options.Shortcut && !Register(options))
        {
            if (old is { Shortcut: true }) Register(old);
            return false;
        }
        _boundShortcut = options;
        return true;
    }

    public App()
    {
        _quotas = new QuotaProvider(_quotaAccounts);
        _savePreferences.Tick += (_, _) => SavePreferences();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ReleaseInstaller.TryRun(e.Args))
        {
            Shutdown();
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        _instance = new Mutex(false, $@"Local\WindowsIsland.{identity.User?.Value}", out bool firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }
        var window = new IslandWindow(OpenSettings, _quotas.Refresh,
            () => { OpenSettings(); _settings?.ShowMessageSettings?.Invoke(); });
        window.DockChanged += preferences =>
        {
            _settings?.SyncDock(preferences);
            ChangePreferences(preferences);
        };
        var presenter = new IslandPresenter(window);
        MainWindow = window;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add("刷新 AI 额度", null, (_, _) => _quotas.Refresh());
        menu.Items.Add("演示通知", null, (_, _) =>
        {
            _events.TryPublish(new IslandEvent("demo", IslandEventType.Notification,
                "第一条通知", "横幅会自动收起", timeout: TimeSpan.FromSeconds(3)));
            _events.TryPublish(new IslandEvent("demo", IslandEventType.Notification,
                "第二条通知", "上一条收起后显示", timeout: TimeSpan.FromSeconds(3)));
        });
        menu.Items.Add("重置位置", null, (_, _) => window.ResetPosition());
        menu.Items.Add("退出", null, (_, _) => Shutdown());
        if (Environment.ProcessPath is { } path)
            _appIcon = Icon.ExtractAssociatedIcon(path);
        _tray = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _appIcon ?? SystemIcons.Application,
            Text = "Windows 灵动岛",
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        window.Show();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            ReleaseInstaller.Cleanup();
        });
        ApplyPreferences(_preferences);
        window.CheckAgentActivity(_events.TryPublish, restoring: true);
        _music = new ProviderHost(_musicProvider, _events,
            () => MusicProvider.Status("请在设置中启用音乐"),
            () => MusicProvider.Status("无法连接 Windows 播放会话"), _shutdown.Token);
        var hwnd = new WindowInteropHelper(window).Handle;
        var messageSources = new MessageSources();
        _chatAlerts = new ChatWindowAlerts(HwndSource.FromHwnd(hwnd), _events, messageSources);
        _hotkeyWindow = hwnd;
        ClipboardSettings.BindShortcut = BindClipboardShortcut;
        BindClipboardShortcut(ClipboardSettings.Options);
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr target, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
        {
            if (message == 0x0312 && wparam.ToInt32() == ClipboardHotkey && ClipboardSettings.Enabled)
            {
                Dispatcher.BeginInvoke(new Action(presenter.OpenClipboard));
                handled = true;
            }
            return IntPtr.Zero;
        });
        _clipboard = new ProviderHost(new ClipboardProvider(HwndSource.FromHwnd(hwnd)), _events,
            () => new IslandEvent(ClipboardProvider.SourceId, IslandEventType.Clipboard, "请在设置中启用剪贴板"),
            () => new IslandEvent(ClipboardProvider.SourceId, IslandEventType.Clipboard, "无法连接系统剪贴板"),
            _shutdown.Token);
        _systemMonitor = new ProviderHost(new SystemMonitorProvider(), _events,
            () => new IslandEvent(SystemMonitorProvider.SourceId, IslandEventType.SystemMetrics, "系统监控已关闭"),
            () => new IslandEvent(SystemMonitorProvider.SourceId, IslandEventType.SystemMetrics, "无法读取系统监控"), _shutdown.Token);
        _deviceBattery = new ProviderHost(new DeviceBatteryProvider(Dispatcher), _events,
            () => new IslandEvent(DeviceBatteryProvider.SourceId, IslandEventType.DeviceBattery, "请在设置中启用设备电量"),
            () => new IslandEvent(DeviceBatteryProvider.SourceId, IslandEventType.DeviceBattery, "无法读取设备电量"), _shutdown.Token);

        try
        {
            var quotaHost = new ProviderHost(_quotas, _events,
                () => new IslandEvent(QuotaProvider.SourceId, IslandEventType.Quota, "AI 额度"),
                () => new IslandEvent(QuotaProvider.SourceId, IslandEventType.Quota, "无法启动额度数据源"), _shutdown.Token);
            await Task.WhenAll(presenter.RunAsync(_events, _shutdown.Token),
                ApplySourcesAsync(_store.LoadSources()), quotaHost.SetEnabledAsync(true),
                messageSources.RunAsync(_events, _shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 正常退出，停止横幅调度。
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _chatAlerts?.Dispose();
        if (_hotkeyWindow != IntPtr.Zero) UnregisterHotKey(_hotkeyWindow, ClipboardHotkey);
        if (_savePreferences.IsEnabled) SavePreferences();
        _shutdown.Cancel();
        _events.Complete();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _appIcon?.Dispose();
        _shutdown.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_store, ApplySourcesAsync, _quotaAccounts, _quotas.Refresh,
                _preferences, ChangePreferences, _quotas.MetricsFor);
            _settings.ExitRequested += Shutdown;
            if (MainWindow is IslandWindow island)
            {
                var settings = _settings;
                settings.UpdateDevices(island.Devices);
                island.DevicesChanged += settings.UpdateDevices;
                settings.Closed += (_, _) => island.DevicesChanged -= settings.UpdateDevices;
            }
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.Activate();
    }

    private void ApplyPreferences(IslandPreferences preferences)
    {
        _preferences = preferences;
        _musicProvider.SetLyricsOptions(preferences.LyricsFolder, preferences.NeteaseOnlineLyrics, preferences.GeneralOnlineLyrics);
        if (MainWindow is IslandWindow island)
        {
            island.ApplyPreferences(preferences);
        }
    }

    private void ChangePreferences(IslandPreferences preferences)
    {
        ApplyPreferences(preferences);
        _savePreferences.Stop();
        _savePreferences.Start();
    }

    private void SavePreferences()
    {
        _savePreferences.Stop();
        try
        {
            _preferences.Save();
            _settings?.ShowPreferencesSaveError(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings?.ShowPreferencesSaveError("更改已生效，但无法保存到本机。");
        }
    }

    private async Task ApplySourcesAsync(Dictionary<string, bool> sources)
    {
        if (_music is not null && _clipboard is not null && _systemMonitor is not null && _deviceBattery is not null)
        {
            try
            {
                await Task.WhenAll(
                    _music.SetEnabledAsync(SettingsStore.SourceEnabled(sources, MusicProvider.SourceId)),
                    _clipboard.SetEnabledAsync(SettingsStore.SourceEnabled(sources, ClipboardProvider.SourceId)),
                    _systemMonitor.SetEnabledAsync(SettingsStore.SourceEnabled(sources, SystemMonitorProvider.SourceId)),
                    _deviceBattery.SetEnabledAsync(SettingsStore.SourceEnabled(sources, DeviceBatteryProvider.SourceId)));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // 应用正在退出。
            }
        }
    }
}
