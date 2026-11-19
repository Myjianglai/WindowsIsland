using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Ellipse = System.Windows.Shapes.Ellipse;
using Island.App.Persistence;
using Island.App.Providers;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed record ActivityOptions(bool Enabled = true, bool Codex = true, bool Claude = true,
    bool Compact = true, bool Completed = true, bool Attention = true, bool Sound = false,
    bool Approval = false, bool Todo = true, bool Messages = true, int Seconds = 5,
    bool CarryTodo = true, bool Pomodoro = false, int PomodoroMinutes = 25,
    bool Qq = true, bool WeChat = true, bool WeChatPreview = false, bool Mail = true, bool WeCom = true);

internal sealed record AgentActivity(string Id, string Source, string Title, string State, string Detail,
    string Request, DateTimeOffset Time, string? Model = null, string? Tool = null, string? Prompt = null,
    bool Archived = false, string? Workspace = null, string? WindowKey = null, string? HostPath = null,
    int HostPid = 0, string? Answer = null, string? Effort = null, string? Permission = null);

internal sealed class ActivityPanel
{
    private static readonly string Folder = Path.Combine(LocalDataStore.DirectoryPath, "agent");
    private readonly Dictionary<(string, string), AgentActivity> _sessions = new();
    private readonly Dictionary<string, ImageSource> _hostIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _codexTitles = new();
    private DateTime _codexIndexWriteTime;
    private static (string, string) SessionKey(AgentActivity item) => (item.Source,
        string.IsNullOrWhiteSpace(item.WindowKey) ? item.Id : item.WindowKey);
    private sealed record MessageEntry(IslandEvent Event, DateTimeOffset ReceivedAt, long ReceivedTick, bool Unread);
    private readonly List<MessageEntry> _history = new();
    private static readonly string[] MessageChannels = ["微信", "QQ", "邮箱", "企业微信"];
    private string _messageChannel = "微信";
    public Action? OpenMessageSettings { get; set; }
    public IslandEvent? Latest => Options.Messages ? _history.FirstOrDefault(x => x.Unread)?.Event : null;
    public int UnreadCount => Options.Messages ? _history.Count(x => x.Unread) : 0;
    public IslandEvent? CompactMessage => Options.Messages &&
        _history.FirstOrDefault(x => x.Unread) is { } latest &&
        System.Diagnostics.Stopwatch.GetElapsedTime(latest.ReceivedTick) < TimeSpan.FromSeconds(5)
            ? latest.Event : null;
    public event Action? UnreadChanged;
    public void OpenLatestChannel()
    {
        if (Latest is { } latest) _messageChannel = latest.Source;
        RenderMessages();
    }
    private DateTimeOffset _lastHeartbeat;
    private bool _showArchived;
    public static ActivityOptions Options { get; private set; } = Load();
    public static event Action? SettingsChanged;
    public StackPanel Sessions { get; } = new();
    public StackPanel QuotaSummary { get; } = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
    public void SetQuotaSummary(QuotaSnapshot snapshot, IslandPreferences preferences)
    {
        QuotaSummary.Children.Clear();
        var accounts = snapshot.Accounts.Where(x => x.Service is "Codex" or "Claude" or "Claude Code")
            .OrderByDescending(x => x.Pinned);
        foreach (var account in accounts.Take(3))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            row.Children.Add(QuotaDisplay.Badge(account, preferences));
            if (account.Metrics is { Count: > 0 } metrics)
            {
                foreach (var metric in metrics.Where(x => x.Used.HasValue).Take(2))
                {
                    var percent = QuotaDisplay.Value(metric.Used, preferences.ShowRemaining);
                    var label = ExpandedTabs.Label($"{percent:0.#}%", 12,
                        row.Children.Count == 1 ? IslandTheme.Account(account.Color) : IslandTheme.Muted);
                    label.ToolTip = $"{metric.Label} · {QuotaDisplay.Describe(percent, preferences.ShowRemaining)}";
                    label.Margin = new Thickness(3, 0, 2, 0);
                    label.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(label);
                }
            }
            QuotaSummary.Children.Add(row);
        }
    }
    public StackPanel Messages { get; } = new();
    public AgentActivity[] Running => _sessions.Values.Where(x => Visible(x) && !x.Archived && x.State is "运行中" or "待确认")
        .OrderByDescending(x => x.Time).ToArray();

    public ActivityPanel()
    {
        try
        {
            foreach (var entry in LocalDataStore.ReadProtected<AgentActivity[]>("agent-sessions.bin") ?? [])
                _sessions[SessionKey(entry)] = entry with { Title = SessionTitle(entry) };
        }
        catch (Exception) { }
        RefreshCodexTitles();
        Render();
        RenderMessages();
        SettingsChanged += () => { Render(); RenderMessages(); };
    }

    private static ActivityOptions Load()
    {
        try { return LocalDataStore.ReadProtected<ActivityOptions>("activity.bin") ?? new(); }
        catch (Exception) { return new(); }
    }

    private static bool Visible(AgentActivity x) => Options.Enabled && (x.Source == "Codex" ? Options.Codex : Options.Claude);

    private void SaveSessions()
    {
        var history = _sessions.Values.Select(entry => entry with { Detail = "", Request = "",
            State = entry.State is "运行中" or "待确认" ? "上次活动" : entry.State }).ToArray();
        LocalDataStore.WriteProtected("agent-sessions.bin", history);
    }

    private bool RefreshCodexTitles()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string path = Path.Combine(home, "session_index.jsonl");
        try
        {
            DateTime changed = File.GetLastWriteTimeUtc(path);
            if (changed == _codexIndexWriteTime) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            _codexTitles.Clear();
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var record = JsonDocument.Parse(line);
                    var root = record.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var thread) ||
                        !root.TryGetProperty("thread_name", out var name) || name.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(name.GetString())) continue;
                    _codexTitles[thread] = name.GetString()!.Trim();
                }
                catch (JsonException) { }
            }
            _codexIndexWriteTime = changed;
            bool updated = false;
            foreach (var key in _sessions.Keys.ToArray())
            {
                var item = _sessions[key];
                var title = SessionTitle(item);
                if (title == item.Title) continue;
                _sessions[key] = item with { Title = title };
                updated = true;
            }
            return updated;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string SessionTitle(AgentActivity item)
    {
        if (item.Source == "Codex" && Guid.TryParse(item.Id, out var thread) && _codexTitles.TryGetValue(thread, out var title))
            return title;
        if (item.Title.Contains("# Files mentioned by the user:", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(item.Workspace) ? item.Source : Path.GetFileName(item.Workspace.TrimEnd('\\', '/'));
        return item.Title;
    }

    public void Poll(Func<IslandEvent, bool> publish)
    {
        try
        {
            if (RefreshCodexTitles()) Render();
            Directory.CreateDirectory(Folder);
            if (Options is { Enabled: true, Approval: true } &&
                DateTimeOffset.UtcNow - _lastHeartbeat >= TimeSpan.FromSeconds(2))
            {
                LocalDataStore.WriteAtomic(Path.Combine(Folder, "heartbeat"), []);
                _lastHeartbeat = DateTimeOffset.UtcNow;
            }
            foreach (string path in Directory.EnumerateFiles(Folder, "*.event").OrderBy(File.GetLastWriteTimeUtc).Take(100))
            {
                AgentActivity? activity;
                try { activity = JsonSerializer.Deserialize<AgentActivity>(File.ReadAllText(path)); }
                catch (JsonException) { activity = null; }
                File.Delete(path);
                if (activity is null || string.IsNullOrEmpty(activity.Id) || activity.Source is not ("Codex" or "Claude") ||
                    activity.Title is null || !Visible(activity) || DateTimeOffset.UtcNow - activity.Time > TimeSpan.FromMinutes(3)) continue;
                var key = SessionKey(activity);
                var existingKey = key;
                if (!_sessions.ContainsKey(key))
                {
                    var existing = _sessions.FirstOrDefault(pair => pair.Value.Source == activity.Source && pair.Value.Id == activity.Id);
                    if (existing.Value is not null) existingKey = existing.Key;
                }
                if (_sessions.TryGetValue(existingKey, out var previous))
                {
                    if (previous.Time > activity.Time) continue;
                    if (previous.Id == activity.Id && previous.Title != previous.Source || activity.Title.Length == 0)
                        activity = activity with { Title = previous.Title };
                    activity = activity with { Model = Prefer(activity.Model, previous.Model), Prompt = Prefer(activity.Prompt, previous.Prompt),
                        Workspace = Prefer(activity.Workspace, previous.Workspace),
                        HostPath = Prefer(activity.HostPath, previous.HostPath),
                        Effort = Prefer(activity.Effort, previous.Effort), Permission = Prefer(activity.Permission, previous.Permission),
                        HostPid = activity.HostPid != 0 ? activity.HostPid : previous.HostPid,
                        Answer = !string.IsNullOrWhiteSpace(activity.Answer) || activity.State == "运行中" ? activity.Answer : previous.Answer,
                        Archived = previous.Archived && activity.State is not ("运行中" or "待确认") };
                    if (existingKey != key)
                    {
                        if (string.IsNullOrWhiteSpace(activity.WindowKey))
                        {
                            key = existingKey;
                            activity = activity with { WindowKey = previous.WindowKey };
                        }
                        else _sessions.Remove(existingKey);
                    }
                }
                if (activity.Title.Length == 0) activity = activity with { Title = activity.Source };
                activity = activity with { Title = SessionTitle(activity) };
                _sessions[key] = activity;
                if (_sessions.Count > 50) _sessions.Remove(_sessions.MinBy(x => x.Value.Time).Key);
                try { SaveSessions(); }
                catch (System.Security.Cryptography.CryptographicException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (Options.Sound && previous?.State != activity.State) System.Media.SystemSounds.Asterisk.Play();
                if (activity.State == "已完成" && Options.Completed || activity.State is "出错" or "待确认" && Options.Attention)
                    publish(new IslandEvent(activity.Source, IslandEventType.Notification, activity.State,
                        activity.Title, timeout: TimeSpan.FromSeconds(Options.Seconds)));
                Render();
            }
            foreach (var key in _sessions.Where(x => x.Value.State == "待确认" &&
                DateTimeOffset.UtcNow - x.Value.Time > TimeSpan.FromSeconds(60)).Select(x => x.Key).ToArray())
            {
                _sessions[key] = _sessions[key] with { State = "请查看终端", Request = "" };
                Render();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Record(IslandEvent message)
    {
        if (!Options.Messages || !ChannelEnabled(message.Source)) return;
        _history.Insert(0, new MessageEntry(message, DateTimeOffset.Now, System.Diagnostics.Stopwatch.GetTimestamp(), true));
        if (_history.Count > 100) _history.RemoveAt(100);
        RenderMessages();
        UnreadChanged?.Invoke();
    }

    private static bool ChannelEnabled(string channel) => channel switch
    {
        "微信" => Options.WeChat, "QQ" => Options.Qq, "邮箱" => Options.Mail, "企业微信" => Options.WeCom, _ => false
    };

    private void RenderMessages()
    {
        Messages.Children.Clear();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var power = ExpandedTabs.MakeFlatButton("⏻");
        bool enabled = ChannelEnabled(_messageChannel);
        string powerLabel = $"{(enabled ? "关闭" : "开启")}{_messageChannel}通知";
        power.ToolTip = powerLabel;
        power.SetResourceReference(Control.ForegroundProperty, enabled ? "AccountGreen" : IslandTheme.Muted);
        System.Windows.Automation.AutomationProperties.SetName(power, powerLabel);
        power.Click += (_, _) =>
        {
            var next = _messageChannel switch
            {
                "微信" => Options with { WeChat = !Options.WeChat },
                "QQ" => Options with { Qq = !Options.Qq },
                "邮箱" => Options with { Mail = !Options.Mail },
                _ => Options with { WeCom = !Options.WeCom }
            };
            try { LocalDataStore.WriteProtected("activity.bin", next); }
            catch (Exception) { power.ToolTip = "无法保存通知设置"; return; }
            Options = next;
            SettingsChanged?.Invoke();
        };
        DockPanel.SetDock(power, Dock.Right);
        header.Children.Add(power);
        var read = ExpandedTabs.MakeFlatButton("✓✓");
        read.ToolTip = "全部已读并清空";
        System.Windows.Automation.AutomationProperties.SetName(read, "全部已读并清空");
        read.IsEnabled = _history.Count > 0;
        read.Click += (_, _) => { _history.Clear(); RenderMessages(); UnreadChanged?.Invoke(); };
        DockPanel.SetDock(read, Dock.Right);
        header.Children.Add(read);
        if (_messageChannel == "邮箱")
        {
            var settings = ExpandedTabs.MakeFlatButton("⚙");
            settings.ToolTip = "管理邮箱连接";
            settings.Click += (_, _) => OpenMessageSettings?.Invoke();
            DockPanel.SetDock(settings, Dock.Right);
            header.Children.Add(settings);
        }
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (string channel in MessageChannels)
        {
            var tab = ExpandedTabs.MakeFlatButton("");
            System.Windows.Automation.AutomationProperties.SetName(tab, channel);
            var contents = new StackPanel { Orientation = Orientation.Horizontal };
            contents.Children.Add(SourceIcon(channel));
            contents.Children.Add(ExpandedTabs.Label(channel, 12, channel == _messageChannel ? IslandTheme.Ink : IslandTheme.Muted));
            if (_history.Any(entry => entry.Unread && entry.Event.Source == channel))
            {
                var dot = ExpandedTabs.Label(" •", 12, "AccountGreen");
                contents.Children.Add(dot);
            }
            tab.Content = contents;
            if (channel == _messageChannel) tab.SetResourceReference(Control.BackgroundProperty, IslandTheme.Tile);
            tab.Click += (_, _) => { _messageChannel = channel; RenderMessages(); };
            tabs.Children.Add(tab);
        }
        header.Children.Add(tabs);
        Messages.Children.Add(header);
        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8) };
        rule.SetResourceReference(Border.BackgroundProperty, IslandTheme.Edge);
        Messages.Children.Add(rule);
        var rows = new StackPanel();
        foreach (var entry in _history.Where(x => x.Event.Source == _messageChannel))
        {
            var item = entry.Event;
            var titleRow = new DockPanel();
            var time = ExpandedTabs.Label(entry.ReceivedAt.ToString("HH:mm"), 11, IslandTheme.Muted);
            DockPanel.SetDock(time, Dock.Right);
            titleRow.Children.Add(time);
            var icon = SourceIcon(item.Source);
            DockPanel.SetDock(icon, Dock.Left);
            titleRow.Children.Add(icon);
            var title = ExpandedTabs.Label(item.Source, 13);
            title.FontWeight = FontWeights.SemiBold;
            titleRow.Children.Add(title);
            var description = ExpandedTabs.Label(string.IsNullOrWhiteSpace(item.Subtitle) ? item.Title : $"{item.Title}\n{item.Subtitle}",
                12, IslandTheme.Muted);
            description.TextWrapping = TextWrapping.Wrap;
            description.MaxHeight = 56;
            description.Margin = new Thickness(4, 5, 0, 0);
            var card = ExpandedTabs.MakeButton("");
            card.Content = new StackPanel { Children = { titleRow, description } };
            card.Padding = new Thickness(10, 7, 10, 8);
            card.Margin = new Thickness(0, 0, 0, 6);
            card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.Focusable = true;
            System.Windows.Automation.AutomationProperties.SetName(card, $"{item.Source} · {item.Title}");
            card.Click += async (_, _) => await OpenMessageAsync(entry);
            rows.Children.Add(card);
        }
        if (rows.Children.Count == 0)
        {
            bool needsMail = _messageChannel == "邮箱" && MessageSources.Accounts.Length == 0;
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 32, 0, 0) };
            var icon = SourceIcon(_messageChannel);
            icon.Width = icon.Height = 30;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var caption = ExpandedTabs.Label(needsMail ? "还没有连接邮箱" : "暂无消息", 13, IslandTheme.Muted);
            caption.Margin = new Thickness(0, 7, 0, 5);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(caption);
            if (needsMail)
            {
                var configure = ExpandedTabs.MakeButton("在设置中添加邮箱");
                configure.Click += (_, _) => OpenMessageSettings?.Invoke();
                empty.Children.Add(configure);
            }
            rows.Children.Add(empty);
        }
        Messages.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private async System.Threading.Tasks.Task OpenMessageAsync(MessageEntry entry)
    {
        if (!_history.Remove(entry)) return;
        RenderMessages();
        UnreadChanged?.Invoke();
        var item = entry.Event;
        if (item.Source is "微信" or "企业微信" && await ChatClientWindow.OpenAsync(item.Source)) return;
        var title = ExpandedTabs.Label(item.Title, 16);
        title.FontWeight = FontWeights.SemiBold;
        title.TextWrapping = TextWrapping.Wrap;
        var stamp = ExpandedTabs.Label($"{item.Source} · {entry.ReceivedAt:yyyy-MM-dd HH:mm}", 11, IslandTheme.Muted);
        stamp.Margin = new Thickness(0, 0, 0, 8);
        var body = ExpandedTabs.Label(item.Subtitle ?? "", 13);
        body.TextWrapping = TextWrapping.Wrap;
        var text = new StackPanel { Children = { stamp, title, body } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0) };
        var close = ExpandedTabs.MakeButton("关闭");
        close.IsCancel = true;
        var open = ExpandedTabs.MakeButton("打开来源应用");
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window { Title = $"{item.Source}消息", Width = 430, MaxHeight = 520,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel };
        if (Application.Current.MainWindow is { } island)
        {
            foreach (var key in new[] { IslandTheme.Surface, IslandTheme.Ink, IslandTheme.Muted, IslandTheme.Tile, IslandTheme.Edge, "IslandButton" })
                if (island.TryFindResource(key) is { } resource) dialog.Resources[key] = resource;
        }
        dialog.SetResourceReference(Window.BackgroundProperty, IslandTheme.Surface);
        close.Click += (_, _) => dialog.Close();
        open.Click += async (_, _) =>
        {
            open.IsEnabled = false;
            bool opened = item.Source is "微信" or "企业微信"
                ? await ChatClientWindow.OpenAsync(item.Source) : OpenMessageSource(item.Source);
            if (opened && dialog.IsVisible) dialog.Close();
            else { open.Content = "无法打开来源应用"; open.IsEnabled = false; }
        };
        actions.Children.Add(open);
        actions.Children.Add(close);
        dialog.ShowDialog();
    }

    private static bool OpenMessageSource(string source)
    {
        string[] names = source switch
        {
            "QQ" => ["QQ"], "邮箱" => ["olk", "OUTLOOK"], _ => []
        };
        foreach (string name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using var app = process;
                try
                {
                    IntPtr window = app.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
                    if (SetForegroundWindow(window)) return true;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }

    private void Render()
    {
        Sessions.Children.Clear();
        var rows = new StackPanel();
        var visible = _sessions.Values.Where(Visible).ToArray();
        if (visible.Any(x => x.Archived))
        {
            var archive = ExpandedTabs.MakeFlatButton(_showArchived ? "返回会话" : "已归档");
            archive.HorizontalAlignment = HorizontalAlignment.Right;
            archive.Click += (_, _) => { _showArchived = !_showArchived; Render(); };
            Sessions.Children.Add(archive);
        }
        else _showArchived = false;
        foreach (var item in visible.Where(x => x.Archived == _showArchived).OrderByDescending(x => x.Time))
        {
            var row = new DockPanel { MinHeight = 34 };
            var details = new StackPanel { Orientation = Orientation.Horizontal };
            var elapsed = DateTimeOffset.UtcNow - item.Time;
            var age = elapsed.TotalMinutes < 1 ? "刚刚" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分" : $"{(int)elapsed.TotalHours} 小时";
            var model = Prefer(item.Model, "模型未知")!;
            if (!string.IsNullOrWhiteSpace(item.Effort))
                model += $" · {char.ToUpperInvariant(item.Effort[0])}{item.Effort[1..]}";
            var tooltip = new List<string> { item.Title, $"{item.State} · {model}" };
            string? permission = item.Permission switch
            {
                "never" => "不询问", "bypassPermissions" => "跳过确认", "on-request" or "default" => "按需确认",
                "acceptEdits" => "自动接受编辑", "plan" => "计划模式", "dontAsk" => "不询问",
                "untrusted" => "不受信任时确认", "on-failure" => "失败时确认", _ => Prefer(item.Permission, null)
            };
            if (permission is not null) tooltip.Add($"权限模式 · {permission}");
            if (item.State is "运行中" or "待确认")
            {
                details.Children.Add(SessionBadge(model, model));
                if (permission is not null)
                    details.Children.Add(SessionBadge(permission, $"权限模式 · {permission}", StatusColor("待确认")));
            }
            if (!string.IsNullOrWhiteSpace(item.Prompt)) tooltip.Add($"你 · {item.Prompt}");
            if (!string.IsNullOrWhiteSpace(item.Tool)) tooltip.Add($"工具 · {item.Tool}");
            if (!string.IsNullOrWhiteSpace(item.Answer)) tooltip.Add($"Agent · {item.Answer}");
            var timestamp = ExpandedTabs.Label(age, 11, IslandTheme.Muted);
            timestamp.Margin = new Thickness(7, 0, 6, 0);
            timestamp.ToolTip = item.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            timestamp.VerticalAlignment = VerticalAlignment.Center;
            details.Children.Add(timestamp);
            var open = ExpandedTabs.MakeFlatButton("↗");
            open.ToolTip = "打开 Agent 会话";
            open.Click += (_, _) => { if (!OpenSession(item)) open.ToolTip = "无法打开 Agent 会话"; };
            details.Children.Add(open);
            if (HostIcon(item) is { } hostIcon) details.Children.Add(hostIcon);
            details.Children.Add(SourceIcon(item.Source));
            if (item.State is not ("运行中" or "待确认"))
            {
                var archive = ExpandedTabs.MakeFlatButton(item.Archived ? "↶" : "▣");
                archive.ToolTip = item.Archived ? "恢复会话" : "归档会话";
                archive.Click += (_, _) =>
                {
                    var key = SessionKey(item);
                    _sessions[key] = item with { Archived = !item.Archived };
                    try { SaveSessions(); }
                    catch (Exception) { _sessions[key] = item; archive.ToolTip = "无法保存归档状态"; return; }
                    Render();
                };
                details.Children.Add(archive);
            }
            DockPanel.SetDock(details, Dock.Right);
            row.Children.Add(details);
            var dot = new Ellipse { Width = 7, Height = 7, Fill = StatusColor(item.State),
                Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            var label = ExpandedTabs.Label(item.Title, 14, item.State is "运行中" or "待确认" ? IslandTheme.Ink : IslandTheme.Muted);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.FontWeight = FontWeights.SemiBold;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var titleButton = ExpandedTabs.MakeFlatButton("");
            titleButton.Content = label;
            titleButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            titleButton.ToolTip = new TextBlock { Text = string.Join(Environment.NewLine, tooltip),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
            titleButton.Click += (_, _) => { if (!OpenSession(item)) titleButton.ToolTip = "无法打开 Agent 会话"; };
            row.Children.Add(titleButton);
            var sessionContent = new StackPanel { Children = { row } };
            if (item.State is "运行中" or "待确认")
            {
                if (!string.IsNullOrWhiteSpace(item.Prompt))
                {
                    var prompt = ExpandedTabs.Label($"你：{item.Prompt}", 12, IslandTheme.Muted);
                    prompt.TextTrimming = TextTrimming.CharacterEllipsis;
                    prompt.Margin = new Thickness(16, 0, 7, 3);
                    sessionContent.Children.Add(prompt);
                }
                if (!string.IsNullOrWhiteSpace(item.Tool))
                {
                    var tool = ExpandedTabs.Label(item.Tool, 12, IslandTheme.Accent);
                    tool.FontFamily = new FontFamily("Consolas");
                    tool.TextTrimming = TextTrimming.CharacterEllipsis;
                    tool.Margin = new Thickness(16, 0, 7, 5);
                    sessionContent.Children.Add(tool);
                }
            }
            var highlight = new Border { Child = sessionContent, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 4, 0),
                Margin = new Thickness(0, 2, 0, 2), BorderThickness = new Thickness(1),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent };
            if (item.State is not ("运行中" or "待确认"))
            {
                var remove = new MenuItem { Header = "从灵动岛移除" };
                remove.Click += (_, _) =>
                {
                    var key = SessionKey(item);
                    if (!_sessions.TryGetValue(key, out var current) || current.State is "运行中" or "待确认") return;
                    _sessions.Remove(key);
                    try { SaveSessions(); }
                    catch (Exception)
                    {
                        _sessions[key] = current;
                        highlight.ToolTip = "无法保存会话列表";
                        return;
                    }
                    Render();
                };
                var menu = new ContextMenu { Items = { remove } };
                menu.SetResourceReference(FrameworkElement.StyleProperty, "IslandExcelMenu");
                highlight.ContextMenu = menu;
            }
            highlight.MouseEnter += (_, _) =>
            {
                highlight.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
                highlight.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
            };
            highlight.MouseLeave += (_, _) =>
            {
                highlight.Background = Brushes.Transparent;
                highlight.BorderBrush = Brushes.Transparent;
            };
            rows.Children.Add(highlight);
            if (!Options.Approval || !Guid.TryParseExact(item.Request, "N", out _)) continue;
            var input = new TextBox { Text = item.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            rows.Children.Add(input);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (caption, decision) in new[] { ("允许本次", "allow"), ("拒绝", "deny"), ("交回终端", "terminal") })
            {
                var button = ExpandedTabs.MakeFlatButton(caption);
                button.Click += (_, _) =>
                {
                    if (DateTimeOffset.UtcNow - item.Time >= TimeSpan.FromSeconds(58)) return;
                    try { LocalDataStore.WriteAtomic(Path.Combine(Folder, item.Request + ".reply"), System.Text.Encoding.ASCII.GetBytes(decision)); }
                    catch (IOException) { input.ToolTip = "无法提交决定，请在终端处理"; return; }
                    catch (UnauthorizedAccessException) { input.ToolTip = "无法提交决定，请在终端处理"; return; }
                    _sessions[SessionKey(item)] = item with { Request = "", State = decision == "terminal" ? "请查看终端" : "运行中" };
                    Render();
                };
                actions.Children.Add(button);
            }
            rows.Children.Add(actions);
        }
        if (rows.Children.Count == 0)
        {
            var empty = ExpandedTabs.Label("正在等待 Codex 或 Claude Code 会话", 13, IslandTheme.Muted);
            rows.Children.Add(empty);
            var help = ExpandedTabs.Label("在设置中安装钩子，然后在工具中发起一轮新任务。", 12, IslandTheme.Muted);
            rows.Children.Add(help);
        }
        Sessions.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 205, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private static string? Prefer(string? current, string? previous) =>
        string.IsNullOrWhiteSpace(current) ? previous : current;

    private static Border SessionBadge(string caption, string hint, Brush? accent = null)
    {
        var label = ExpandedTabs.Label(caption, 11, IslandTheme.Muted);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (accent is not null) label.Foreground = accent;
        var badge = new Border { Child = label, ToolTip = hint, MaxWidth = 150,
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        return badge;
    }

    private FrameworkElement? HostIcon(AgentActivity item)
    {
        if (item.HostPath is not { } path || !File.Exists(path)) return null;
        try
        {
            if (!_hostIcons.TryGetValue(path, out var source))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;
                source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(16, 16));
                source.Freeze();
                _hostIcons[path] = source;
            }
            return new Image { Source = source, Width = 16, Height = 16, Margin = new Thickness(3, 0, 1, 0),
                ToolTip = Path.GetFileNameWithoutExtension(path), VerticalAlignment = VerticalAlignment.Center };
        }
        catch (Exception) { return null; }
    }

    private static bool OpenSession(AgentActivity item)
    {
        try
        {
            if (item.Source == "Claude" && item.HostPid > 0 && item.HostPath is not null)
            {
                try
                {
                    using var host = Process.GetProcessById(item.HostPid);
                    if (string.Equals(host.MainModule?.FileName, item.HostPath, StringComparison.OrdinalIgnoreCase) &&
                        host.MainWindowHandle is { } window && window != IntPtr.Zero)
                    {
                        if (IsIconic(window)) ShowWindowAsync(window, 9);
                        if (SetForegroundWindow(window)) return true;
                    }
                }
                catch (Exception) { }
            }
            if (!Guid.TryParse(item.Id, out var session)) return false;
            if (item.Source == "Codex")
            {
                string url = $"codex://threads/{session:D}";
                using var opened = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            var claude = LocalAiDiscovery.FindOnPath("claude.exe") ?? LocalAiDiscovery.FindOnPath("claude.cmd");
            if (claude is null) return false;
            using var resumed = Process.Start(new ProcessStartInfo(claude, $"--resume {session:D}") { UseShellExecute = true,
                WorkingDirectory = Directory.Exists(item.Workspace) ? item.Workspace! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
            return resumed is not null;
        }
        catch (Exception) { return false; }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private static Brush StatusColor(string state) => SettingsRows.Brush(state switch
    {
        "运行中" => "#35C874", "待确认" or "请查看终端" => "#F2A742", "出错" => "#F46B76", _ => "#9297A2"
    });

    public static FrameworkElement SourceIcon(string source)
    {
        string? file = source switch { "Codex" => "openai.ico", "Claude" => "claude.ico", "微信" => "wechat.ico",
            "QQ" => "qq.ico", "企业微信" => "wecom.ico", _ => null };
        if (file is not null)
        {
            try
            {
                var icon = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}"));
                return new Image { Source = icon, Width = 16, Height = 16, ToolTip = source,
                    Margin = new Thickness(3, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            }
            catch (Exception) { }
        }
        var fallback = ExpandedTabs.Label(source switch { "Codex" => "CX", "Claude" => "CC", "邮箱" => "✉", _ => "◈" }, 16,
            source == "邮箱" ? IslandTheme.Accent : IslandTheme.Ink);
        fallback.ToolTip = source;
        fallback.VerticalAlignment = VerticalAlignment.Center;
        fallback.Margin = new Thickness(3, 0, 5, 0);
        return fallback;
    }

    public static FrameworkElement Settings(string page)
    {
        var rows = new StackPanel();
        SettingsRows.Section(rows, page);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Save(ActivityOptions options)
        {
            try
            {
                LocalDataStore.WriteProtected("activity.bin", options);
                LocalDataStore.WriteAtomic(Path.Combine(Folder, "approval"), ApprovalSources(options));
                Options = options;
                SettingsChanged?.Invoke();
                status.Text = "已保存";
            }
            catch (Exception) { status.Text = "无法保存设置"; }
        }
        void Toggle(string caption, bool value, Func<bool, ActivityOptions> change)
        {
            var check = new CheckBox { IsChecked = value };
            check.Checked += (_, _) => Save(change(true));
            check.Unchecked += (_, _) => Save(change(false));
            rows.Children.Add(SettingsRows.Row(caption, check));
        }
        if (page == "AI 会话")
        {
            Toggle("启用 AI Agent 状态灯", Options.Enabled, x => Options with { Enabled = x });
            var install = new Button { Content = "安装 / 修复生命周期钩子" };
            install.Click += (_, _) =>
            {
                try { InstallHooks(); status.Text = "已安装。请重新启动工具，并在 Codex 中信任钩子。"; }
                catch (Exception ex) { status.Text = $"钩子安装失败 · {ex.Message}"; }
            };
            rows.Children.Add(install);
            SettingsRows.Section(rows, "会话来源");
            Toggle("Codex", Options.Codex, x => Options with { Codex = x });
            Toggle("Claude Code", Options.Claude, x => Options with { Claude = x });
            SettingsRows.Section(rows, "小岛显示与提醒");
            Toggle("运行时显示会话标题和数量", Options.Compact, x => Options with { Compact = x });
            Toggle("会话完成时提醒", Options.Completed, x => Options with { Completed = x });
            Toggle("等待确认或出错时提醒", Options.Attention, x => Options with { Attention = x });
            Toggle("提示音", Options.Sound, x => Options with { Sound = x });
            Toggle("在岛上审批 Codex 和 Claude Code 请求", Options.Approval, x => Options with { Approval = x });
        }
        else if (page == "待办")
        {
            Toggle("启用待办", Options.Todo, x => Options with { Todo = x });
            SettingsRows.Section(rows, "任务");
            Toggle("未完成任务顺延至今天", Options.CarryTodo, x => Options with { CarryTodo = x });
            Toggle("番茄钟到点提醒", Options.Pomodoro, x => Options with { Pomodoro = x });
            var minutes = new Slider { Minimum = 1, Maximum = 90, Value = Options.PomodoroMinutes,
                IsSnapToTickEnabled = true, TickFrequency = 1, AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft };
            minutes.ValueChanged += (_, _) => Save(Options with { PomodoroMinutes = (int)minutes.Value });
            rows.Children.Add(SettingsRows.Row("番茄钟时长（分钟）", minutes));
        }
        else
        {
            Toggle("记录岛上消息", Options.Messages, x => Options with { Messages = x });
            var duration = new Slider { Minimum = 2, Maximum = 20, Value = Options.Seconds, IsSnapToTickEnabled = true, TickFrequency = 1 };
            duration.ValueChanged += (_, _) => Save(Options with { Seconds = (int)duration.Value });
            rows.Children.Add(SettingsRows.Row("岛上通知停留时长（秒）", duration));
            SettingsRows.Section(rows, "QQ 与微信");
            Toggle("接收 QQ 消息", Options.Qq, x => Options with { Qq = x });
            Toggle("接收微信提醒", Options.WeChat, x => Options with { WeChat = x });
            Toggle("接收企业微信提醒", Options.WeCom, x => Options with { WeCom = x });
            Toggle("显示微信系统通知内容", Options.WeChatPreview, x => Options with { WeChatPreview = x });
            var access = new Button { Content = "请求 Windows 通知访问权限" };
            access.Click += async (_, _) =>
            {
                try
                {
                    var permission = await Windows.UI.Notifications.Management.UserNotificationListener.Current.RequestAccessAsync();
                    status.Text = permission == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed
                        ? "已取得 Windows 通知访问权限" : "Windows 尚未授予通知访问权限";
                }
                catch (Exception) { status.Text = "当前运行环境无法请求 Windows 通知访问权限"; }
            };
            rows.Children.Add(access);
            SettingsRows.Section(rows, "邮箱");
            Toggle("启用邮箱", Options.Mail, x => Options with { Mail = x });
            var accounts = new StackPanel();
            rows.Children.Add(accounts);
            void RefreshAccounts()
            {
                accounts.Children.Clear();
                foreach (var account in MessageSources.Accounts)
                {
                    var disconnect = new Button { Content = "断开" };
                    disconnect.Click += (_, _) =>
                    {
                        try { MessageSources.Remove(account.Address); RefreshAccounts(); }
                        catch (Exception) { status.Text = "无法移除邮箱"; }
                    };
                    accounts.Children.Add(SettingsRows.Row(account.Address, disconnect));
                }
            }
            RefreshAccounts();
            SettingsRows.Section(rows, "添加邮箱");
            var address = new TextBox();
            var secret = new PasswordBox();
            var host = new TextBox { ToolTip = "可选，默认 imap.邮箱域名，端口默认 993" };
            rows.Children.Add(SettingsRows.Row("邮箱地址", address));
            rows.Children.Add(SettingsRows.Row("邮箱授权码", secret));
            rows.Children.Add(SettingsRows.Row("IMAP 服务器", host));
            var connect = new Button { Content = "连接邮箱" };
            connect.Click += async (_, _) =>
            {
                var email = address.Text.Trim();
                if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email ||
                    string.IsNullOrWhiteSpace(secret.Password))
                { status.Text = "请输入邮箱地址和授权码"; return; }
                var server = host.Text.Trim();
                if (server.Length == 0) server = "imap." + parsed.Host;
                if (!Uri.TryCreate("imaps://" + server, UriKind.Absolute, out var endpoint) ||
                    endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 ||
                    endpoint.Fragment.Length != 0)
                { status.Text = "请输入 IMAP 主机名，可附带端口"; return; }
                connect.IsEnabled = false;
                try
                {
                    await MessageSources.ConnectAsync(new MailAccount(email, secret.Password, endpoint.Host,
                        endpoint.Port < 0 ? 993 : endpoint.Port));
                    secret.Clear();
                    RefreshAccounts();
                    status.Text = "邮箱连接成功";
                }
                catch (Exception) { status.Text = "连接失败，请检查 IMAP 服务器、邮箱地址和授权码"; }
                finally { connect.IsEnabled = true; }
            };
            rows.Children.Add(connect);
        }
        rows.Children.Add(status);
        return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void InstallHooks()
    {
        LocalDataStore.WriteAtomic(Path.Combine(Folder, "approval"), ApprovalSources(Options));
        string script = Path.Combine(Folder, "agent-hook.ps1");
        using var resource = typeof(ActivityPanel).Assembly.GetManifestResourceStream("Island.AgentHook")!;
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        LocalDataStore.WriteAtomic(script, new byte[] { 0xef, 0xbb, 0xbf }.Concat(buffer.ToArray()).ToArray());
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string codex = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        string claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        Install(Path.Combine(codex, "hooks.json"), "Codex", ["UserPromptSubmit", "Stop", "Interrupt", "PermissionRequest"]);
        Install(Path.Combine(claude, "settings.json"), "Claude", ["UserPromptSubmit", "Stop", "StopFailure", "PermissionRequest"]);

        void Install(string path, string source, string[] events)
        {
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            if (root["hooks"] is null) root["hooks"] = new JsonObject();
            var hooks = root["hooks"]!.AsObject();
            foreach (string name in events)
            {
                if (hooks[name] is null) hooks[name] = new JsonArray();
                var groups = hooks[name]!.AsArray();
                string command = $"powershell.exe -NoProfile -File \"{script}\" {source}";
                if (groups.OfType<JsonObject>().Any(group => group["hooks"] is JsonArray list &&
                    list.OfType<JsonObject>().Any(hook => (string?)hook["command"] == command))) continue;
                groups.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = command, ["timeout"] = 70
                }) });
            }
            if (File.Exists(path)) File.Copy(path, path + ".island-" + Guid.NewGuid().ToString("N") + ".bak");
            LocalDataStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static byte[] ApprovalSources(ActivityOptions options) => System.Text.Encoding.ASCII.GetBytes(
        options is { Enabled: true, Approval: true } ? $"{(options.Codex ? "Codex" : "")},{(options.Claude ? "Claude" : "")}" : "");
}
