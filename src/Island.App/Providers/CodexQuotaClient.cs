using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.App.Persistence;
using Island.Core.Events;
using static Island.App.Providers.QuotaProvider;

namespace Island.App.Providers;

internal static class CodexQuotaClient
{
    public static async Task<QuotaAccountStatus> ReadAsync(QuotaAccount account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(account.LocalPath) ? LocalAiDiscovery.FindCodex() : account.LocalPath;
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return Status(account, "未知", "请配置已登录的 Codex CLI 原生 exe");
        }
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = LocalDataStore.DirectoryPath,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        if (!string.IsNullOrWhiteSpace(account.ProfilePath))
        {
            if (!Path.IsPathFullyQualified(account.ProfilePath) || !Directory.Exists(account.ProfilePath))
            {
                return Status(account, "未知", "Codex 配置目录不存在");
            }
            start.Environment["CODEX_HOME"] = account.ProfilePath;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex App Server。");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await SendAsync(process, """{"method":"initialize","id":1,"params":{"clientInfo":{"name":"windows_island","title":"Windows Island","version":"0.1.0"}}}""", token);
            await ReceiveAsync(process, 1, token);
            await SendAsync(process, """{"method":"initialized"}""", token);
            await SendAsync(process, """{"method":"account/rateLimits/read","id":2}""", token);
            var result = await ReceiveAsync(process, 2, token);
            return Parse(account, result, now);
        }
        finally
        {
            await process.StandardInput.DisposeAsync();
            using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            await stderr.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    internal static QuotaAccountStatus Parse(QuotaAccount account, JsonElement result, DateTimeOffset now)
    {
        var details = new List<string>();
        var windows = new List<QuotaMetric>();
        string? mainId = null;
        if (TryProperty(result, "rateLimits", out var main) && main.ValueKind == JsonValueKind.Object)
        {
            mainId = TryProperty(main, "limitId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : "codex";
            AddBucket(main, "Codex", mainId ?? "codex", now, windows);
            if (TryProperty(main, "planType", out var plan) && plan.ValueKind == JsonValueKind.String)
            {
                details.Add($"套餐 {plan.GetString()}");
            }
            if (TryProperty(main, "credits", out var credits) && TryProperty(credits, "balance", out var balance)
                && balance.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                details.Add($"套餐积分 {balance}");
            }
        }
        if (TryProperty(result, "rateLimitsByLimitId", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in limits.EnumerateObject())
            {
                if (bucket.Name != mainId)
                {
                    AddBucket(bucket.Value, bucket.Name, bucket.Name, now, windows);
                }
            }
        }
        if (TryProperty(result, "rateLimitResetCredits", out var resets) && TryProperty(resets, "availableCount", out var count)
            && count.ValueKind == JsonValueKind.Number)
        {
            details.Add($"可用重置次数 {count}");
        }
        return WindowStatus(account, windows, details);
    }

    private static void AddBucket(JsonElement bucket, string label, string bucketId, DateTimeOffset now, List<QuotaMetric> windows)
    {
        foreach (var (key, fallback) in new[] { ("primary", "主要额度"), ("secondary", "次要额度") })
        {
            if (TryProperty(bucket, key, out var entry) && TryProperty(entry, "windowDurationMins", out var duration)
                && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out int minutes) && minutes > 0)
            {
                string period = minutes % 1440 == 0 ? $"{minutes / 1440} 天"
                    : minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes} 分钟";
                AddWindow(bucket, key, $"{label} {period}", "usedPercent", "resetsAt", now, windows, $"{bucketId}/{key}");
            }
            else
            {
                AddWindow(bucket, key, $"{label} {fallback}", "usedPercent", "resetsAt", now, windows, $"{bucketId}/{key}");
            }
        }
    }

    private static async Task SendAsync(Process process, string request, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    private static async Task<JsonElement> ReceiveAsync(Process process, int requestId, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (TryProperty(root, "id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.TryGetInt32(out int number) && number == requestId && !root.TryGetProperty("method", out _))
            {
                return root.GetProperty("result").Clone();
            }
        }
        throw new EndOfStreamException("Codex App Server 已退出。");
    }
}
