using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Border _card = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), MinHeight = 151 };
    private readonly StackPanel _leftActions = new();
    private readonly ContentControl _reminderOptions = new();
    private readonly Button _edit = ExpandedTabs.MakeButton("✎ 改一下");
    private Guid? _selected;
    private DateTime _today = DateTime.Today;
    public event Action<bool>? EditingChanged;
    public event Action? ItemsChanged;
    public bool IsEditing { get; private set; }
    public TodoPreview? GetPreview(DateTimeOffset now) => _store.GetPreview(now);

    public TodoView(TodoStore? store = null)
    {
        _store = store ?? new TodoStore();
        Margin = new Thickness(8, 5, 5, 7);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = ExpandedTabs.Label("待办", 19);
        title.FontWeight = FontWeights.SemiBold;
        var list = ExpandedTabs.Label("清单", 19, "AccountGreen");
        list.FontWeight = FontWeights.SemiBold;
        header.Children.Add(title);
        header.Children.Add(list);
        left.Children.Add(header);
        _counter.Margin = new Thickness(0, 9, 0, 0);
        _counter.FontWeight = FontWeights.Bold;
        left.Children.Add(_counter);
        left.Children.Add(ExpandedTabs.Label("已完成", 12, IslandTheme.Muted));
        var add = ExpandedTabs.MakeButton("⊕ 加一个");
        add.Click += (_, _) => OpenEditor(null, reminder: false);
        _edit.Click += (_, _) => { if (Selected() is { } item) OpenEditor(item, reminder: false); };
        _leftActions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 17, 0, 0), Children = { add, _edit } });
        var import = ExpandedTabs.MakeButton("导入 Excel");
        var export = ExpandedTabs.MakeButton("导出 Excel");
        import.Click += async (_, _) => await TransferExcelAsync(import: true);
        export.Click += async (_, _) => await TransferExcelAsync(import: false);
        _leftActions.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0),
            Children = { import, export } });
        left.Children.Add(_leftActions);
        left.Children.Add(_reminderOptions);
        _status.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_status);
        Children.Add(left);
        _card.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        _card.VerticalAlignment = VerticalAlignment.Top;
        SetColumn(_card, 1);
        Children.Add(_card);
        Render();
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
                var dialog = new OpenFileDialog { Title = "导入待办", Filter = filter, CheckFileExists = true };
                if (dialog.ShowDialog(owner) != true) return;
                var imported = await Task.Run(() => TodoSpreadsheet.Import(dialog.FileName));
                var merged = TodoSpreadsheet.Merge(_store.Items, imported);
                int added = merged.Length - _store.Items.Count;
                if (added == 0 || Commit(merged))
                    _status.Text = $"导入 {added} 项，跳过 {imported.Length - added} 项";
            }
            else
            {
                var dialog = new SaveFileDialog { Title = "导出待办", Filter = filter, DefaultExt = ".xlsx",
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
            SetEditing(false);
            Render();
        }
    }

    public void CheckReminders(DateTimeOffset now, Func<IslandEvent, bool> publish)
    {
        if (now.LocalDateTime.Date != _today && !IsEditing)
        {
            _today = now.LocalDateTime.Date;
            Render();
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

    private TodoItem? Selected() => _store.Items.FirstOrDefault(item => item.Id == _selected);

    private void Render()
    {
        var now = DateTimeOffset.Now;
        var items = _store.Items.Where(item => item.VisibleToday(now)).ToArray();
        _counter.Text = $"{items.Count(item => item.IsDone(now))}/{items.Length}";
        if (items.Length == 0)
        {
            _card.Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "☷", FontSize = 29, HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 5) },
                    ExpandedTabs.Label("今天还没有安排", 14, IslandTheme.Muted)
                }
            };
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
                var remove = ExpandedTabs.MakeFlatButton("×");
                remove.ToolTip = "删除待办";
                remove.Click += (_, _) => Commit(_store.Items.Where(row => row.Id != item.Id).ToArray());
                DockPanel.SetDock(remove, Dock.Right);
                var clock = ExpandedTabs.MakeFlatButton("◷");
                clock.ToolTip = item.Reminder is { } at ? at.LocalDateTime.ToString("MM-dd HH:mm") : "设置提醒";
                clock.Click += (_, _) => OpenEditor(item, reminder: true);
                DockPanel.SetDock(clock, Dock.Right);
                var body = ExpandedTabs.MakeFlatButton("");
                body.HorizontalContentAlignment = HorizontalAlignment.Left;
                var label = ExpandedTabs.Label(item.Title, 13, done ? IslandTheme.Muted : IslandTheme.Ink);
                if (done) label.TextDecorations = TextDecorations.Strikethrough;
                body.Content = label;
                body.ToolTip = item.Title;
                body.Click += (_, _) => { _selected = item.Id; _edit.IsEnabled = true; };
                var row = new Border { Margin = new Thickness(0, 0, 0, 3),
                    Child = new DockPanel { Children = { check, remove, clock, body } } };
                row.SetResourceReference(FrameworkElement.StyleProperty, "TodoListRow");
                rows.Children.Add(row);
            }
            _card.Child = new ScrollViewer { Content = rows, MaxHeight = 155, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        if (Selected() is null || !items.Any(item => item.Id == _selected)) _selected = items.FirstOrDefault()?.Id;
        _edit.IsEnabled = _selected is not null;
        _status.Text = _store.ReadError ?? "";
    }

    public void CancelEditing()
    {
        if (!IsEditing) return;
        SetEditing(false);
        Render();
    }

    private void OpenEditor(TodoItem? item, bool reminder)
    {
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
        var when = item?.Reminder?.LocalDateTime ?? DateTime.Now.AddMinutes(15);
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
                item is not null && item.Reminder == alert && item.Weekly == mask ? item.RemindedAt : null);
            if (Replace(row))
            {
                _selected = row.Id;
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
        SetEditing(true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsEditing && ReferenceEquals(_card.Child, editor)) { text.Focus(); text.CaretIndex = text.Text.Length; }
        }));
    }

    private static string ClockCaption(int value) => value.ToString("00", CultureInfo.InvariantCulture);

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
        IsEditing = editing;
        if (!editing)
        {
            _reminderOptions.Content = null;
            _leftActions.Visibility = Visibility.Visible;
        }
        EditingChanged?.Invoke(editing);
    }
}
