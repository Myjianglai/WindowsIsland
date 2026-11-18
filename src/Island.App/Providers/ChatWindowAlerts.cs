using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Island.App.UI;
using Island.Core.Events;

namespace Island.App.Providers;

internal sealed class ChatWindowAlerts : IDisposable
{
    private const int ShellFlash = 0x8006;
    private readonly HwndSource _window;
    private readonly IslandEventBus _events;
    private readonly uint _message = RegisterWindowMessage("SHELLHOOK");
    private readonly MessageSources _sources;

    public ChatWindowAlerts(HwndSource window, IslandEventBus events, MessageSources sources)
    {
        _window = window;
        _events = events;
        _sources = sources;
        if (_message != 0 && RegisterShellHookWindow(_window.Handle)) _window.AddHook(Handle);
    }

    private IntPtr Handle(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        var options = ActivityPanel.Options;
        if (!options.Messages || message != _message || wparam.ToInt64() != ShellFlash) return IntPtr.Zero;
        GetWindowThreadProcessId(lparam, out uint pid);
        if (pid == 0) return IntPtr.Zero;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string? source = process.ProcessName switch
            {
                "Weixin" or "WeChat" when options.WeChat => "微信",
                "WXWork" or "WeCom" when options.WeCom => "企业微信",
                _ => null
            };
            if (source is null) return IntPtr.Zero;
            _sources.PublishChatAlert(_events, source, "有新提醒", null, fromFlash: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_window.IsDisposed) return;
        _window.RemoveHook(Handle);
        DeregisterShellHookWindow(_window.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool RegisterShellHookWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool DeregisterShellHookWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
