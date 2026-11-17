using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private const string ReleasesPage = "https://github.com/Myjianglai/WindowsIsland-Releases/releases";
    private static readonly Version Current = typeof(ReleaseSettings).Assembly.GetName().Version!;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private sealed record ReleaseAsset(Version Version, long Size, string Sha256);

    public ReleaseSettings(Action restart)
    {
        var check = new Button { Content = "检查更新" };
        var install = new Button { Content = "下载并重启安装", IsEnabled = false };
        var open = new Button { Content = "打开 GitHub 发布页" };
        var status = new TextBlock
        {
            Foreground = SettingsRows.Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Text = "更新文件通过公开发布页下载，无需登录。"
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
                path = ReleaseInstaller.PrepareDownload();
                status.Text = $"正在下载 v{asset.Version}…";
                var progress = new Progress<int>(percent => status.Text = $"正在下载 v{asset.Version} · {percent}%");
                await DownloadAsync(asset, path, progress, timeout.Token);
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
        using var response = await OpenDownloadAsync($"{ReleasesPage}/latest/download/update.json", timeout.Token);
        response.EnsureSuccessStatusCode();
        var manifest = new byte[4097];
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        int length = await source.ReadAtLeastAsync(manifest, manifest.Length, throwOnEndOfStream: false, timeout.Token);
        if (length == manifest.Length) throw new InvalidDataException("Update manifest is too large.");
        using var document = JsonDocument.Parse(manifest.AsMemory(0, length));
        var asset = ReadAsset(document.RootElement);
        if (asset is null) return ("无法解析发布版本。可打开发布页下载。", null);
        if (asset.Version <= Version.Parse(Current.ToString(3)))
            return ($"当前已是最新版本 {Current.ToString(3)}。", null);
        return ($"发现新版本 {asset.Version}。", asset);
    }

    private static ReleaseAsset? ReadAsset(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("version", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !Version.TryParse(tag.GetString(), out var version) || version.Build < 0 || version.Revision >= 0 ||
            !manifest.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
            !size.TryGetInt64(out long length) || length is <= 0 or > 500_000_000 ||
            !manifest.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
            hash.GetString() is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))
            return null;
        return new ReleaseAsset(version, length, digest);
    }

    private static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        using var response = await OpenDownloadAsync($"{ReleasesPage}/download/v{asset.Version}/Island.exe", cancellationToken);
        await SaveAssetAsync(response, asset, path, progress, cancellationToken);
    }

    private static async Task<HttpResponseMessage> OpenDownloadAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        for (int hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WindowsIsland/" + Current.ToString(3));
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var redirect = (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;
            if (!redirect) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) break;
            uri = new Uri(uri, location);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                break;
        }
        throw new IOException("Unexpected release download redirect.");
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

}
