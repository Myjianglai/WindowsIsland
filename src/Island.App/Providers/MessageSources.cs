using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Island.App.Persistence;
using Island.App.UI;
using Island.Core.Events;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Island.App.Providers;

internal sealed record MailAccount(string Address, string Secret, string Host, int Port);

internal sealed class MessageSources
{
    private readonly HashSet<uint> _seen = [];
    private readonly Dictionary<string, HashSet<UniqueId>> _mailSeen = new(StringComparer.OrdinalIgnoreCase);
    private bool _toastInitialized;
    private readonly Dictionary<string, (DateTimeOffset Time, bool Flash)> _lastChatAlert = new();

    public void PublishChatAlert(IslandEventBus events, string source, string title, string? preview, bool fromFlash)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastChatAlert.TryGetValue(source, out var previous) &&
            now - previous.Time < TimeSpan.FromSeconds(4) && (fromFlash || previous.Flash))
            return;
        if (events.TryPublish(new IslandEvent(source, IslandEventType.Notification, title, preview,
            timeout: TimeSpan.FromSeconds(ActivityPanel.Options.Seconds))))
            _lastChatAlert[source] = (now, fromFlash);
    }
    public static MailAccount[] Accounts { get; private set; } = Load();

    private static MailAccount[] Load()
    {
        try { return LocalDataStore.ReadProtected<MailAccount[]>("mail-accounts.bin") ?? []; }
        catch (Exception) { return []; }
    }

    public static async Task ConnectAsync(MailAccount account)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await OpenAsync(account, timeout.Token);
        var next = Accounts.Where(x => !x.Address.Equals(account.Address, StringComparison.OrdinalIgnoreCase)).Append(account).ToArray();
        Save(next);
    }

    public static void Remove(string address) => Save(Accounts.Where(x => x.Address != address).ToArray());

    private static void Save(MailAccount[] accounts)
    {
        LocalDataStore.WriteProtected("mail-accounts.bin", accounts);
        Accounts = accounts;
    }

    private static async Task<ImapClient> OpenAsync(MailAccount account, CancellationToken token)
    {
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(account.Host, account.Port, SecureSocketOptions.SslOnConnect, token);
            await client.AuthenticateAsync(account.Address, account.Secret, token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    public async Task RunAsync(IslandEventBus events, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        int tick = 0;
        while (await timer.WaitForNextTickAsync(token))
        {
            var options = ActivityPanel.Options;
            if (options.Messages && (options.Qq || options.WeChat || options.WeCom))
            {
                try
                {
                    var items = await UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast);
                    _seen.IntersectWith(items.Select(item => item.Id));
                    foreach (var item in items)
                    {
                        if (!_seen.Add(item.Id) || !_toastInitialized) continue;
                        var app = item.AppInfo.DisplayInfo.DisplayName.Trim().ToUpperInvariant();
                        string? source = app switch
                        {
                            "微信" or "WECHAT" or "WEIXIN" when options.WeChat => "微信",
                            "企业微信" or "WECOM" or "WXWORK" or "WECHAT WORK" when options.WeCom => "企业微信",
                            "QQ" when options.Qq => "QQ",
                            _ => null
                        };
                        if (source is null) continue;
                        var lines = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                        bool preview = source != "微信" || options.WeChatPreview;
                        var title = preview ? lines?.FirstOrDefault()?.Text : null;
                        PublishChatAlert(events, source,
                            string.IsNullOrWhiteSpace(title) ? "收到一条新消息" : title,
                            preview && lines is not null ? string.Join(" ", lines.Skip(1).Select(x => x.Text)) : null,
                            fromFlash: false);
                    }
                    _toastInitialized = true;
                }
                catch (Exception) when (!token.IsCancellationRequested) { _toastInitialized = false; }
            }
            if (++tick % 10 != 1 || !options.Messages || !options.Mail) continue;
            foreach (var account in Accounts)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using var client = await OpenAsync(account, timeout.Token);
                    await client.Inbox.OpenAsync(FolderAccess.ReadOnly, timeout.Token);
                    var unread = (await client.Inbox.SearchAsync(SearchQuery.NotSeen, timeout.Token)).TakeLast(100).ToHashSet();
                    if (_mailSeen.TryGetValue(account.Address, out var previous))
                        foreach (var uid in unread.Except(previous).Take(10))
                        {
                            var summary = (await client.Inbox.FetchAsync(new[] { uid }, MessageSummaryItems.Envelope, timeout.Token))
                                .FirstOrDefault()?.Envelope;
                            events.TryPublish(new IslandEvent("邮箱", IslandEventType.Mail,
                                string.IsNullOrWhiteSpace(summary?.Subject) ? "收到新邮件" : summary.Subject,
                                summary?.From.ToString() ?? account.Address, timeout: TimeSpan.FromSeconds(options.Seconds)));
                        }
                    _mailSeen[account.Address] = unread;
                }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
        }
    }
}
