using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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
            if (!processes.Contains(pid) || GetWindow(handle, 4) != IntPtr.Zero) return true;
            var className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            var name = className.ToString();
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

    private static async Task<bool> ActivateAsync(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
        SetForegroundWindow(window);
        if (GetForegroundWindow() == window) return true;
        await Task.Delay(100);
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
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
