using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Island.App.Persistence;
using Island.App.Providers;

namespace Island.Mac;

internal sealed record MacPreferences(string Edge = "顶部", bool Light = false, bool Clock = true, string Lyrics = "",
    bool Clipboard = false, int ClipboardLimit = 30, int Scale = 100, string Shape = "刘海");

internal sealed class IslandWindow : Window
{
    private readonly TodoStore _todo = new();
    private readonly Dictionary<(string, string), AgentSession> _sessions = new();
    private readonly List<string> _shelf = [];
    private readonly List<string> _clipboard = [];
    private readonly TextBlock _summary = Text("Windows Island · Mac", 13);
    private readonly TextBlock _status = Text("", 11);
    private readonly StackPanel _body = new();
    private readonly DockPanel _panel = new();
    private readonly Border _root = new();
    private readonly DispatcherTimer _collapse;
    private readonly DispatcherTimer _tick;
    private MacPreferences _prefs = Load<MacPreferences>("preferences.json") ?? new();
    private DateTime _date = DateTime.Today;
    private string _page = "AI 会话";
    private bool _expanded;
    private bool _polling;
    private bool _mailBusy;
    private string? _lastClipboard;
    private MailMessage[] _messages = [];
    private (string Host, string Address, string Password)? _mail;
    private MusicTrack? _track;
    private string? _compactAlert;
    private DateTimeOffset _alertEnd;
    private DateTimeOffset _lastMail;

