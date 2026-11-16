using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Island.App.Persistence;
using Forms = System.Windows.Forms;

namespace Island.App.UI;

internal static class TaskbarDock
{
    public static (Rectangle Bounds, DockEdge Edge)? Find(Forms.Screen screen)
    {
        (Rectangle, DockEdge)? match = null;
        EnumWindows((handle, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(handle, name, name.Capacity);
            if (name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                || !IsWindowVisible(handle) || !GetWindowRect(handle, out var rect))
                return true;

            var bounds = Rectangle.Intersect(screen.Bounds, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            if (bounds.Width < 12 || bounds.Height < 12 || Forms.Screen.FromRectangle(bounds).DeviceName != screen.DeviceName)
                return true;

            DockEdge edge = bounds.Width >= bounds.Height
                ? (bounds.Top - screen.Bounds.Top < screen.Bounds.Bottom - bounds.Bottom ? DockEdge.Top : DockEdge.Bottom)
                : (bounds.Left - screen.Bounds.Left < screen.Bounds.Right - bounds.Right ? DockEdge.Left : DockEdge.Right);
            match = (bounds, edge);
            return false;
        }, IntPtr.Zero);
        return match;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
