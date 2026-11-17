using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.App.Persistence;
using Island.Core.Events;
using Island.Core.Providers;

namespace Island.App.Providers;

internal sealed class QuotaProvider(QuotaAccountStore store) : IIslandProvider
{
    public const string SourceId = "ai-quota";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private int _refreshVersion;
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<QuotaMetric>> _metrics = new();
    public string Id => SourceId;
    public IReadOnlyList<QuotaMetric> MetricsFor(Guid id) => _metrics.TryGetValue(id, out var value) ? value : [];

    public void Refresh()
    {
        Interlocked.Increment(ref _refreshVersion);
        if (_refresh.CurrentCount == 0)
        {
            try { _refresh.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    public async Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<Guid, CachedResult>();
        var balanceHistory = new QuotaBalanceHistory();
        int lastRefresh = Volatile.Read(ref _refreshVersion);
        string? lastFingerprint = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            int refreshVersion = Volatile.Read(ref _refreshVersion);
            if (refreshVersion != lastRefresh)
            {
                cache.Clear();
                lastRefresh = refreshVersion;
            }
            var accounts = store.Load();
            foreach (var deleted in cache.Keys.Except(accounts.Select(account => account.Id)).ToArray())
            {
                cache.Remove(deleted);
            }
            foreach (var deleted in _metrics.Keys.Except(accounts.Select(account => account.Id)))
                _metrics.TryRemove(deleted, out _);
            var now = DateTimeOffset.UtcNow;
            var pending = accounts.Select(async account =>
            {
                var network = account.Service is QuotaService.Codex or QuotaService.DeepSeek or QuotaService.Cursor;
                if (network && cache.TryGetValue(account.Id, out var item) && item.Account == account && item.NextRefresh > now)
                {
                    return item;
                }
                return new CachedResult(account, await LoadStatusAsync(account, now, cancellationToken),
                    now.AddMinutes(5));
            }).ToArray();
            var results = await Task.WhenAll(pending);
            foreach (var result in results)
            {
                cache[result.Account.Id] = result;
                if (result.Status.Metrics is { Count: > 0 } metrics)
                {
                    _metrics[result.Account.Id] = metrics;
                }
            }
            var snapshot = balanceHistory.AttachReference(new QuotaSnapshot(results.Select(result => result.Status).ToArray()));
            var fingerprint = JsonSerializer.Serialize(snapshot);
            if (fingerprint != lastFingerprint)
            {
                await publisher.PublishAsync(new IslandEvent(Id, IslandEventType.Quota, "AI 额度", quota: snapshot),
                    cancellationToken);
                lastFingerprint = fingerprint;
            }
            await _refresh.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private sealed record CachedResult(QuotaAccount Account, QuotaAccountStatus Status, DateTimeOffset NextRefresh);

    private static async Task<QuotaAccountStatus> LoadStatusAsync(QuotaAccount account, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return account.Service switch
            {
                QuotaService.Claude => await ClaudeAsync(account, now, cancellationToken),
                QuotaService.DeepSeek => await DeepSeekAsync(account, cancellationToken),
                QuotaService.Codex => await CodexQuotaClient.ReadAsync(account, now, cancellationToken),
                QuotaService.Cursor => await LocalAiDiscovery.CursorStatusAsync(account, cancellationToken),
                _ => account.ManualCredits is { } credits
                    ? Status(account, $"{credits:G} 积分", $"剩余积分 {credits:G}（手动录入）")
                    : Status(account, "未知", "剩余积分未知，可从客户端查看后手动录入")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 对每个账号独立处理读取失败。异常可包含本机路径或远端响应，因此不记录。
            return Status(account, "未知", "暂时无法读取额度");
        }
    }

    private static async Task<QuotaAccountStatus> ClaudeAsync(QuotaAccount account, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(account.LocalPath) ? QuotaAccountStore.DefaultClaudeStatus : account.LocalPath;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 128 * 1024)
        {
            return Status(account, "未知", "等待 Claude Code 官方状态栏数据");
        }
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryProperty(doc.RootElement, "rate_limits", out var limits))
        {
            return Status(account, "未知", "额度与重置时间未知");
        }
        var windows = new List<QuotaMetric>();
        AddWindow(limits, "five_hour", "5 小时额度", "used_percentage", "resets_at", now, windows);
        AddWindow(limits, "seven_day", "7 天额度", "used_percentage", "resets_at", now, windows);
        AddWindow(limits, "spend_limit", "支出上限", "used_percentage", "resets_at", now, windows);
        return WindowStatus(account, windows, []);
    }

    private static async Task<QuotaAccountStatus> DeepSeekAsync(QuotaAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.ApiKey))
        {
            return Status(account, "未知", "请在设置中填写 DeepSeek API Key");
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.ApiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Status(account, "未知", "DeepSeek 未接受此 API Key");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseDeepSeek(account, doc.RootElement);
    }