    public IslandWindow()
    {
        Title = "灵动岛";
        CanResize = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        _root.Background = new SolidColorBrush(Color.Parse("#151B23"));
        _root.CornerRadius = new CornerRadius(12);
        _root.ClipToBounds = true;
        _root.Child = _summary;
        Content = _root;
        _summary.HorizontalAlignment = HorizontalAlignment.Center;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(12, 0);
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        var navigation = new WrapPanel { Margin = new Thickness(0, 0, 0, 9) };
        foreach (string name in new[] { "AI 会话", "AI 额度", "待办", "音乐", "剪贴板", "文件", "系统", "设备", "消息", "小红书", "设置" })
            navigation.Children.Add(Button(name, () => SelectPage(name)));
        DockPanel.SetDock(navigation, Dock.Top);
        _panel.Children.Add(navigation);
        _status.Margin = new Thickness(0, 6, 0, 0);
        DockPanel.SetDock(_status, Dock.Bottom);
        _panel.Children.Add(_status);
        _panel.Children.Add(new ScrollViewer { Content = _body });
        DragDrop.SetAllowDrop(_root, true);
        _root.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;
            AddFiles(files);
            Expand();
            SelectPage("文件");
            e.Handled = true;
        });
        foreach (var entry in Load<AgentSession[]>("agent-sessions.json") ?? [])
            _sessions[(entry.Source, entry.Id)] = entry;
        _clipboard.AddRange(Load<string[]>("clipboard.json") ?? []);
        _collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(210) };
        _collapse.Tick += (_, _) =>
        {
            if (_root.IsPointerOver || FocusManager?.GetFocusedElement() is TextBox) return;
            _collapse.Stop();
            ShowCompact();
        };
        PointerEntered += (_, _) => { _collapse.Stop(); Expand(); };
        PointerExited += (_, _) => _collapse.Start();
        Opened += (_, _) => { ApplyTheme(); ShowCompact(); _ = Poll(); };
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _ = Poll();
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); _collapse.Stop(); };
    }

    private static T? Load<T>(string name)
    {
        try { return LocalDataStore.ReadProtected<T>(name); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return default; }
    }

    private static TextBlock Text(string value, double size = 13) =>
        new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static Button Button(string caption, Action action)
    {
        var button = new Button { Content = caption, Margin = new Thickness(0, 0, 5, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private Button ActionButton(string caption, Func<Task> action) => Button(caption, async () =>
    {
        try { await action(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    });

    private static WrapPanel Row(params Control[] items)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = _prefs.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        _root.Background = new SolidColorBrush(Color.Parse(_prefs.Light ? "#F5F1E9" : "#151B23"));
    }

    private void ResizeIsland(double width, double height)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var factor = _prefs.Scale / 100d;
        Width = Math.Min(width * factor, area.Width / screen.Scaling);
        Height = Math.Min(height * factor, area.Height / screen.Scaling);
        int w = (int)(Width * screen.Scaling), h = (int)(Height * screen.Scaling);
        Position = _prefs.Edge switch
        {
            "左侧" => new PixelPoint(area.X, area.Y + (area.Height - h) / 2),
            "右侧" => new PixelPoint(area.Right - w, area.Y + (area.Height - h) / 2),
            "底部" => new PixelPoint(area.X + (area.Width - w) / 2, area.Bottom - h),
            _ => new PixelPoint(area.X + (area.Width - w) / 2, area.Y)
        };
    }

    private void ShowCompact()
    {
        _expanded = false;
        _root.Padding = new Thickness(0);
        _root.Child = _summary;
        _root.CornerRadius = new CornerRadius(_prefs.Shape == "胶囊" ? 18 : 7);
        _summary.IsVisible = _prefs.Shape != "指示条";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var agent = _sessions.Values.Where(item => item.State is "运行中" or "待确认").MaxBy(item => item.Time);
        var now = DateTimeOffset.Now;
        _summary.Text = now < _alertEnd && _compactAlert is not null ? _compactAlert
            : agent is not null ? $"●  {agent.Title}"
            : _prefs.Clock ? now.ToString("M月d日 HH:mm") : "灵动岛";
        if (!_expanded)
            ResizeIsland(_prefs.Shape == "指示条" ? 105 : agent is not null || now < _alertEnd ? 350 : 210,
                _prefs.Shape == "指示条" ? 5 : 34);
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _root.Padding = new Thickness(13);
        _root.CornerRadius = new CornerRadius(15);
        _root.Child = _panel;
        ResizeIsland(700, 410);
        SelectPage(_page);
    }

    public void Reveal(string? page = null)
    {
        Show();
        Expand();
        Activate();
        if (page is not null) SelectPage(page);
    }

    private async Task Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            bool changed = AgentHooks.ReadEvents(_sessions);
            if (changed && _expanded && _page == "AI 会话") ShowAgents();
            var now = DateTimeOffset.Now;
            var due = _todo.Items.Where(item => item.IsDue(now)).ToArray();
            if (due.Length > 0 && _todo.Save(_todo.Items.Select(item => due.Contains(item) ? item with { RemindedAt = now } : item).ToArray()))
            {
                _compactAlert = $"待办提醒 · {due[0].Title}";
                _alertEnd = now.AddSeconds(5);
                if (_expanded) _status.Text = _compactAlert;
            }
            if (_prefs.Clipboard && Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } value &&
                value != _lastClipboard && value.Length <= 16000)
            {
                _lastClipboard = value;
                _clipboard.Remove(value);
                _clipboard.Insert(0, value);
                if (_clipboard.Count > _prefs.ClipboardLimit) _clipboard.RemoveRange(_prefs.ClipboardLimit, _clipboard.Count - _prefs.ClipboardLimit);
                LocalDataStore.WriteProtected("clipboard.json", _clipboard);
                if (_expanded && _page == "剪贴板") ShowClipboard();
            }
            if (_mail.HasValue && now - _lastMail > TimeSpan.FromMinutes(1)) await CheckMail();
            UpdateSummary();
        }
        catch (Exception ex) { if (_expanded) _status.Text = ex.Message; }
        finally { _polling = false; }
    }

    private void SelectPage(string page)
    {
        _page = page;
        _status.Text = "";
        _body.Children.Clear();
        switch (page)
        {
            case "AI 会话": ShowAgents(); break;
            case "AI 额度": ShowQuota(); break;
            case "待办": ShowTodo(); break;
            case "音乐": ShowMusic(); break;
            case "剪贴板": ShowClipboard(); break;
            case "文件": ShowFiles(); break;
            case "系统": ShowSystem(false); break;
            case "设备": ShowSystem(true); break;
            case "消息": ShowMessages(); break;
            case "小红书":
                _body.Children.Add(ActionButton("在浏览器打开小红书", async () => await MacServices.Open("https://www.xiaohongshu.com/explore")));
                break;
            case "设置": ShowSettings(); break;
        }
    }

    private void ShowAgents()
    {
        _body.Children.Clear();
        if (_sessions.Count == 0) _body.Children.Add(Text("最近 2 小时没有 Agent 会话。可在设置中安装 Codex 和 Claude Code 钩子。"));
        foreach (var item in _sessions.Values.OrderByDescending(item => item.Time))
            _body.Children.Add(Row(ActionButton($"●  {item.Title}", async () =>
            {
                if (item.Orca) await MacServices.OpenApp("Orca");
                else if (item.Source == "Codex" && Guid.TryParse(item.Id, out var id))
                    await MacServices.Open($"codex://threads/{id:D}");
                else if (item.Workspace is { } workspace && Directory.Exists(workspace)) await MacServices.Open(workspace);
                else _status.Text = "没有找到可打开的会话窗口。";
            }), Text($"{item.Source} · {item.State} · {item.Model ?? "模型未知"} · {item.Time.ToLocalTime():HH:mm}")));
    }

    private void ShowQuota()
    {
        var result = Text("在 Mac 上使用已登录的 Codex CLI 查询额度。");
        _body.Children.Add(ActionButton("查询 Codex 额度", async () => result.Text = await MacServices.ReadCodexQuota()));
        _body.Children.Add(result);
        var claude = new TextBox { Watermark = "Claude 状态栏 JSON 文件路径" };
        _body.Children.Add(claude);
        _body.Children.Add(ActionButton("读取 Claude 额度", async () =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(claude.Text?.Trim() ?? ""));
            if (!json.RootElement.TryGetProperty("rate_limits", out var limits)) throw new IOException("状态文件没有额度字段。");
            var values = new List<string>();
            foreach (var window in new[] { ("five_hour", "5 小时"), ("seven_day", "7 天") })
                if (limits.TryGetProperty(window.Item1, out var slot) && slot.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    slot.TryGetProperty("used_percentage", out var value) && value.TryGetDouble(out var used))
                    values.Add($"{window.Item2}已用 {used:0.#}%");
            result.Text = values.Count > 0 ? string.Join(" · ", values) : "未找到可用额度";
        }));
    }

    private void SaveTodos(IReadOnlyList<TodoItem> list)
    {
        if (!_todo.Save(list)) { _status.Text = _todo.ReadError ?? "无法保存待办。"; return; }
        ShowTodo();
    }

    private void ReplaceTodo(TodoItem item) => SaveTodos(_todo.Items.Select(row => row.Id == item.Id ? item : row).ToArray());

    private void ShowTodo()
    {
        _body.Children.Clear();
        var date = new DatePicker { SelectedDate = _date };
        date.SelectedDateChanged += (_, _) => { if (date.SelectedDate is { } day && day.Date != _date) { _date = day.Date; ShowTodo(); } };
        _body.Children.Add(Row(date, Button("今天", () => { _date = DateTime.Today; ShowTodo(); }),
            ActionButton("导入 Excel", () => Excel(true)), ActionButton("导出 Excel", () => Excel(false))));
        var input = new TextBox { Watermark = "添加待办", Width = 260 };
        void Add()
        {
            if (input.Text?.Trim() is not { Length: > 0 } title) return;
            SaveTodos(_todo.Items.Append(new TodoItem(Guid.NewGuid(), title, null, 0, PlannedDay: DateOnly.FromDateTime(_date))).ToArray());
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        _body.Children.Add(Row(input, Button("添加", Add), Button("当天笔记", ShowNote)));
        var dayKey = DateOnly.FromDateTime(_date);
        foreach (var item in _todo.Items.Where(item => item.Weekly != 0
                     ? item.VisibleToday(new DateTimeOffset(_date)) && item.Reminder?.LocalDateTime.Date <= _date
                     : (item.PlannedDay ?? DateOnly.FromDateTime(item.Reminder?.LocalDateTime ?? DateTime.Today)) == dayKey))
        {
            bool done = item.IsDone(new DateTimeOffset(_date));
            bool timing = item.StartedAt.HasValue && !item.EndedAt.HasValue;
            _body.Children.Add(Row(Button(done ? "✓" : "○", () => ReplaceTodo(item with { CompletedAt = done ? null : DateTimeOffset.Now })),
                Button(item.DurationMinutes is { } minutes ? $"{item.Title} · {minutes} 分钟" : item.Title, () => EditTodo(item)),
                Button(timing ? "结束" : "开始", () => ReplaceTodo(timing ? item with { EndedAt = DateTimeOffset.Now }
                    : item with { StartedAt = DateTimeOffset.Now, EndedAt = null })),
                Button("↑", () => MoveTodo(item, -1)), Button("↓", () => MoveTodo(item, 1)),
                Button("删除", () => SaveTodos(_todo.Items.Where(row => row.Id != item.Id).ToArray()))));
        }
    }

    private void MoveTodo(TodoItem item, int step)
    {
        var items = _todo.Items.ToList();
        int index = items.FindIndex(row => row.Id == item.Id);
        int next = index + step;
        if (index < 0 || next < 0 || next >= items.Count) return;
        (items[next], items[index]) = (items[index], items[next]);
        SaveTodos(items);
    }

    private void ShowNote()
    {
        _body.Children.Clear();
        string name = $"note-{_date:yyyy-MM-dd}.json";
        var note = new TextBox { Text = Load<string>(name) ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220 };
        note.TextChanged += (_, _) => LocalDataStore.WriteProtected(name, note.Text ?? "");
        _body.Children.Add(Button("返回待办", ShowTodo));
        _body.Children.Add(note);
    }

    private void EditTodo(TodoItem item)
    {
        _body.Children.Clear();
        var title = new TextBox { Text = item.Title };
        var reminder = new TextBox { Text = item.Reminder?.ToString("yyyy-MM-dd HH:mm"), Watermark = "提醒时间 yyyy-MM-dd HH:mm" };
        var link = new TextBox { Text = item.Url, Watermark = "https://…" };
        var days = new CheckBox[7];
        for (int i = 0; i < days.Length; i++) days[i] = new CheckBox
        { Content = "日一二三四五六"[i].ToString(), IsChecked = (item.Weekly & (1 << i)) != 0 };
        _body.Children.Add(title);
        _body.Children.Add(reminder);
        _body.Children.Add(link);
        _body.Children.Add(Row(days));
        _body.Children.Add(Row(Button("保存", () =>
        {
            if (title.Text?.Trim() is not { Length: > 0 } text) { _status.Text = "请输入标题"; return; }
            DateTimeOffset? time = null;
            if (!string.IsNullOrWhiteSpace(reminder.Text))
            {
                if (!DateTimeOffset.TryParse(reminder.Text, out var parsed)) { _status.Text = "请输入有效日期"; return; }
                time = parsed;
            }
            var url = link.Text?.Trim();
            if (!string.IsNullOrEmpty(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            { _status.Text = "请输入有效的 HTTP 或 HTTPS 链接"; return; }
            int weekly = time.HasValue ? days.Select((box, i) => box.IsChecked == true ? 1 << i : 0).Sum() : 0;
            ReplaceTodo(item with { Title = text, Reminder = time, Weekly = weekly, Url = url,
                RemindedAt = time == item.Reminder && weekly == item.Weekly ? item.RemindedAt : null });
        }), Button("取消", ShowTodo)));
        if (Uri.TryCreate(item.Url, UriKind.Absolute, out var existing) && existing.Scheme is "http" or "https")
            _body.Children.Add(ActionButton("打开链接", async () => await MacServices.Open(existing.AbsoluteUri)));
    }

    private async Task Excel(bool import)
    {
        var format = new FilePickerFileType("Excel") { Patterns = ["*.xlsx"], AppleUniformTypeIdentifiers = ["org.openxmlformats.spreadsheetml.sheet"] };
        if (import)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { FileTypeFilter = [format] });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
                SaveTodos(TodoSpreadsheet.Merge(_todo.Items, await Task.Run(() => TodoSpreadsheet.Import(path))));
        }
        else
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            { SuggestedFileName = "灵动岛待办.xlsx", DefaultExtension = "xlsx", FileTypeChoices = [format] });
            if (file?.TryGetLocalPath() is { } path)
            {
                var snapshot = _todo.Items;
                await Task.Run(() => TodoSpreadsheet.Export(path, snapshot));
                _status.Text = $"已导出 {snapshot.Count} 条待办";
            }
        }
    }

    private void ShowMusic()
    {
        var playing = Text("刷新以读取 Apple Music 或 Spotify。");
        async Task Refresh()
        {
            _track = await MacServices.ReadMusic();
            var track = _track;
            if (track is null) { playing.Text = "没有可用的音乐会话"; return; }
            var line = LocalLyrics.Find(_prefs.Lyrics, track.Title, track.Artist, null, track.App)
                .LastOrDefault(lyric => lyric.At.TotalSeconds <= track.Position);
            playing.Text = $"{track.Title} · {track.Artist}\n{line?.Text}";
        }
        _body.Children.Add(playing);
        _body.Children.Add(Row(ActionButton("刷新", Refresh),
            ActionButton("上一首", async () => { await MacServices.MusicCommand(_track, "previous track"); await Refresh(); }),
            ActionButton("播放 / 暂停", async () => { await MacServices.MusicCommand(_track, "playpause"); await Refresh(); }),
            ActionButton("下一首", async () => { await MacServices.MusicCommand(_track, "next track"); await Refresh(); })));
        var volume = new Slider { Minimum = 0, Maximum = 100, Width = 180, Value = 50 };
        _body.Children.Add(Row(volume, ActionButton("应用系统音量", async () =>
            await MacServices.AppleScript($"set volume output volume {(int)volume.Value}"))));
    }

    private void ShowClipboard()
    {
        _body.Children.Clear();
        _body.Children.Add(Text(_prefs.Clipboard ? "文本剪贴板历史" : "可在设置中启用文本剪贴板记录。"));
        _body.Children.Add(Button("清空历史", () => { _clipboard.Clear(); LocalDataStore.WriteProtected("clipboard.json", _clipboard); ShowClipboard(); }));
        foreach (string value in _clipboard)
        {
            var preview = value.Replace('\n', ' ');
            if (preview.Length > 70) preview = preview[..70] + "…";
            _body.Children.Add(ActionButton(preview, async () =>
            {
                if (Clipboard is { } target) { _lastClipboard = value; await target.SetTextAsync(value); }
            }));
        }
    }

    private void AddFiles(IEnumerable<IStorageItem> files)
    {
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path && !_shelf.Contains(path)) _shelf.Add(path);
    }

    private void ShowFiles()
    {
        _body.Children.Clear();
        _body.Children.Add(Text("把文件拖到岛上，或点击添加。"));
        _body.Children.Add(ActionButton("添加文件", async () =>
        {
            AddFiles(await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true }));
            ShowFiles();
        }));
        foreach (var path in _shelf)
            _body.Children.Add(Row(ActionButton(Path.GetFileName(path), async () => await MacServices.Open(path)),
                ActionButton("在 Finder 中显示", async () => await MacServices.Run("/usr/bin/open", "-R", "--", path)),
                Button("移除", () => { _shelf.Remove(path); ShowFiles(); })));
    }

    private void ShowSystem(bool device)
    {
        var result = Text(device ? "查看本机电池和系统报告的设备信息。" : "查看 CPU、内存和网络系统统计。", 12);
        _body.Children.Add(ActionButton("刷新", async () =>
        {
            if (device)
            {
                var battery = MacServices.SummarizeBattery(await MacServices.Run("/usr/bin/pmset", "-g", "batt"));
                var bt = await MacServices.Run("/usr/sbin/system_profiler", "SPBluetoothDataType", "-detailLevel", "mini");
                result.Text = $"电脑 · {battery}\n{bt}";
            }
            else
            {
                var data = await Task.WhenAll(MacServices.Run("/usr/bin/top", "-l", "2", "-n", "0"),
                    MacServices.Run("/usr/sbin/netstat", "-ibn"), MacServices.Run("/bin/ps", "-axo", "comm,%cpu,%mem", "-r"));
                result.Text = string.Join('\n', data[0].Split('\n').Where(line => line.StartsWith("CPU usage") ||
                    line.StartsWith("PhysMem")).TakeLast(2).Concat(data[1].Split('\n').Take(8)).Concat(data[2].Split('\n').Take(7)));
            }
        }));
        _body.Children.Add(result);
    }

    private async Task CheckMail()
    {
        if (_mailBusy || _mail is not { } account) return;
        _mailBusy = true;
        _lastMail = DateTimeOffset.Now;
        try
        {
            var next = await MacServices.ReadMail(account.Host, 993, account.Address, account.Password);
            if (next.Except(_messages).Any())
            {
                _compactAlert = "邮箱收到新消息";
                _alertEnd = DateTimeOffset.Now.AddSeconds(5);
            }
            _messages = next;
            if (_expanded && _page == "消息") ShowMessages();
        }
        finally { _mailBusy = false; }
    }

    private void ShowMessages()
    {
        _body.Children.Clear();
        _body.Children.Add(Row(ActionButton("微信", async () => await MacServices.OpenApp("WeChat")),
            ActionButton("企业微信", async () => await MacServices.OpenApp("WeCom")),
            ActionButton("QQ", async () => await MacServices.OpenApp("QQ"))));
        var host = new TextBox { Watermark = "IMAP 服务器（TLS 993）", Text = _mail?.Host };
        var address = new TextBox { Watermark = "邮箱地址", Text = _mail?.Address };
        var password = new TextBox { Watermark = "邮箱授权码", PasswordChar = '●' };
        _body.Children.Add(host);
        _body.Children.Add(address);
        _body.Children.Add(password);
        _body.Children.Add(Row(ActionButton("连接邮箱", async () =>
        {
            if (host.Text?.Trim() is not { Length: > 0 } server || address.Text?.Trim() is not { Length: > 0 } email ||
                password.Text is not { Length: > 0 } secret) throw new ArgumentException("请填写服务器、邮箱和授权码。");
            _mail = (server, email, secret);
            try { await CheckMail(); }
            catch { _mail = null; throw; }
        }), ActionButton("刷新邮箱", CheckMail), Button("断开邮箱", () => { _mail = null; _messages = []; ShowMessages(); })));
        foreach (var message in _messages)
            _body.Children.Add(Text($"{message.When.ToLocalTime():HH:mm} · {message.Sender}\n{message.Subject}"));
    }

    private void ShowSettings()
    {
        var edge = new ComboBox { ItemsSource = new[] { "顶部", "底部", "左侧", "右侧" }, SelectedItem = _prefs.Edge };
        var shape = new ComboBox { ItemsSource = new[] { "刘海", "胶囊", "指示条" }, SelectedItem = _prefs.Shape };
        var light = new CheckBox { Content = "浅色外观", IsChecked = _prefs.Light };
        var clock = new CheckBox { Content = "收起时显示时钟", IsChecked = _prefs.Clock };
        var clipboard = new CheckBox { Content = "记录文本剪贴板", IsChecked = _prefs.Clipboard };
        var lyrics = new TextBox { Text = _prefs.Lyrics, Watermark = "本地歌词目录" };
        var scale = new Slider { Minimum = 80, Maximum = 140, Value = _prefs.Scale, Width = 160 };
        _body.Children.Add(Row(edge, shape, light, clock, clipboard, scale));
        _body.Children.Add(lyrics);
        _body.Children.Add(Row(ActionButton("保存设置", () =>
        {
            _prefs = _prefs with
            {
                Edge = edge.SelectedItem as string ?? "顶部", Shape = shape.SelectedItem as string ?? "刘海",
                Light = light.IsChecked == true, Clock = clock.IsChecked == true, Clipboard = clipboard.IsChecked == true,
                Lyrics = lyrics.Text?.Trim() ?? "", Scale = (int)scale.Value
            };
            LocalDataStore.WriteProtected("preferences.json", _prefs);
            ApplyTheme();
            ResizeIsland(700, 410);
            _status.Text = "已保存";
            return Task.CompletedTask;
        }), ActionButton("安装 Agent 钩子", () =>
        {
            AgentHooks.Install();
            _status.Text = "钩子已安装。重启工具，并按 Codex 的提示信任钩子。";
            return Task.CompletedTask;
        }), ActionButton("发布页", async () => await MacServices.Open("https://github.com/Myjianglai/WindowsIsland/releases")),
            Button("退出", () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown())));
    }
}
