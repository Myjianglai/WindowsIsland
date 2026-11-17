using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ClosedXML.Excel;

namespace Island.App.Persistence;

internal static class TodoSpreadsheet
{
    private static readonly string[] Headers = ["标题", "提醒时间", "每周重复", "完成时间", "ID", "已提醒时间"];
    private static readonly string[] Days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public static void Export(string path, IReadOnlyList<TodoItem> items)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("待办");
        for (int i = 0; i < Headers.Length; i++)
            sheet.Cell(1, i + 1).Value = Headers[i];
        var header = sheet.Range(1, 1, 1, Headers.Length);
        header.Style.Font.SetBold();
        header.Style.Fill.SetBackgroundColor(XLColor.LightBlue);
        sheet.SheetView.FreezeRows(1);
        sheet.Column(1).Width = 38;
        sheet.Columns(2, Headers.Length).Width = 34;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int row = i + 2;
            sheet.Cell(row, 1).Value = item.Title;
            sheet.Cell(row, 2).Value = item.Reminder?.ToString("o") ?? "";
            sheet.Cell(row, 3).Value = string.Join("、", Days.Where((day, index) => (item.Weekly & (1 << index)) != 0));
            sheet.Cell(row, 4).Value = item.CompletedAt?.ToString("o") ?? "";
            sheet.Cell(row, 5).Value = item.Id.ToString();
            sheet.Cell(row, 6).Value = item.RemindedAt?.ToString("o") ?? "";
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".xlsx";
        try
        {
            book.SaveAs(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public static TodoItem[] Import(string path)
    {
        using var input = File.OpenRead(path);
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 1000 || zip.Entries.Sum(entry => entry.Length) > 64 * 1024 * 1024)
                throw new InvalidDataException("Excel 文件超出允许的大小。");
        }
        input.Position = 0;
        using var book = new XLWorkbook(input);
        var sheet = book.Worksheets.FirstOrDefault() ?? throw new InvalidDataException("文件没有工作表。");
        var columns = sheet.Row(1).CellsUsed()
            .Where(cell => !cell.HasFormula && Headers.Contains(cell.GetString().Trim()))
            .GroupBy(cell => cell.GetString().Trim()).ToDictionary(group => group.Key, group => group.First().Address.ColumnNumber);
        if (!columns.ContainsKey(Headers[0]))
            throw new InvalidDataException("第一行需要有「标题」列。可先导出一份模板。");
        var items = new List<TodoItem>();
        foreach (var row in sheet.RowsUsed().Where(row => row.RowNumber() > 1))
        {
            if (items.Count >= 10000) throw new InvalidDataException("一次最多导入 10000 项。");
            IXLCell? Cell(int index)
            {
                if (!columns.TryGetValue(Headers[index], out int col)) return null;
                var cell = row.Cell(col);
                if (cell.HasFormula) throw new InvalidDataException($"第 {row.RowNumber()} 行的待办字段需要填写实际值。");
                return cell;
            }
            var title = Cell(0)?.GetString().Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var days = (Cell(2)?.GetString() ?? "").Split(['、', ',', '，', ' '],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int weekly = 0;
            foreach (var day in days)
            {
                int index = Array.IndexOf(Days, day);
                if (index < 0) throw new InvalidDataException($"第 {row.RowNumber()} 行的重复日应填写周一、周二等。");
                weekly |= 1 << index;
            }
            var reminder = Date(Cell(1), row.RowNumber());
            if (weekly != 0 && reminder is null)
                throw new InvalidDataException($"第 {row.RowNumber()} 行设置了重复日，请填写提醒时间。");
            var completed = Date(Cell(3), row.RowNumber());
            var id = Guid.TryParse(Cell(4)?.GetString(), out var value) && value != Guid.Empty ? value : Guid.NewGuid();
            items.Add(new TodoItem(id, title, reminder, weekly, completed, Date(Cell(5), row.RowNumber())));
        }
        return items.ToArray();
    }

    public static TodoItem[] Merge(IReadOnlyList<TodoItem> existing, IReadOnlyList<TodoItem> imported)
    {
        var ids = existing.Select(item => item.Id).ToHashSet();
        var tasks = existing.Select(Identity).ToHashSet();
        return existing.Concat(imported.Where(item => ids.Add(item.Id) && tasks.Add(Identity(item)))).ToArray();
    }

    private static (string Title, DateTimeOffset? Reminder, int Weekly, DateTimeOffset? Completed) Identity(TodoItem item) =>
        (item.Title, item.Reminder, item.Weekly, item.CompletedAt);

    private static DateTimeOffset? Date(IXLCell? cell, int row)
    {
        if (cell is null || cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime)
        {
            var date = cell.GetDateTime();
            if (!TimeZoneInfo.Local.IsInvalidTime(date)) return new DateTimeOffset(date);
        }
        else if (DateTimeOffset.TryParse(cell.GetString(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
            return time;
        throw new InvalidDataException($"第 {row} 行的时间格式无效，请使用日期和时间。");
    }
}
