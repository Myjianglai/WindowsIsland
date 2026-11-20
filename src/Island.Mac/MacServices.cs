using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace Island.Mac;

internal sealed record MusicTrack(string App, string Title, string Artist, double Position, bool Playing);
internal sealed record MailMessage(string Sender, string Subject, DateTimeOffset When);

internal static class MacServices
{
    public static async Task<string> Run(string executable, params string[] arguments)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("此功能需要 macOS。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("无法启动系统工具。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var stdout = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await child.WaitForExitAsync(timeout.Token);
            var result = await stdout;
            var error = await stderr;
            if (child.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "系统工具执行失败。" : error.Trim());
            return result.TrimEnd();
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            throw new IOException("系统工具响应超时。");
        }
    }

    public static Task<string> AppleScript(string script) => Run("/usr/bin/osascript", "-e", script);
    public static Task<string> Open(string path) => Run("/usr/bin/open", "--", path);
    public static Task<string> OpenApp(string name) => Run("/usr/bin/open", "-a", name);

    public static async Task<MusicTrack?> ReadMusic()
    {
        foreach (var app in new[] { "Music", "Spotify" })
        {
            var output = await AppleScript($"""
                if application "{app}" is running then
                    tell application "{app}"
                        if player state is not stopped then
                            return (name of current track) & linefeed & (artist of current track) & linefeed & (player position as text) & linefeed & (player state as text)
                        end if
                    end tell
                end if
                """);
            var fields = output.Split('\n');
            if (fields.Length != 4) continue;
            _ = double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var position);
            return new MusicTrack(app, fields[0], fields[1], position, fields[3].Trim() == "playing");
        }
        return null;
    }

    public static Task<string> MusicCommand(MusicTrack? track, string action)
    {
        if (action is not ("playpause" or "next track" or "previous track")) throw new ArgumentException(nameof(action));
        return AppleScript($"tell application \"{(track?.App == "Spotify" ? "Spotify" : "Music")}\" to {action}");
    }

    public static async Task<MailMessage[]> ReadMail(string host, int port, string address, string password)
    {
        using var client = new ImapClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, timeout.Token);
        await client.AuthenticateAsync(address, password, timeout.Token);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, timeout.Token);
        var ids = (await inbox.SearchAsync(SearchQuery.NotSeen, timeout.Token)).TakeLast(30).ToArray();
        if (ids.Length == 0) return [];
        var result = await inbox.FetchAsync(ids, MessageSummaryItems.Envelope, timeout.Token);
        return result.Where(row => row.Envelope is not null).Select(row => new MailMessage(row.Envelope!.From.ToString(),
            row.Envelope.Subject ?? "无主题", row.Envelope.Date ?? DateTimeOffset.Now))
            .OrderByDescending(message => message.When).ToArray();
    }

    public static async Task<string> ReadCodexQuota()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var path = new[] { "/opt/homebrew/bin", "/usr/local/bin", "/Applications/ChatGPT.app/Contents/Resources",
            "/Applications/Codex.app/Contents/Resources" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            .Where(Path.IsPathFullyQualified).Select(dir => Path.Combine(dir, "codex")).FirstOrDefault(File.Exists);
        if (path is null) throw new IOException("找不到 Codex CLI。");
        var start = new ProcessStartInfo(path, "app-server")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            async Task<JsonElement> Receive(int id)
            {
                while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("id", out var key) && key.ValueKind == JsonValueKind.Number && key.GetInt32() == id)
                        return root.TryGetProperty("result", out var value) ? value.Clone() : throw new IOException("Codex 未返回额度数据。");
                }
                throw new IOException("Codex App Server 已退出。");
            }
            await process.StandardInput.WriteLineAsync("""{"method":"initialize","id":1,"params":{"clientInfo":{"name":"island_mac","title":"Island Mac","version":"0.3.3"}}}""");
            await process.StandardInput.FlushAsync(token);
            await Receive(1);
            await process.StandardInput.WriteLineAsync("""{"method":"initialized"}""");
            await process.StandardInput.WriteLineAsync("""{"method":"account/rateLimits/read","id":2}""");
            await process.StandardInput.FlushAsync(token);
            var result = await Receive(2);
            if (!result.TryGetProperty("rateLimits", out var limits)) return "Codex 未返回额度数据";
            var values = new List<string>();
            foreach (string name in new[] { "primary", "secondary" })
                if (limits.TryGetProperty(name, out var slot) && slot.ValueKind == JsonValueKind.Object &&
                    slot.TryGetProperty("usedPercent", out var pct) && pct.TryGetDouble(out var used))
                    values.Add($"已用 {used:0.#}%");
            return values.Count == 0 ? "Codex 未返回用量百分比" : string.Join(" · ", values);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    public static string SummarizeBattery(string text) =>
        Regex.Match(text, @"\d+%[^\r\n]*") is { Success: true } match ? match.Value.Trim() : "未报告内置电池";
}
