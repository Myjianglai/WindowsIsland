using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace Island.App.Persistence;

internal sealed record TodoItem(Guid Id, string Title, DateTimeOffset? Reminder, int Weekly,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? RemindedAt = null)
{
    public bool IsDone(DateTimeOffset now) => CompletedAt is { } completed
        && (Weekly == 0 || completed.LocalDateTime.Date == now.LocalDateTime.Date);

    public bool VisibleToday(DateTimeOffset now) => Weekly == 0 || (Weekly & (1 << (int)now.LocalDateTime.DayOfWeek)) != 0;

    public bool IsDue(DateTimeOffset now)
    {
        if (Reminder is not { } reminder || IsDone(now) || !VisibleToday(now)) return false;
        if (Weekly == 0) return RemindedAt is null && reminder <= now;
        if (RemindedAt?.LocalDateTime.Date == now.LocalDateTime.Date) return false;
        return reminder.LocalDateTime.Date <= now.LocalDateTime.Date
            && reminder.LocalDateTime.TimeOfDay <= now.LocalDateTime.TimeOfDay;
    }
}

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
}