    internal static QuotaAccountStatus ParseDeepSeek(QuotaAccount account, JsonElement root)
    {
        var details = new List<string>();
        var balances = new List<QuotaBalance>();
        string? brief = null;
        foreach (var balance in root.GetProperty("balance_infos").EnumerateArray())
        {
            var currency = balance.GetProperty("currency").GetString();
            var total = balance.GetProperty("total_balance").GetString();
            var toppedUp = balance.GetProperty("topped_up_balance").GetString();
            var granted = balance.GetProperty("granted_balance").GetString();
            brief ??= $"{currency} {total}";
            if (!string.IsNullOrWhiteSpace(currency) &&
                decimal.TryParse(total, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                balances.Add(new QuotaBalance(currency, amount));
                details.Add($"{currency} · 充值余额 {toppedUp} · 赠送余额 {granted}");
            }
            else
                details.Add($"{currency} {total} · 充值余额 {toppedUp} · 赠送余额 {granted}");
        }
        if (root.TryGetProperty("is_available", out var available) && available.ValueKind == JsonValueKind.False)
        {
            details.Add("余额不足以调用 API");
        }
        return brief is null ? Status(account, "未知", "余额未知") :
            Status(account, brief, details.ToArray()) with { Balances = balances.ToArray() };
    }

    internal static QuotaAccountStatus WindowStatus(QuotaAccount account, List<QuotaMetric> windows, List<string> details)
    {
        string? brief = null;
        foreach (var window in windows)
        {
            var percent = window.Used?.ToString("0.#", CultureInfo.InvariantCulture);
            if (percent is not null)
            {
                brief ??= $"已用 {percent}%";
            }
        }
        if (details.Count == 0 && windows.Count == 0)
        {
            details.Add("额度与重置时间未知");
        }
        return Status(account, brief ?? "未知", details.ToArray()) with { Metrics = windows.ToArray() };
    }

    internal static void AddWindow(JsonElement limits, string property, string label, string usageKey, string resetKey,
        DateTimeOffset now, List<QuotaMetric> windows, string? key = null)
    {
        if (!TryProperty(limits, property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        DateTimeOffset? reset = null;
        if (TryProperty(window, resetKey, out var resetValue) && resetValue.ValueKind == JsonValueKind.Number
            && resetValue.TryGetInt64(out long seconds) && seconds is >= 0 and <= 253402300799)
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset <= now)
            {
                windows.Add(new QuotaMetric(key ?? property, label, null, null));
                return;
            }
        }
        double? used = null;
        if (TryProperty(window, usageKey, out var usedValue) && usedValue.ValueKind == JsonValueKind.Number
            && usedValue.TryGetDouble(out double percent) && double.IsFinite(percent) && percent >= 0)
        {
            used = percent;
        }
        windows.Add(new QuotaMetric(key ?? property, label, used, reset));
    }

    internal static bool TryProperty(JsonElement element, string property, out JsonElement result)
    {
        result = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out result);
    }

    internal static QuotaAccountStatus Status(QuotaAccount account, string brief, params string[] details) =>
        new(account.Service.ToString(), account.Name, account.Color, account.Pinned, brief, details, null, account.RingKeys, account.Id);
}
