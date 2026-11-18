using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Persistence;
using Island.Core.Events;
using Microsoft.Win32;

namespace Island.App.UI;

internal sealed class TodoView : Grid
{
    private sealed record DayChoice(DateTime Day, string Caption);
    private readonly TodoStore _store;
    private readonly HashSet<Guid> _remindedWithoutSave = [];
    private readonly TextBlock _counter = ExpandedTabs.Label("0/0", 35);
    private readonly TextBlock _status = ExpandedTabs.Label("", 11, IslandTheme.Muted);
    private readonly Border _card = new();
    private readonly StackPanel _leftActions = new();
    private readonly ContentControl _reminderOptions = new();
    private readonly ProgressBar _progress = new() { Width = 95, Height = 5, Minimum = 0, Maximum = 1,
        Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(Button Button, string Name, string Icon)> _tabs = [];
    private readonly Button _excel = ExpandedTabs.MakeButton("");
    private int _mode;
    private Action? _resetQuickAdd;
    private FrameworkElement? _editorSurface;
    private bool _editorHasFocus;
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private Popup? _datePicker;
    public bool IsDatePickerOpen => _datePicker?.IsOpen == true;
    private DateTime _today = DateTime.Today;
    private DateTime _day = DateTime.Today;
    private readonly HashSet<(Guid, DateTimeOffset)> _pomodoroAlerts = [];
    private readonly StackPanel _navigation = new() { Orientation = Orientation.Horizontal };
    public event Action<bool>? EditingChanged;
    public event Action? ItemsChanged;
    public bool IsEditing { get; private set; }
    public bool HoldsKeyboard => IsEditing && (_editorSurface is null || _editorHasFocus);

    public void ReleaseEditorFocus(bool outsideWindow = false)
    {
        var editor = _editorSurface;
        if (editor is null || !_editorHasFocus ||
            (!outsideWindow && (editor.IsMouseOver || _reminderOptions.IsMouseOver))) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ReferenceEquals(_editorSurface, editor) || !_editorHasFocus) return;
            if (editor is TextBox { Text.Length: 0 } && _resetQuickAdd is not null)
            {
                CancelEditing();
                return;
            }
            _editorHasFocus = false;
            if (editor.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            EditingChanged?.Invoke(false);
        }));
    }

    private void TrackEditor(FrameworkElement editor)
    {
        _editorSurface = editor;
        editor.PreviewMouseDown += (_, _) =>
        {
            if (!ReferenceEquals(_editorSurface, editor) || _editorHasFocus) return;
            if (!IsEditing) return;
            _editorHasFocus = true;
            EditingChanged?.Invoke(true);
        };
    }
    public TodoPreview? GetPreview(DateTimeOffset now) => _store.GetPreview(now);

    public TodoView(TodoStore? store = null)
    {
        _store = store ?? new TodoStore();
        ActivityPanel.SettingsChanged += () => { if (!IsEditing && _mode != 1) Render(); };
        Margin = new Thickness(8, 5, 5, 7);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var header = new DockPanel();
        var navigation = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left, Child = _navigation };
        navigation.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        foreach (var (caption, glyph) in new (string, string)[]
        {
            ("待办", "\uE9D5"), ("笔记", "\uE70F"), ("日历", "\uE787")
        })
        {
            int index = _tabs.Count;
            var button = ExpandedTabs.MakeFlatButton("");
            button.ToolTip = caption;
            System.Windows.Automation.AutomationProperties.SetName(button, caption);
            button.Click += (_, _) => SelectMode(index);
            _tabs.Add((button, caption, glyph));
            _navigation.Children.Add(button);
        }
        _counter.FontSize = 13;
        _counter.VerticalAlignment = VerticalAlignment.Center;
        _counter.FontWeight = FontWeights.Bold;
        _progress.SetResourceReference(Control.ForegroundProperty, IslandTheme.Accent);
        var completion = new StackPanel { Orientation = Orientation.Horizontal, Children = { _progress, _counter } };
        _excel.Content = IconCaption("\uE9F9", "Excel");
        _excel.SetResourceReference(StyleProperty, "IslandExcelButton");
        _excel.Margin = new Thickness(8, 0, 0, 0);
        _excel.ToolTip = "导入或导出全部待办";
        System.Windows.Automation.AutomationProperties.SetName(_excel, "Excel 导入导出");
        completion.Children.Add(_excel);
        DockPanel.SetDock(completion, Dock.Right);
        header.Children.Add(completion);
        header.Children.Add(navigation);
        left.Children.Add(header);
        var add = ExpandedTabs.MakeFlatButton("＋   添加待办");
        add.HorizontalContentAlignment = HorizontalAlignment.Left;
        var quickAdd = new ContentControl { Content = add, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => StartQuickAdd(quickAdd);
        var actions = new DockPanel { Margin = new Thickness(0, 5, 0, 0), MinHeight = 33 };
        var bar = new Border { Child = actions, CornerRadius = new CornerRadius(7) };
        bar.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        _leftActions.Children.Add(bar);
        var import = new MenuItem { Header = "从 Excel 导入到全部待办", Icon = Glyph("\uE896") };
        var export = new MenuItem { Header = "将全部待办导出到 Excel", Icon = Glyph("\uE898") };
        var detailed = new MenuItem { Header = "添加待办并设置详情" };
        detailed.Click += (_, _) => OpenEditor(null, reminder: false);
        import.Click += async (_, _) => await TransferExcelAsync(import: true);
        export.Click += async (_, _) => await TransferExcelAsync(import: false);
        var menu = new ContextMenu { Placement = PlacementMode.Bottom, VerticalOffset = 4, Items = { import, export } };
        menu.SetResourceReference(StyleProperty, "IslandExcelMenu");
        _excel.ContextMenu = menu;
        _excel.Click += (_, _) => { menu.PlacementTarget = _excel; menu.IsOpen = true; };
        actions.ContextMenu = new ContextMenu { Items = { detailed } };
        var selectedDate = ExpandedTabs.MakeFlatButton("");
        var dateCaption = ExpandedTabs.Label("今天", 12);
        selectedDate.Content = IconCaption("\uE787", dateCaption);
        System.Windows.Automation.AutomationProperties.SetName(selectedDate, "选择待办日期");
        selectedDate.Click += (_, _) => OpenDatePicker(selectedDate);
        DockPanel.SetDock(selectedDate, Dock.Right);
        actions.Children.Add(selectedDate);
        left.Children.Add(new FrameworkElement { Height = 2 });
        actions.Children.Add(quickAdd);
        LayoutUpdated += (_, _) =>
        {
            string label = $"＋   添加 {_day:yyyy-MM-dd} 的待办";
            if (!Equals(add.Content, label)) add.Content = label;
            string date = _day == _today ? "今天" : $"{_day:MM-dd}";
            if (dateCaption.Text != date) dateCaption.Text = date;
        };
        left.Children.Add(_leftActions);
        left.Children.Add(_reminderOptions);
        _status.Margin = new Thickness(0, 2, 0, 0);
        left.Children.Add(_status);
        Children.Add(left);
        SetRow(_card, 1);
        Children.Add(_card);
        SelectMode(0);
    }

    private async Task TransferExcelAsync(bool import)
    {
        if (IsEditing) return;
        if (_store.ReadError is { } error)
        {
            _status.Text = error;
            return;
        }
        const string filter = "Excel 工作簿 (*.xlsx)|*.xlsx";
        SetEditing(true);
        try
        {
            var owner = Window.GetWindow(this);
            if (import)
            {
                var dialog = new OpenFileDialog { Title = "从 Excel 导入到全部待办", Filter = filter, CheckFileExists = true };
                if (dialog.ShowDialog(owner) != true) return;
                var imported = await Task.Run(() => TodoSpreadsheet.Import(dialog.FileName));
                var merged = TodoSpreadsheet.Merge(_store.Items, imported);
                int added = merged.Length - _store.Items.Count;
                if (added == 0 || Commit(merged))
                    _status.Text = $"导入 {added} 项，跳过 {imported.Length - added} 项";
            }
            else
            {
                var dialog = new SaveFileDialog { Title = "导出全部待办", Filter = filter, DefaultExt = ".xlsx",
                    AddExtension = true, FileName = "灵动岛待办.xlsx" };
                if (dialog.ShowDialog(owner) != true) return;
                var snapshot = _store.Items;
                await Task.Run(() => TodoSpreadsheet.Export(dialog.FileName, snapshot));
                _status.Text = $"已导出 {snapshot.Count} 项";
            }
        }
        catch (InvalidDataException ex) { _status.Text = ex.Message; }
        catch (Exception) { _status.Text = import ? "无法读取该 Excel 文件" : "无法保存 Excel 文件"; }
        finally
        {
            string feedback = _status.Text;
            SetEditing(false);
            Render();
            if (!string.IsNullOrEmpty(feedback)) _status.Text = feedback;
        }
    }

    public void CheckReminders(DateTimeOffset now, Func<IslandEvent, bool> publish)
    {
        if (now.LocalDateTime.Date != _today && !IsEditing)
        {
            if (_day == _today) _day = now.LocalDateTime.Date;
            _today = now.LocalDateTime.Date;
            Render();
        }
        if (ActivityPanel.Options.Pomodoro)
        {
            foreach (var item in _store.Items)
                if (item.StartedAt is { } start && item.EndedAt is null && !item.IsDone(now) &&
                    start.AddMinutes(ActivityPanel.Options.PomodoroMinutes) <= now && !_pomodoroAlerts.Contains((item.Id, start)) &&
                    publish(new IslandEvent("番茄钟", IslandEventType.Task, item.Title, "计时结束")))
                    _pomodoroAlerts.Add((item.Id, start));
        }
        foreach (var item in _store.Items.Where(item => item.IsDue(now)).ToArray())
        {
            if (_remindedWithoutSave.Contains(item.Id)) continue;
            var alert = new IslandEvent("待办清单", IslandEventType.Task, item.Title, "到提醒时间了");
            if (!publish(alert)) break;
            if (!_store.Save(_store.Items.Select(row => row.Id == item.Id ? row with { RemindedAt = now } : row).ToArray()))
            {
                _remindedWithoutSave.Add(item.Id);
                _status.Text = "提醒状态无法保存";
                break;
            }
        }
    }

    private void Render()
    {
        _leftActions.Visibility = _mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == 2) { ShowCalendar(); return; }
        var items = ItemsOn(_day);
        int done = items.Count(item => item.IsDone(new DateTimeOffset(_day)));
        _counter.ToolTip = _day.ToString("yyyy-MM-dd");
        _counter.Text = $"{done}/{items.Length}";
        _progress.Value = items.Length == 0 ? 0 : done / (double)items.Length;
        _card.Child = RenderItems(items);
        _status.Text = _store.ReadError ?? "";
    }

    private void StartQuickAdd(ContentControl slot)
    {
        var previous = slot.Content;
        var input = new TextBox { MaxLength = 200, Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "输入标题，按 Enter 添加，按 Esc 取消" };
        input.SetResourceReference(Control.BackgroundProperty, IslandTheme.Surface);
        input.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
        input.SetResourceReference(Control.BorderBrushProperty, IslandTheme.Accent);
        slot.Content = input;
        _resetQuickAdd = () => slot.Content = previous;
        TrackEditor(input);
        SetEditing(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => input.Focus()));
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; CancelEditing(); return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            string title = input.Text.Trim();
            if (title.Length == 0) return;
            if (Replace(new TodoItem(Guid.NewGuid(), title, null, 0, PlannedDay: DateOnly.FromDateTime(_day))))
                CancelEditing();
        };
    }

    private TodoItem[] ItemsOn(DateTime date)
    {
        var now = new DateTimeOffset(date);
        var day = DateOnly.FromDateTime(date);
        return _store.Items.Where(item =>
        {
            if (item.Weekly != 0) return item.VisibleToday(now) && item.Reminder?.LocalDateTime.Date <= date;
            var planned = item.PlannedDay ?? DateOnly.FromDateTime(item.Reminder?.LocalDateTime ?? _today);
            return planned == day || (ActivityPanel.Options.CarryTodo && date == _today && planned < day && !item.IsDone(now));
        }).ToArray();
    }

    private FrameworkElement RenderItems(TodoItem[] items)
    {
        var now = new DateTimeOffset(_day);
        if (items.Length == 0)
        {
            var empty = ExpandedTabs.Label("这一天还没有待办", 14, IslandTheme.Muted);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 32, 0, 0);
            return empty;
        }
        else
        {
            var rows = new StackPanel();
            foreach (var item in items)
            {
                bool done = item.IsDone(now);
                var check = ExpandedTabs.MakeFlatButton(done ? "✓" : "○");
                check.ToolTip = done ? "取消完成" : "标记完成";
                check.SetResourceReference(Control.ForegroundProperty, done ? "AccountGreen" : IslandTheme.Muted);
                check.Click += (_, _) => Replace(item with { CompletedAt = done ? null : DateTimeOffset.Now });
                DockPanel.SetDock(check, Dock.Left);
                var remove = ExpandedTabs.MakeFlatButton("");
                remove.Content = Glyph("\uE74D");
                remove.ToolTip = "删除待办";
                remove.Click += (_, _) => Commit(_store.Items.Where(row => row.Id != item.Id).ToArray());
                DockPanel.SetDock(remove, Dock.Right);
                var clock = ExpandedTabs.MakeFlatButton("◷");
                clock.ToolTip = item.Reminder is { } at ? at.LocalDateTime.ToString("MM-dd HH:mm") : "设置提醒";
                clock.Click += (_, _) => OpenEditor(item, reminder: true);
                DockPanel.SetDock(clock, Dock.Right);
                var start = ExpandedTabs.MakeFlatButton("▶");
                start.ToolTip = item.StartedAt is null ? "开始计时" : "重新开始计时";
                start.Click += (_, _) => Replace(item with { StartedAt = DateTimeOffset.Now, EndedAt = null });
                DockPanel.SetDock(start, Dock.Right);
                var finish = ExpandedTabs.MakeFlatButton("■");
                finish.ToolTip = item.StartedAt is null ? "请先开始计时" : item.EndedAt is null ? "结束计时" : $"已用 {item.DurationMinutes} 分钟";
                finish.IsEnabled = item.StartedAt is not null && item.EndedAt is null;
                finish.Click += (_, _) => Replace(item with { EndedAt = DateTimeOffset.Now });
                DockPanel.SetDock(finish, Dock.Right);
                string timerDetails = item.StartedAt is { } startedAt
                    ? $"开始 {startedAt.LocalDateTime:MM-dd HH:mm}" +
                      (item.EndedAt is { } endedAt
                          ? $"，结束 {endedAt.LocalDateTime:MM-dd HH:mm}，共 {item.DurationMinutes} 分钟"
                          : "，计时中")
                    : "尚未开始计时";
                string titleText = item.DurationMinutes is { } duration ? $"{item.Title} · {duration} 分钟" : item.Title;
                var body = ExpandedTabs.MakeFlatButton("");
                body.HorizontalContentAlignment = HorizontalAlignment.Left;
                var label = ExpandedTabs.Label(titleText, 13, done ? IslandTheme.Muted : IslandTheme.Ink);
                if (done) label.TextDecorations = TextDecorations.Strikethrough;
                body.Content = label;
                body.ToolTip = $"{item.Title}\n{timerDetails}";
                body.Click += (_, _) => OpenEditor(item, reminder: false);
                var options = new ContextMenu();
                void AddAction(string caption, Button button)
                {
                    var entry = new MenuItem { Header = caption, IsEnabled = button.IsEnabled };
                    entry.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    options.Items.Add(entry);
                }
                AddAction("设置提醒", clock);
                AddAction("开始计时", start);
                AddAction("结束计时", finish);
                var grip = ExpandedTabs.MakeFlatButton("⠿");
                grip.ToolTip = "拖动排序，或点击查看操作";
                grip.ContextMenu = options;
                grip.Click += (_, _) => { options.PlacementTarget = grip; options.IsOpen = true; };
                grip.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton == MouseButtonState.Pressed && grip.IsMouseCaptured)
                        DragDrop.DoDragDrop(grip, new DataObject("WindowsIsland.Todo", item.Id.ToString()), DragDropEffects.Move);
                };
                var actions = new List<UIElement> { check, remove, grip };
                if (TryHttpUrl(item.Url, out var validTarget))
                {
                    var link = ExpandedTabs.MakeFlatButton("↗");
                    link.ToolTip = validTarget.AbsoluteUri;
                    link.Click += (_, _) => OpenUrl(validTarget);
                    AddAction("打开链接", link);
                }
                var row = new Border { Margin = new Thickness(0, 0, 0, 2), AllowDrop = true,
                    Child = new DockPanel { Children = { body } } };
                row.Drop += (_, e) =>
                {
                    if (e.Data.GetData("WindowsIsland.Todo") is not string source || !Guid.TryParse(source, out var id) || id == item.Id) return;
                    var reordered = _store.Items.ToList();
                    var from = reordered.FindIndex(x => x.Id == id);
                    var target = reordered.FindIndex(x => x.Id == item.Id);
                    if (from < 0 || target < 0) return;
                    var dragged = reordered[from];
                    reordered.RemoveAt(from);
                    reordered.Insert(target, dragged);
                    Commit(reordered.ToArray());
                    e.Handled = true;
                };
                var dock = (DockPanel)row.Child;
                foreach (var action in actions)
                {
                    DockPanel.SetDock(action, action == check ? Dock.Left : Dock.Right);
                    dock.Children.Insert(0, action);
                }
                row.SetResourceReference(FrameworkElement.StyleProperty, "TodoListRow");
                rows.Children.Add(row);
            }
            return new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
    }

    public void CancelEditing()
    {
        if (_datePicker is { } picker) picker.IsOpen = false;
        if (!IsEditing) return;
        SetEditing(false);
        if (_mode == 1) { _leftActions.Visibility = Visibility.Collapsed; return; }
        Render();
    }

    private void OpenEditor(TodoItem? item, bool reminder)
    {
        if (IsEditing) SetEditing(false);
        var editor = new StackPanel();
        var text = new TextBox { Text = item?.Title ?? "", MaxLength = 200, Padding = new Thickness(6, 4, 6, 4),
            ToolTip = "要做点什么？回车保存", MinHeight = 27 };
        text.SetResourceReference(Control.BackgroundProperty, IslandTheme.Surface);
        text.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
        text.SetResourceReference(Control.BorderBrushProperty, IslandTheme.Accent);
        var prompt = ExpandedTabs.Label("要做点什么？回车保存", 12, IslandTheme.Muted);
        prompt.IsHitTestVisible = false;
        prompt.Margin = new Thickness(8, 0, 0, 0);
        prompt.VerticalAlignment = VerticalAlignment.Center;
        void UpdatePrompt() => prompt.Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        text.TextChanged += (_, _) => UpdatePrompt();
        UpdatePrompt();
        editor.Children.Add(new Grid { Children = { text, prompt } });
        var url = new TextBox { Text = item?.Url ?? "", MaxLength = 2048, Padding = new Thickness(6, 4, 6, 4),
            ToolTip = "可选，填写 http 或 https 链接", MinHeight = 27, Margin = new Thickness(0, 5, 0, 0) };
        url.SetResourceReference(Control.BackgroundProperty, IslandTheme.Surface);
        url.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
        url.SetResourceReference(Control.BorderBrushProperty, IslandTheme.Accent);
        editor.Children.Add(url);
        var when = item?.Reminder?.LocalDateTime ?? (_day == DateTime.Today ? DateTime.Now.AddMinutes(15) : _day.AddHours(9));
        var today = DateTime.Today;
        var options = Enumerable.Range(0, 366).Select(days => today.AddDays(days)).Append(when.Date).Distinct()
            .Order().Select(day => new DayChoice(day, (day - today).Days switch
            {
                0 => "今天", 1 => "明天", 2 => "后天", _ => day.ToString("M/d", CultureInfo.InvariantCulture)
            })).ToArray();
        var date = new ReminderWheel(options.Length, Array.FindIndex(options, option => option.Day == when.Date),
            index => options[index].Caption, "提醒日期", wrap: false);
        var hours = new ReminderWheel(24, when.Hour, ClockCaption, "小时", wrap: true);
        var minutes = new ReminderWheel(60, when.Minute, ClockCaption, "分钟", wrap: true);
        var time = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var width in new[] { new GridLength(1.65, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
                     GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            time.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var highlight = new Border { Height = 28, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
        highlight.SetResourceReference(Border.BackgroundProperty, IslandTheme.Edge);
        Grid.SetColumnSpan(highlight, 4);
        time.Children.Add(highlight);
        var separator = ExpandedTabs.Label(":", 17);
        separator.VerticalAlignment = VerticalAlignment.Center;
        foreach (var (control, column) in new (FrameworkElement, int)[] { (date, 0), (hours, 1), (separator, 2), (minutes, 3) })
        {
            Grid.SetColumn(control, column);
            time.Children.Add(control);
        }
        var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        var repeat = new CheckBox[7];
        string[] week = ["一", "二", "三", "四", "五", "六", "日"];
        for (int index = 0; index < week.Length; index++)
        {
            int day = (index + 1) % 7;
            var box = new CheckBox { Content = week[index], IsChecked = ((item?.Weekly ?? 0) & (1 << day)) != 0,
                Margin = new Thickness(0, 0, 4, 0) };
            box.SetResourceReference(FrameworkElement.StyleProperty, "TodoRepeatDay");
            System.Windows.Automation.AutomationProperties.SetName(box, $"每周{week[index]}");
            box.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
            repeat[day] = box;
            days.Children.Add(box);
        }
        var addReminder = ExpandedTabs.MakeButton("设置提醒");
        var noReminder = ExpandedTabs.MakeButton("暂不提醒");
        var cancel = ExpandedTabs.MakeButton("取消");
        var save = ExpandedTabs.MakeButton("完成");
        noReminder.SetResourceReference(FrameworkElement.StyleProperty, "IslandCancelButton");
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "IslandCancelButton");
        save.SetResourceReference(FrameworkElement.StyleProperty, "IslandConfirmButton");
        save.MinWidth = 62;
        var repeatPanel = new StackPanel
        {
            Margin = new Thickness(0, 7, 0, 0),
            Children = { ExpandedTabs.Label("每周重复", 11, IslandTheme.Muted), days, noReminder }
        };
        noReminder.HorizontalAlignment = HorizontalAlignment.Left;
        _reminderOptions.Content = repeatPanel;
        bool scheduled = reminder || item?.Reminder is not null;
        void ShowReminder(bool visible)
        {
            scheduled = visible;
            time.Visibility = repeatPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _leftActions.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            addReminder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }
        addReminder.Click += (_, _) => ShowReminder(true);
        noReminder.Click += (_, _) => ShowReminder(false);
        cancel.Click += (_, _) => CancelEditing();
        editor.Children.Add(time);
        editor.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 3, 0, 0), Children = { addReminder, cancel, save } });
        ShowReminder(scheduled);
        void Save()
        {
            string title = text.Text.Trim();
            if (title.Length == 0) { _status.Text = "请输入待办内容"; return; }
            string? address = string.IsNullOrWhiteSpace(url.Text) ? null : url.Text.Trim();
            if (address is not null && !TryHttpUrl(address, out _))
            { _status.Text = "链接需以 http:// 或 https:// 开头"; return; }
            int mask = 0;
            DateTimeOffset? alert = null;
            if (scheduled)
            {
                var at = options[date.SelectedIndex].Day.AddHours(hours.SelectedIndex).AddMinutes(minutes.SelectedIndex);
                if (TimeZoneInfo.Local.IsInvalidTime(at)) { _status.Text = "请选择可用的本地时间"; return; }
                alert = new DateTimeOffset(at);
                for (int index = 0; index < repeat.Length; index++)
                    if (repeat[index].IsChecked == true) mask |= 1 << index;
            }
            var row = new TodoItem(item?.Id ?? Guid.NewGuid(), title, alert, mask, item?.CompletedAt,
                item is not null && item.Reminder == alert && item.Weekly == mask ? item.RemindedAt : null, address,
                item?.StartedAt, item?.EndedAt, item?.PlannedDay ?? DateOnly.FromDateTime(_day));
            if (Replace(row))
            {
                CancelEditing();
            }
        }
        save.Click += (_, _) => Save();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelEditing(); e.Handled = true; }
            else if (e.Key == Key.Enter && text.IsKeyboardFocusWithin) { Save(); e.Handled = true; }
        };
        _card.Child = editor;
        TrackEditor(editor);
        SetEditing(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsEditing && ReferenceEquals(_card.Child, editor)) { text.Focus(); text.CaretIndex = text.Text.Length; }
        }));
    }

    private static string ClockCaption(int value) => value.ToString("00", CultureInfo.InvariantCulture);

    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static StackPanel IconCaption(string glyph, string caption) => IconCaption(glyph, ExpandedTabs.Label(caption, 12));

    private static StackPanel IconCaption(string glyph, TextBlock caption)
    {
        caption.Margin = new Thickness(5, 0, 0, 0);
        caption.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(glyph), caption } };
    }

    private void SelectMode(int mode)
    {
        if (IsEditing) SetEditing(false);
        _mode = mode;
        for (int i = 0; i < _tabs.Count; i++)
        {
            var (button, name, icon) = _tabs[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Glyph(icon));
            if (mode == i)
            {
                var caption = ExpandedTabs.Label(name, 12);
                caption.Margin = new Thickness(5, 0, 0, 0);
                content.Children.Add(caption);
                button.SetResourceReference(Control.BackgroundProperty, IslandTheme.Edge);
            }
            else button.Background = Brushes.Transparent;
            button.Content = content;
        }
        _progress.Visibility = _counter.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        _excel.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        if (mode == 1) ShowNotes();
        else Render();
    }

    private FrameworkElement BuildMonth(DateTime displayed, Action<int> changeMonth, Action<DateTime> selectDate, bool picker)
    {
        var month = new DockPanel { LastChildFill = true };
        var monthHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        DockPanel.SetDock(monthHeader, Dock.Top);
        month.Children.Add(monthHeader);
        Button MonthAction(string caption, string tip, Action action, Dock dock)
        {
            var button = ExpandedTabs.MakeFlatButton(caption);
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            DockPanel.SetDock(button, dock);
            monthHeader.Children.Add(button);
            return button;
        }
        MonthAction("‹", "上个月", () => changeMonth(-1), Dock.Left).IsEnabled = displayed.Year > 1 || displayed.Month > 1;
        if (!picker)
        {
            var today = MonthAction("", "回到今天", () => selectDate(DateTime.Today), Dock.Right);
            today.Content = IconCaption("\uE787", "今天");
        }
        MonthAction("›", "下个月", () => changeMonth(1), Dock.Right).IsEnabled = displayed.Year < 9999 || displayed.Month < 12;
        var caption = ExpandedTabs.Label(displayed.ToString("yyyy年M月", CultureInfo.InvariantCulture), 14);
        caption.FontWeight = FontWeights.SemiBold;
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.VerticalAlignment = VerticalAlignment.Center;
        monthHeader.Children.Add(caption);
        var grid = new UniformGrid { Columns = 7, Rows = 7 };
        foreach (var weekday in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var heading = ExpandedTabs.Label(weekday, 11, IslandTheme.Muted);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            heading.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(heading);
        }
        int offset = ((int)displayed.DayOfWeek + 6) % 7;
        for (int i = 0; i < 42; i++)
        {
            int relative = i - offset;
            if (relative < 0 && displayed == DateTime.MinValue ||
                relative >= 31 && displayed.Year == 9999 && displayed.Month == 12)
            {
                grid.Children.Add(new Border());
                continue;
            }
            var date = displayed.AddDays(relative);
            var button = ExpandedTabs.MakeButton("");
            button.Background = Brushes.Transparent;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(2, 1, 2, 1);
            button.Click += (_, _) => selectDate(date);
            button.ToolTip = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            System.Windows.Automation.AutomationProperties.SetName(button, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var number = ExpandedTabs.Label(date.Day.ToString(CultureInfo.InvariantCulture), 12,
                date.Month == displayed.Month ? IslandTheme.Ink : IslandTheme.Muted);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.VerticalAlignment = VerticalAlignment.Center;
            if (date == _day)
            {
                number.Foreground = Brushes.Black;
                button.SetResourceReference(Control.BackgroundProperty, IslandTheme.Accent);
            }
            button.Content = number;
            grid.Children.Add(button);
        }
        month.Children.Add(grid);
        return month;
    }

    private void OpenDatePicker(Button anchor)
    {
        if (_datePicker is { IsOpen: true } open) { open.IsOpen = false; return; }
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Custom, StaysOpen = false,
            AllowsTransparency = true };
        popup.CustomPopupPlacementCallback = (size, target, _) =>
        [
            new CustomPopupPlacement(new Point(target.Width - size.Width, target.Height + 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(target.Width - size.Width, -size.Height - 4), PopupPrimaryAxis.Horizontal)
        ];
        var contents = new DockPanel();
        var footer = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        contents.Children.Add(footer);
        void Choose(DateTime day) { popup.IsOpen = false; SelectDay(day); }
        var reset = ExpandedTabs.MakeFlatButton("清除日期");
        reset.ToolTip = "取消日期筛选并返回今天";
        reset.Click += (_, _) => Choose(DateTime.Today);
        DockPanel.SetDock(reset, Dock.Right);
        footer.Children.Add(reset);
        var today = ExpandedTabs.MakeFlatButton("今天");
        today.HorizontalAlignment = HorizontalAlignment.Left;
        today.Click += (_, _) => Choose(DateTime.Today);
        footer.Children.Add(today);
        var body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch };
        contents.Children.Add(body);
        var displayed = new DateTime(_day.Year, _day.Month, 1);
        void RefreshMonth() => body.Content = BuildMonth(displayed, offset =>
        {
            displayed = displayed.AddMonths(offset);
            RefreshMonth();
        }, Choose, picker: true);
        RefreshMonth();
        var frame = new Border { Child = contents, Width = 310, Height = 320, Padding = new Thickness(9),
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        frame.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
        popup.Child = frame;
        popup.Closed += (_, _) => { if (ReferenceEquals(_datePicker, popup)) _datePicker = null; };
        _datePicker = popup;
        popup.IsOpen = true;
    }

    private void ShowCalendar()
    {
        _leftActions.Visibility = Visibility.Collapsed;
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56, GridUnitType.Star) });
        var month = BuildMonth(_month, offset => { _month = _month.AddMonths(offset); Render(); }, SelectDay, picker: false);
        var calendar = new Border { Child = month, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        calendar.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        calendar.SetResourceReference(Border.BorderBrushProperty, IslandTheme.Edge);
        layout.Children.Add(calendar);

        var items = ItemsOn(_day);
        int done = items.Count(item => item.IsDone(new DateTimeOffset(_day)));
        var sidebar = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        SetColumn(sidebar, 1);
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var ratio = ExpandedTabs.Label($"{done}/{items.Length} 已完成", 11, IslandTheme.Muted);
        DockPanel.SetDock(ratio, Dock.Right);
        title.Children.Add(ratio);
        var dayTitle = ExpandedTabs.Label(_day == DateTime.Today ? "今天" : _day.ToString("M月d日"), 19);
        dayTitle.FontWeight = FontWeights.SemiBold;
        title.Children.Add(new StackPanel { Children = { dayTitle,
            ExpandedTabs.Label(_day.ToString("yyyy年M月d日dddd", CultureInfo.GetCultureInfo("zh-CN")), 11, IslandTheme.Muted) } });
        DockPanel.SetDock(title, Dock.Top);
        sidebar.Children.Add(title);
        var add = ExpandedTabs.MakeButton("＋   添加待办");
        add.HorizontalContentAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 0, 0, 6);
        var quickAdd = new ContentControl { Content = add, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        add.Click += (_, _) => StartQuickAdd(quickAdd);
        DockPanel.SetDock(quickAdd, Dock.Top);
        sidebar.Children.Add(quickAdd);
        sidebar.Children.Add(RenderItems(items));
        layout.Children.Add(sidebar);
        _card.Child = layout;
    }

    private void SelectDay(DateTime date)
    {
        _day = date.Date;
        _month = new DateTime(date.Year, date.Month, 1);
        Render();
    }

    private void ShowNotes()
    {
        string file = $"notes/{_day:yyyy-MM-dd}.bin";
        string note;
        try { note = LocalDataStore.ReadProtected<string>(file) ?? ""; }
        catch (Exception) { _status.Text = "无法读取笔记"; return; }
        var editor = new TextBox { Text = note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxLength = 50000, Padding = new Thickness(12), FontSize = 15,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.SetResourceReference(Control.BackgroundProperty, IslandTheme.Surface);
        editor.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
        editor.SetResourceReference(Control.BorderBrushProperty, IslandTheme.Edge);
        editor.TextChanged += (_, _) =>
        {
            try { LocalDataStore.WriteProtected(file, editor.Text); _status.Text = ""; }
            catch (Exception) { _status.Text = "笔记保存失败"; }
        };
        _card.Child = editor;
        _leftActions.Visibility = Visibility.Collapsed;
        TrackEditor(editor);
        SetEditing(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => editor.Focus()));
    }

    private static bool TryHttpUrl(string? value, out Uri target) =>
        Uri.TryCreate(value, UriKind.Absolute, out target!) &&
        (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps);

    private void OpenUrl(Uri target)
    {
        try { Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { _status.Text = "无法打开链接"; }
    }

    private bool Replace(TodoItem item) => Commit(_store.Items.Any(row => row.Id == item.Id)
        ? _store.Items.Select(row => row.Id == item.Id ? item : row).ToArray() : _store.Items.Append(item).ToArray());

    private bool Commit(TodoItem[] items)
    {
        if (!_store.Save(items))
        {
            _status.Text = "无法保存待办";
            return false;
        }
        if (!IsEditing) Render();
        ItemsChanged?.Invoke();
        return true;
    }

    private void SetEditing(bool editing)
    {
        if (IsEditing == editing) return;
        bool heldKeyboard = HoldsKeyboard;
        IsEditing = editing;
        _editorHasFocus = editing;
        if (!editing)
        {
            if (_editorSurface?.IsKeyboardFocusWithin == true) Keyboard.ClearFocus();
            _editorSurface = null;
            _resetQuickAdd?.Invoke();
            _resetQuickAdd = null;
            _reminderOptions.Content = null;
            _leftActions.Visibility = Visibility.Visible;
        }
        if (editing || heldKeyboard) EditingChanged?.Invoke(editing);
    }
}
