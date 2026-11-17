using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Island.App.Persistence;

internal static class ReleaseInstaller
{
    public const string InstallArgument = "--install-island-update";
    private const string WorkDirectory = ".windows-island-update";
    private const string PackageName = "package.exe";
    private const string HelperName = "helper.exe";
    private const string PlanName = "install.json";
    private const string BackupName = "previous.exe";
    private sealed record Plan(string TargetName, int ProcessId, long StartTime, string Sha256);

    public static string PrepareDownload()
    {
        var target = RunningExecutable();
        var version = typeof(ReleaseInstaller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is null || FileVersionInfo.GetVersionInfo(target).ProductVersion != version)
            throw new IOException("Automatic updates require the published Island executable.");
        var directory = CheckedDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(Path.Combine(directory, BackupName)))
            throw new IOException("A previous update backup is still present.");
        foreach (var name in new[] { PackageName, HelperName, PlanName })
            File.Delete(Path.Combine(directory, name));
        return Path.Combine(directory, PackageName);
    }

    public static void Cleanup()
    {
        var directory = Path.Combine(Path.GetDirectoryName(RunningExecutable())!, WorkDirectory);
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                File.Exists(Path.Combine(directory, PlanName)) || File.Exists(Path.Combine(directory, BackupName)) ||
                File.Exists(Path.Combine(directory, PackageName)))
                return;
            File.Delete(Path.Combine(directory, HelperName));
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Start(string sha256)
    {
        var target = RunningExecutable();
        var directory = CheckedDirectory(Path.GetDirectoryName(target)!);
        var helper = Path.Combine(directory, HelperName);
        using var process = Process.GetCurrentProcess();
        var plan = new Plan(Path.GetFileName(target), process.Id, process.StartTime.ToUniversalTime().Ticks, sha256);
        File.Copy(target, helper);
        File.WriteAllText(Path.Combine(directory, PlanName), JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add(InstallArgument);
        using var launched = Process.Start(start) ?? throw new IOException("Unable to launch the installer.");
    }

    public static bool TryRun(string[] args)
    {
        if (args.Length != 1 || args[0] != InstallArgument) return false;
        try { Install(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or Win32Exception or CryptographicException)
        {
            MessageBox.Show("无法完成更新。请重新打开灵动岛或从发布页下载新版。", "灵动岛更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return true;
    }

    private static void Install()
    {
        var helper = RunningExecutable();
        var directory = Path.GetDirectoryName(helper)!;
        var parent = Path.GetDirectoryName(directory) ?? throw new IOException("Invalid installation folder.");
        if (!string.Equals(helper, Path.Combine(CheckedDirectory(parent), HelperName), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid updater path.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(Path.Combine(directory, PlanName)))
            ?? throw new IOException("Missing update plan.");
        if (string.IsNullOrWhiteSpace(plan.TargetName) || Path.GetFileName(plan.TargetName) != plan.TargetName ||
            !plan.TargetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid update target.");
        var target = Path.Combine(parent, plan.TargetName);
        var package = Path.Combine(directory, PackageName);
        var backup = Path.Combine(directory, BackupName);
        WaitForOriginal(plan, target);
        using (var source = File.OpenRead(package))
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(source)), plan.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Incorrect package hash.");
        }
        File.Replace(package, target, backup);
        try
        {
            using var installed = Launch(target);
            if (installed.WaitForExit(3500))
                throw new IOException("New version exited before it finished starting.");
        }
        catch
        {
            File.Replace(backup, target, null);
            using var restored = Launch(target);
            throw;
        }
        File.Delete(backup);
        File.Delete(Path.Combine(directory, PlanName));
        // 当前 helper 仍被 Windows 占用，下一次下载时会清理。
    }

    private static Process Launch(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })
        ?? throw new IOException("Unable to start Island.");

    private static void WaitForOriginal(Plan plan, string target)
    {
        Process? process;
        try { process = Process.GetProcessById(plan.ProcessId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != plan.StartTime) return;
                if (!string.Equals(process.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The original Island path changed.");
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            if (!process.WaitForExit(20000))
                throw new IOException("Island did not exit before the update deadline.");
        }
    }

    private static string CheckedDirectory(string parent)
    {
        var directory = Directory.CreateDirectory(Path.Combine(parent, WorkDirectory));
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid update work directory.");
        return directory.FullName;
    }

    private static string RunningExecutable() => Environment.ProcessPath ?? throw new IOException("Missing executable path.");
}
