using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Island.App.Providers;

internal static class NeteaseSeek
{
    public static async ValueTask SetAsync(TimeSpan position, TimeSpan duration, CancellationToken token)
    {
        var fraction = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
        if (!await Task.Run(() => TrySet(fraction, token), token))
            throw new InvalidOperationException("找不到网易云的播放进度条，请保持播放器主窗口可见。");
    }

    private static bool TrySet(double fraction, CancellationToken token)
    {
        if (!double.IsFinite(fraction)) return false;
        foreach (var process in Process.GetProcessesByName("cloudmusic"))
        using (process)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OrpheusBrowserHost")));
                if (window is null || window.Current.IsOffscreen) continue;
                var progress = window.FindFirst(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "播放进度调节"),
                    new PropertyCondition(AutomationElement.NameProperty, "播放進度調節")));
                if (progress is null || progress.Current.IsOffscreen || !progress.Current.IsEnabled) continue;
                token.ThrowIfCancellationRequested();
                if (progress.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) && pattern is RangeValuePattern range)
                {
                    if (range.Current.IsReadOnly) continue;
                    double minimum = range.Current.Minimum;
                    range.SetValue(minimum + (range.Current.Maximum - minimum) * fraction);
                    return true;
                }

                // Chromium 皮肤的进度条可能只暴露位置。验证目标窗口后向该控件发送点击。
                var bounds = progress.Current.BoundingRectangle;
                var root = window.Current.BoundingRectangle;
                root.Inflate(3, 3);
                if (bounds.Width < 100 || bounds.Height < 1 || bounds.Height > 60 || !root.Contains(bounds)) continue;
                var point = new NativePoint
                {
                    X = (int)Math.Floor(bounds.Left + Math.Clamp(fraction * bounds.Width, 1, bounds.Width - 2)),
                    Y = (int)Math.Floor(bounds.Top + bounds.Height / 2)
                };
                var rootHandle = new IntPtr(window.Current.NativeWindowHandle);
                var handle = WindowFromPoint(point);
                if (handle == IntPtr.Zero || GetAncestor(handle, 2) != rootHandle)
                    handle = ChildAt(rootHandle, point);
                if (handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var pid) == 0 ||
                    pid != (uint)process.Id || !ScreenToClient(handle, ref point)) continue;
                nint coordinates = (nint)((point.X & 0xFFFF) | ((point.Y & 0xFFFF) << 16));
                return PostMessage(handle, 0x0201, 1, coordinates) && PostMessage(handle, 0x0202, 0, coordinates);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ElementNotAvailableException) { }
        }
        return false;
    }

    private static IntPtr ChildAt(IntPtr window, NativePoint screen)
    {
        for (int depth = 0; window != IntPtr.Zero && depth < 8; depth++)
        {
            var client = screen;
            if (!ScreenToClient(window, ref client)) return IntPtr.Zero;
            var child = ChildWindowFromPointEx(window, client, 7);
            if (child == IntPtr.Zero || child == window) return window;
            window = child;
        }
        return window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr handle, NativePoint point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, nuint wParam, nint lParam);
}
