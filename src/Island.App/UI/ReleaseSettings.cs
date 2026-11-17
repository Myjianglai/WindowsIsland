using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Island.App.Persistence;

namespace Island.App.UI;

internal sealed class ReleaseSettings : StackPanel
{
    private const string ReleasesPage = "https://github.com/Myjianglai/WindowsIsland/releases";
    private const string ReleasesApi = "https://api.github.com/repos/Myjianglai/WindowsIsland/releases/latest";
    private static readonly Version Current = typeof(ReleaseSettings).Assembly.GetName().Version!;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private sealed record ReleaseAsset(Version Version, long Id, long Size, string Sha256);

    public ReleaseSettings(Action restart)
    {
        var check = new Button { Content = "检查更新" };
        var install = new Button { Content = "下载并重启安装", IsEnabled = false };
        var open = new Button { Content = "打开 GitHub 发布页" };
        var status = new TextBlock
        {
            Foreground = SettingsRows.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Text = "检查和下载使用 Git Credential Manager 中已有的 GitHub 授权。"
        };
        Children.Add(SettingsRows.Row($"当前版本 {Current.ToString(3)}", check));
        Children.Add(SettingsRows.Row("安装新版本", install));
        Children.Add(SettingsRows.Row("发布说明", open));
        Children.Add(status);
        ReleaseAsset? available = null;

        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
            catch (Win32Exception) { status.Text = "无法打开浏览器。"; }
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            install.IsEnabled = false;
            available = null;
            status.Text = "正在检查 GitHub 发布版本…";
            try
            {
                var result = await CheckAsync();
                available = result.Asset;
                status.Text = result.Message;
            }
            catch (OperationCanceledException) { status.Text = "检查超时。可重试或打开发布页。"; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or System.IO.IOException)
            {
                status.Text = "暂时无法读取 GitHub 发布信息。";
            }
            finally
            {
                check.IsEnabled = true;
                install.IsEnabled = available is not null;
            }
        };
        install.Click += async (_, _) =>
        {
            if (available is not { } asset || !InstallGate.Wait(0)) return;
            install.IsEnabled = check.IsEnabled = false;
            string? path = null;
            bool started = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                authTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var token = await ReadGitHubCredentialAsync(authTimeout.Token);
                if (string.IsNullOrEmpty(token))
                {
                    status.Text = "本机没有可用的 GitHub 授权。可以打开发布页下载。";
                    return;
                }
                path = ReleaseInstaller.PrepareDownload();
                status.Text = $"正在下载 v{asset.Version}…";
                var progress = new Progress<int>(percent => status.Text = $"正在下载 v{asset.Version} · {percent}%");
                await DownloadAsync(asset, token, path, progress, timeout.Token);
                var fileVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+', 2)[0];
                if (!Version.TryParse(fileVersion, out var actual) || actual != asset.Version)
                    throw new InvalidDataException("Unexpected executable version.");
                status.Text = "校验通过，正在重启…";
                ReleaseInstaller.Start(asset.Sha256);
                started = true;
                restart();
            }
            catch (UnauthorizedAccessException)
            {
                status.Text = "当前 EXE 所在文件夹无法写入。可从发布页下载并替换。";
            }
            catch (OperationCanceledException)
            {
                status.Text = "下载超时，可以重试。";
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or Win32Exception or CryptographicException)
            {
                status.Text = "下载或校验失败，当前版本仍可使用。";
            }
            finally
            {
                if (!started && path is not null)
                {
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                InstallGate.Release();
                if (!started) install.IsEnabled = check.IsEnabled = true;
            }
        };
    }

    private static async Task<(string Message, ReleaseAsset? Asset)> CheckAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = await ReadGitHubCredentialAsync(timeout.Token);
        if (string.IsNullOrEmpty(token))
            return ("本机没有可用的 GitHub 授权。请在浏览器中登录后查看发布页。", null);

        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.UserAgent.ParseAdd("WindowsIsland/" + Current.ToString(3));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return ("GitHub 未返回可读取的发布版本。请确认账号有仓库访问权限，或打开发布页查看。", null);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!document.RootElement.TryGetProperty("tag_name", out var tag) ||
            tag.ValueKind != JsonValueKind.String ||
            !Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var latest))
            return ("GitHub 发布标签无法识别。请打开发布页查看。", null);

        if (latest <= Version.Parse(Current.ToString(3)))
            return ($"当前已是最新版本 {Current.ToString(3)}。", null);

        var asset = ReadAsset(document.RootElement, latest);
        return asset is null ? ($"发现新版本 {latest}，可打开发布页下载。", null)
            : ($"发现新版本 {latest}。", asset);
    }

    private static ReleaseAsset? ReadAsset(JsonElement root, Version version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in assets.EnumerateArray())
        {
            if (item.GetProperty("name").GetString() != "Island.exe" || item.GetProperty("state").GetString() != "uploaded")
                continue;
            if (item.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind == JsonValueKind.String &&
                digestValue.GetString() is { Length: 71 } digest && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
                digest[7..].All(Uri.IsHexDigit) && item.GetProperty("size").TryGetInt64(out long length) &&
                length is > 0 and <= 500_000_000 && item.GetProperty("id").TryGetInt64(out long id) && id > 0)
                return new ReleaseAsset(version, id, length, digest[7..]);
        }
        return null;
    }

    private static async Task DownloadAsync(ReleaseAsset asset, string token, string path, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/Myjianglai/WindowsIsland/releases/assets/{asset.Id}");
        request.Headers.UserAgent.ParseAdd("WindowsIsland/" + Current.ToString(3));
        request.Headers.Accept.ParseAdd("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Found && response.Headers.Location is { IsAbsoluteUri: true } location &&
            location.Scheme == Uri.UriSchemeHttps && location.Port == 443 && location.UserInfo.Length == 0 &&
            location.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com")
        {
            // 临时下载地址使用自己的签名，GitHub 授权只发给 API 主机。
            using var download = await Http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await SaveAssetAsync(download, asset, path, progress, cancellationToken);
            return;
        }
        await SaveAssetAsync(response, asset, path, progress, cancellationToken);
    }

    private static async Task SaveAssetAsync(HttpResponseMessage response, ReleaseAsset asset, string path, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int previous = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > asset.Size) throw new InvalidDataException("Update package is larger than expected.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            int percent = (int)(total * 100 / asset.Size);
            if (percent != previous) progress.Report(percent);
            previous = percent;
        }
        if (total != asset.Size ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256)))
            throw new InvalidDataException("Update package did not match GitHub's digest.");
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
