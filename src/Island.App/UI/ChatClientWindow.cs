using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Win32;

namespace Island.App.UI;

internal static class ChatClientWindow
{
    public static async Task<bool> OpenAsync(string source)
    {
        bool enterprise = source == "企业微信";
        string[] names = enterprise ? ["WXWork", "WeCom"] : ["Weixin", "WeChat"];
        var processes = FindProcesses(names, out var executable);
        var window = FindMainWindow(processes, enterprise);
        if (await ActivateAsync(window)) return true;
        if (processes.Count > 0)
        {
            // Launching the executable again can open a new sign-in window.
            if (await Task.Run(() => InvokeTrayIcon(enterprise)) && await WaitForActiveClient()) return true;
            return enterprise && await Task.Run(() => InvokeTaskbarButton(executable)) && await WaitForActiveClient();

            async Task<bool> WaitForActiveClient()
            {
                for (int attempt = 0; attempt < 18; attempt++)
                {
                    await Task.Delay(150);
                    if (await ActivateAsync(FindMainWindow(processes, enterprise))) return true;
                    if (enterprise && IsEnterprisePreviewActive(processes)) return true;
                }
                return false;
            }
        }
        executable ??= FindInstalledExecutable(names, enterprise ? ["企业微信", "WeCom"] : ["微信", "WeChat", "Weixin"]);
        if (executable is null) return false;
        try
        {
            using var launched = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            });
            for (int attempt = 0; attempt < 16; attempt++)
            {
                await Task.Delay(250);
                if (await ActivateAsync(FindMainWindow(FindProcesses(names, out _), enterprise))) return true;
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private static bool IsEnterprisePreviewActive(HashSet<uint> processes)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) || !IsWindowEnabled(window)) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return processes.Contains(pid) && WindowClass(window) == "ImagePreview";
    }

    private static bool InvokeTaskbarButton(string? executable)
    {
        if (executable is null) return false;
        try
        {
            var root = AutomationElement.RootElement;
            var bars = root.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd")));
            foreach (AutomationElement bar in bars)
            foreach (AutomationElement button in bar.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true)))
            {
                if (button.Current.IsOffscreen ||
                    !button.Current.AutomationId.Equals("Appid: " + executable, StringComparison.OrdinalIgnoreCase) ||
                    !button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) continue;
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return false;
    }

    private static bool InvokeTrayIcon(bool enterprise)
    {
        try
        {
            var root = AutomationElement.RootElement;
            var taskbars = root.FindAll(TreeScope.Children, new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd")));
            InvokePattern? overflowButton = null;
            foreach (AutomationElement taskbar in taskbars)
            {
                if (TryInvokeChatIcon(taskbar, enterprise)) return true;
                var toggle = taskbar.FindFirst(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "显示隐藏的图标"),
                    new PropertyCondition(AutomationElement.NameProperty, "Show hidden icons")));
                if (toggle?.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern) == true)
                    overflowButton = (InvokePattern)pattern;
            }
            var overflow = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"));
            bool TryOverflow()
            {
                foreach (AutomationElement panel in root.FindAll(TreeScope.Children, overflow))
                    if (!panel.Current.IsOffscreen && TryInvokeChatIcon(panel, enterprise)) return true;
                return false;
            }
            if (TryOverflow()) return true;
            if (overflowButton is null) return false;
            overflowButton.Invoke();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                System.Threading.Thread.Sleep(75);
                if (TryOverflow()) return true;
            }
            overflowButton.Invoke();
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return false;
    }

    private static bool TryInvokeChatIcon(AutomationElement container, bool enterprise)
    {
        string[] names = enterprise ? ["企业微信", "WeCom", "WeChat Work"] : ["微信", "WeChat", "Weixin"];
        var controls = container.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true));
        foreach (AutomationElement icon in controls)
        {
            var info = icon.Current;
            string label = info.Name.Trim();
            if (info.IsOffscreen || !names.Any(name => label.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith(name + "：", StringComparison.OrdinalIgnoreCase))) continue;
            if (!icon.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) continue;
            ((InvokePattern)pattern).Invoke();
            return true;
        }
        return false;
    }

    private static HashSet<uint> FindProcesses(string[] names, out string? executable)
    {
        var processes = new HashSet<uint>();
        executable = null;
        foreach (var process in names.SelectMany(Process.GetProcessesByName))
        {
            using var app = process;
            processes.Add((uint)app.Id);
            try { executable ??= MatchExecutable(app.MainModule?.FileName, names); }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        return processes;
    }

    private static string? FindInstalledExecutable(string[] names, string[] labels)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (string name in names)
                {
                    using var app = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}.exe");
                    if (MatchExecutable(app?.GetValue(null) as string, names) is { } appPath) return appPath;
                }
                using var entries = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string subkey in entries?.GetSubKeyNames() ?? [])
                {
                    using var entry = entries!.OpenSubKey(subkey);
                    if (entry?.GetValue("DisplayName") is not string label ||
                        !labels.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase)) continue;
                    if (MatchExecutable(entry.GetValue("DisplayIcon") as string, names) is { } iconPath) return iconPath;
                    if (entry.GetValue("InstallLocation") is not string folder || string.IsNullOrWhiteSpace(folder)) continue;
                    foreach (string name in names)
                        if (MatchExecutable(Path.Combine(folder.Trim().Trim('"'), name + ".exe"), names) is { } installPath) return installPath;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        return FindStartMenuExecutable(names, labels);
    }

    private static string? FindStartMenuExecutable(string[] names, string[] labels)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return null;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                string directory = Environment.GetFolderPath(folder);
                if (!Directory.Exists(directory)) continue;
                var entries = Directory.EnumerateFiles(directory, "*.lnk",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
                foreach (string file in entries)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!labels.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                        !names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    object? shortcut = null;
                    try
                    {
                        shortcut = ((dynamic)shell).CreateShortcut(file);
                        if (MatchExecutable((string)((dynamic)shortcut).TargetPath, names) is { } target) return target;
                    }
                    catch (COMException) { }
                    finally { if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut); }
                }
            }
        }
        catch (COMException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        return null;
    }

    private static string? MatchExecutable(string? value, string[] names)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string path = Environment.ExpandEnvironmentVariables(value.Trim());
        if (path.LastIndexOf(',') is int comma && comma >= 0 && int.TryParse(path[(comma + 1)..], out _))
            path = path[..comma].TrimEnd();
        path = path.Trim('"');
        return Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    private static IntPtr FindMainWindow(HashSet<uint> processes, bool enterprise)
    {
        if (processes.Count == 0) return IntPtr.Zero;
        var window = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint pid);
            // A tray-hidden Qt window must be restored by the client itself.
            if (!processes.Contains(pid) || !IsWindowVisible(handle) || !IsWindowEnabled(handle) ||
                GetWindow(handle, 4) != IntPtr.Zero) return true;
            var name = WindowClass(handle);
            if (enterprise)
            {
                if (name != "WeWorkWindow") return true;
            }
            else if (name != "WeChatMainWndForPC")
            {
                if (!name.StartsWith("Qt", StringComparison.Ordinal) || !name.EndsWith("QWindowIcon", StringComparison.Ordinal))
                    return true;
                var title = new StringBuilder(64);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString() is not ("微信" or "WeChat" or "Weixin")) return true;
            }
            window = handle;
            return false;
        }, IntPtr.Zero);
        return window;
    }

    private static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static async Task<bool> ActivateAsync(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window) || !IsWindowEnabled(window)) return false;
        if (IsIconic(window)) ShowWindowAsync(window, 9);
        SetForegroundWindow(window);
        if (GetForegroundWindow() == window) return true;
        await Task.Delay(100);
        if (!IsWindowVisible(window) || !IsWindowEnabled(window)) return false;
        SetForegroundWindow(window);
        return GetForegroundWindow() == window;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
