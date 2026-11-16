using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Island.App.UI;

internal sealed class ReleaseSettings : StackPanel
{
    private const string ReleasesPage = "https://github.com/Myjianglai/WindowsIsland/releases";
    private const string ReleasesApi = "https://api.github.com/repos/Myjianglai/WindowsIsland/releases/latest";
    private static readonly Version Current = typeof(ReleaseSettings).Assembly.GetName().Version!;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public ReleaseSettings()
    {
        var check = new Button { Content = "检查更新" };
        var open = new Button { Content = "打开 GitHub 发布页" };
        var status = new TextBlock
        {
            Foreground = SettingsRows.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Text = "手动检查会使用 Git Credential Manager 中已有的 GitHub 授权。你也可以在浏览器中登录后查看发布页。"
        };
        Children.Add(SettingsRows.Row($"当前版本 {Current.ToString(3)}", check));
        Children.Add(SettingsRows.Row("下载新版本", open));
        Children.Add(status);

        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
            catch (Win32Exception) { status.Text = "无法打开浏览器。"; }
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            status.Text = "正在检查 GitHub 发布版本…";
            try { status.Text = await CheckAsync(); }
            catch (OperationCanceledException) { status.Text = "检查超时。可重试或打开发布页。"; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or System.IO.IOException)
            {
                status.Text = "暂时无法读取 GitHub 发布信息。";
            }
            finally { check.IsEnabled = true; }
        };
    }

    private static async Task<string> CheckAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = await ReadGitHubCredentialAsync(timeout.Token);
        if (string.IsNullOrEmpty(token))
            return "本机没有可用的 GitHub 授权。请在浏览器中登录后查看发布页。";

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("WindowsIsland/" + Current.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return "GitHub 未返回可读取的发布版本。请确认账号有仓库访问权限，或打开发布页查看。";
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!document.RootElement.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String ||
            !Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var latest))
            return "GitHub 发布标签无法识别。请打开发布页查看。";

        return latest > Version.Parse(Current.ToString(3))
            ? $"发现新版本 {latest}。可打开发布页下载。"
            : $"当前已是最新版本 {Current.ToString(3)}。";
    }

    private static async Task<string?> ReadGitHubCredentialAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git", "credential-manager get")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_TRACE"] = "0";
        start.Environment["GCM_TRACE_SECRETS"] = "0";
        try
        {
            using var git = Process.Start(start);
            if (git is null) return null;
            try
            {
                // 仅在手动检查时向凭据管理器请求 GitHub 授权，不写入应用配置。
                await git.StandardInput.WriteAsync("protocol=https\nhost=github.com\nusername=Myjianglai\n\n");
                git.StandardInput.Close();
                var result = await git.StandardOutput.ReadToEndAsync(cancellationToken);
                await git.WaitForExitAsync(cancellationToken);
                return git.ExitCode == 0
                    ? result.Split('\n').FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..].TrimEnd('\r')
                    : null;
            }
            catch (OperationCanceledException)
            {
                try { git.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                throw;
            }
        }
        catch (Win32Exception) { return null; }
    }
}
