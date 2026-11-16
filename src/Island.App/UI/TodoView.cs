using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Island.App.Persistence;
using Island.Core.Events;

namespace Island.App.UI;

internal sealed class TodoView : Grid
{
    private sealed record DayChoice(DateTime Day, string Caption);
    private readonly TodoStore _store;
    private readonly HashSet<Guid> _remindedWithoutSave = [];
    private readonly TextBlock _counter = ExpandedTabs.Label("0/0", 35);
    private readonly TextBlock _status = ExpandedTabs.Label("", 11, IslandTheme.Muted);
    private readonly Border _card = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(12), MinHeight = 151 };
    private readonly Button _edit = ExpandedTabs.MakeButton("✎ 改一下");
    private Guid? _selected;
    private DateTime _today = DateTime.Today;
    public event Action<bool>? EditingChanged;
    public bool IsEditing { get; private set; }

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
        left.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 17, 0, 0), Children = { add, _edit } });
        _status.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_status);
        Children.Add(left);
        _card.SetResourceReference(Border.BackgroundProperty, IslandTheme.Tile);
        _card.VerticalAlignment = VerticalAlignment.Top;
        SetColumn(_card, 1);
        Children.Add(_card);
        Render();
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
                var check = ExpandedTabs.MakeButton(done ? "✓" : "○");
                check.ToolTip = done ? "取消完成" : "标记完成";
                check.SetResourceReference(Control.ForegroundProperty, done ? "AccountGreen" : IslandTheme.Muted);
                check.Click += (_, _) => Replace(item with { CompletedAt = done ? null : DateTimeOffset.Now });
                DockPanel.SetDock(check, Dock.Left);
                var remove = ExpandedTabs.MakeButton("×");
                remove.ToolTip = "删除待办";
                remove.Click += (_, _) => Commit(_store.Items.Where(row => row.Id != item.Id).ToArray());
                DockPanel.SetDock(remove, Dock.Right);
                var clock = ExpandedTabs.MakeButton("◷");
                clock.ToolTip = item.Reminder is { } at ? at.LocalDateTime.ToString("MM-dd HH:mm") : "设置提醒";
                clock.Click += (_, _) => OpenEditor(item, reminder: true);
                DockPanel.SetDock(clock, Dock.Right);
                var body = ExpandedTabs.MakeButton("");
                body.HorizontalContentAlignment = HorizontalAlignment.Left;
                var label = ExpandedTabs.Label(item.Title, 13, done ? IslandTheme.Muted : IslandTheme.Ink);
                if (done) label.TextDecorations = TextDecorations.Strikethrough;
                body.Content = label;
                body.ToolTip = item.Title;
                body.Click += (_, _) => { _selected = item.Id; Render(); };
                if (_selected == item.Id) body.SetResourceReference(Control.BackgroundProperty, IslandTheme.Edge);
                rows.Children.Add(new DockPanel { Margin = new Thickness(0, 0, 0, 5), Children = { check, remove, clock, body } });
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
        var options = Enumerable.Range(0, 8).Select(days => DateTime.Today.AddDays(days)).Append(when.Date).Distinct()
            .Order().Select(day => new DayChoice(day, day == DateTime.Today ? "今天" :
                day == DateTime.Today.AddDays(1) ? "明天" : day.ToString("MM-dd"))).ToArray();
        var date = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(DayChoice.Caption), MinWidth = 75,
            Margin = new Thickness(0, 0, 7, 0), SelectedItem = options.First(option => option.Day == when.Date) };
        var hours = ClockPart(24, when.Hour);
        var minutes = ClockPart(60, when.Minute);
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 4),
            Children = { date, hours, ExpandedTabs.Label(" : ", 13), minutes } };
        var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        var repeat = new CheckBox[7];
        string[] week = ["一", "二", "三", "四", "五", "六", "日"];
        for (int index = 0; index < week.Length; index++)
        {
            int day = (index + 1) % 7;
            var box = new CheckBox { Content = week[index], IsChecked = ((item?.Weekly ?? 0) & (1 << day)) != 0,
                Margin = new Thickness(0, 0, 6, 0) };
            box.SetResourceReference(Control.ForegroundProperty, IslandTheme.Ink);
            repeat[day] = box;
            days.Children.Add(box);
        }
        var addReminder = ExpandedTabs.MakeButton("设置提醒");
        var noReminder = ExpandedTabs.MakeButton("暂不提醒");
        var cancel = ExpandedTabs.MakeButton("取消");
        var save = ExpandedTabs.MakeButton("完成");
        save.SetResourceReference(Control.ForegroundProperty, "AccountGreen");
        bool scheduled = reminder || item?.Reminder is not null;
        void ShowReminder(bool visible)
        {
            scheduled = visible;
            time.Visibility = days.Visibility = noReminder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            addReminder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }
        addReminder.Click += (_, _) => ShowReminder(true);
        noReminder.Click += (_, _) => ShowReminder(false);
        cancel.Click += (_, _) => CancelEditing();
        editor.Children.Add(time);
        editor.Children.Add(days);
        editor.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0),
            Children = { addReminder, noReminder, cancel, save } });
        ShowReminder(scheduled);
        void Save()
        {
            string title = text.Text.Trim();
            if (title.Length == 0) { _status.Text = "请输入待办内容"; return; }
            int mask = 0;
            DateTimeOffset? alert = null;
            if (scheduled && date.SelectedItem is DayChoice choice && hours.SelectedIndex >= 0 && minutes.SelectedIndex >= 0)
            {
                var at = choice.Day.AddHours(hours.SelectedIndex).AddMinutes(minutes.SelectedIndex);
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

    private static ComboBox ClockPart(int count, int current) => new()
    {
        MinWidth = 48, ItemsSource = Enumerable.Range(0, count).Select(value => value.ToString("00", CultureInfo.InvariantCulture)).ToArray(),
        SelectedIndex = current
    };

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
        return true;
    }

    private void SetEditing(bool editing)
    {
        if (IsEditing == editing) return;
        IsEditing = editing;
        EditingChanged?.Invoke(editing);
    }
}
