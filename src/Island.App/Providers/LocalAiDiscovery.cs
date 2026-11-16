using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.Providers;

internal static class LocalAiDiscovery
{
    public static IEnumerable<QuotaAccount> Detect()
    {
        var cli = FindCodex();
        if (cli is not null)
            yield return new(Guid.NewGuid(), QuotaService.Codex, "Codex", "Blue", false, cli);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (File.Exists(QuotaAccountStore.DefaultClaudeStatus) || FindOnPath("claude.exe") is not null
            || FindOnPath("claude.cmd") is not null || IsRunning("Claude")
            || File.Exists(Path.Combine(local, "AnthropicClaude", "Claude.exe"))
            || File.Exists(Path.Combine(local, "Programs", "Claude", "Claude.exe")))
            yield return new(Guid.NewGuid(), QuotaService.Claude, "Claude", "Orange", false);

        var cursorCli = FindOnPath("cursor-agent.exe");
        if (cursorCli is not null || IsRunning("Cursor") || File.Exists(Path.Combine(local, "Programs", "cursor", "Cursor.exe"))
            || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cursor", "Cursor.exe")))
            yield return new(Guid.NewGuid(), QuotaService.Cursor, "Cursor", "Purple", false, cursorCli ?? "");
    }

    private static bool IsRunning(string name)
    {
        var matches = Process.GetProcessesByName(name);
        foreach (var process in matches) process.Dispose();
        return matches.Length > 0;
    }

    public static string? FindCodex()
    {
        var direct = FindOnPath("codex.exe");
        if (direct is not null)
            return direct;
        string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        string platform = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        foreach (var directory in PathDirectories().Append(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")))
        {
            var root = Path.Combine(directory, "node_modules", "@openai", "codex");
            var platformPackage = "codex-win32-" + architecture;
            foreach (var vendor in new[] { Path.Combine(root, "node_modules", "@openai", platformPackage, "vendor"),
                Path.Combine(directory, "node_modules", "@openai", platformPackage, "vendor"), Path.Combine(root, "vendor") })
            {
                var binary = Path.Combine(vendor, platform, "bin", "codex.exe");
                if (File.Exists(binary)) return binary;
                binary = Path.Combine(vendor, platform, "codex", "codex.exe");
                if (File.Exists(binary)) return binary;
            }
        }
        return null;
    }

    public static string? FindOnPath(string file) =>
        PathDirectories().Select(directory => Path.Combine(directory, file)).FirstOrDefault(File.Exists);

    private static IEnumerable<string> PathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase);

    public static async Task<QuotaAccountStatus> CursorStatusAsync(QuotaAccount account, CancellationToken token)
    {
        var path = FindOnPath("cursor-agent.exe");
        if (path is null)
            return QuotaProvider.Status(account, "未知", "已识别 Cursor，个人额度请在官方用量面板查看");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("status");
        start.Environment.Remove("CURSOR_API_KEY");
        using var process = Process.Start(start);
        if (process is null)
            return QuotaProvider.Status(account, "未知", "无法检查 Cursor 登录状态");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            var result = await output;
            bool loggedIn = process.ExitCode == 0 && !result.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                && !result.Contains("not logged", StringComparison.OrdinalIgnoreCase)
                && (result.Contains("authenticated", StringComparison.OrdinalIgnoreCase) || result.Contains("logged in", StringComparison.OrdinalIgnoreCase));
            return QuotaProvider.Status(account, "未知", loggedIn ? "Cursor CLI 已登录，个人额度请在官方用量面板查看"
                : "Cursor 登录状态未知，个人额度请在官方用量面板查看");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
