using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Island.App.Persistence;

internal sealed record TodoItem(Guid Id, string Title, DateTimeOffset? Reminder, int Weekly,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? RemindedAt = null, string? Url = null,
    DateTimeOffset? StartedAt = null, DateTimeOffset? EndedAt = null, DateOnly? PlannedDay = null)
{
    public int? DurationMinutes => StartedAt is { } started && EndedAt is { } ended
        ? Math.Max(0, (int)Math.Round((ended - started).TotalMinutes, MidpointRounding.AwayFromZero))
        : null;

    public bool IsDone(DateTimeOffset now) => CompletedAt is { } completed
        && (Weekly == 0 || completed.LocalDateTime.Date == now.LocalDateTime.Date);

    public bool VisibleToday(DateTimeOffset now) => Weekly == 0 || (Weekly & (1 << (int)now.LocalDateTime.DayOfWeek)) != 0;

    public bool IsDue(DateTimeOffset now)
    {
        return ReminderToday(now) is { } due && due <= now &&
            (Weekly == 0 ? RemindedAt is null : RemindedAt?.LocalDateTime.Date != now.LocalDateTime.Date);
    }

    public DateTimeOffset? ReminderToday(DateTimeOffset now)
    {
        if (Reminder is not { } reminder || IsDone(now) || !VisibleToday(now)) return null;
        var local = reminder.LocalDateTime;
        if (Weekly == 0) return reminder;
        var today = now.LocalDateTime.Date;
        return local.Date <= today ? new DateTimeOffset(today + local.TimeOfDay) : null;
    }
}

internal sealed record TodoPreview(string Text, string Description, bool Overdue);

internal sealed class TodoStore
{
    private readonly string _file;
    public IReadOnlyList<TodoItem> Items { get; private set; } = [];
    public string? ReadError { get; }

    public TodoStore(string file = "todo.bin")
    {
        _file = file;
        try { Items = LocalDataStore.ReadProtected<List<TodoItem>>(_file) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            ReadError = "无法读取待办，请检查本机记录";
        }
    }

    public bool Save(IReadOnlyList<TodoItem> items)
    {
        if (ReadError is not null) return false;
        try
        {
            LocalDataStore.WriteProtected(_file, items);
            Items = items;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    public TodoPreview? GetPreview(DateTimeOffset now)
    {
        var pending = Items.Where(item => item.VisibleToday(now) && !item.IsDone(now)).ToArray();
        if (pending.Length == 0) return null;
        var next = pending.Select(item => (Item: item, Due: item.ReminderToday(now)))
            .Where(item => item.Due.HasValue).OrderBy(item => item.Due).FirstOrDefault();
        if (next.Due is not { } due)
            return new TodoPreview($"待办 {pending.Length} 项", string.Join(Environment.NewLine, pending.Select(item => item.Title)), false);
        bool overdue = due <= now;
        var localDue = due.LocalDateTime;
        var time = localDue.ToString(localDue.Date == now.LocalDateTime.Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
        var prefix = overdue ? "待办到期" : $"待办 {time}";
        var extra = pending.Length > 1 ? $" +{pending.Length - 1}" : "";
        return new TodoPreview($"{prefix} · {next.Item.Title}{extra}",
            $"{time} · {next.Item.Title}{Environment.NewLine}共 {pending.Length} 项未完成", overdue);
    }
}
