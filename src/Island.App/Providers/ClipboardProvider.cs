using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Island.App.Persistence;
using Island.Core.Events;
using Island.Core.Providers;

namespace Island.App.Providers;

internal sealed partial class ClipboardProvider(HwndSource window) : IIslandProvider
{
    public const string SourceId = "clipboard";
    private const string FileName = "clipboard-history.bin";
    private const int WmClipboardUpdate = 0x031D;
    private const int RecentLimit = 30;
    private const long RecentByteLimit = 96L * 1024 * 1024;
    private enum Operation { Observe, Copy, TogglePin, Clear }
    private sealed record Command(Operation Operation, Guid EntryId = default);
    internal sealed record Entry(Guid Id, string Text, bool Pinned, ClipboardFormats.Payload? Content = null,
        string? Fingerprint = null, DateTimeOffset? AddedAt = null)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public ClipboardFormats.Payload Payload => Content ?? new ClipboardFormats.Payload(Text);
    }
    public string Id => SourceId;

    public async Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken)
    {
        List<Entry> entries;
        string? warning = null;
        try
        {
            entries = LocalDataStore.ReadProtected<List<Entry>>(FileName) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            entries = [];
            warning = "无法读取已保存的历史";
        }

        var commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
        int pendingObservation = 0;
        IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmClipboardUpdate && Interlocked.Exchange(ref pendingObservation, 1) == 0)
            {
                commands.Writer.TryWrite(new Command(Operation.Observe));
            }
            return IntPtr.Zero;
        }
        await window.Dispatcher.InvokeAsync(() =>
        {
            if (!AddClipboardFormatListener(window.Handle))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            window.AddHook(OnMessage);
        }, DispatcherPriority.Normal, cancellationToken);
        try
        {
            async ValueTask ReportAsync()
            {
                IslandAction Action(string title, Operation operation, Guid id = default) =>
                    new(title, token =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return commands.Writer.WriteAsync(new Command(operation, id), token);
                    });
                var previews = entries.OrderByDescending(entry => entry.Pinned)
                    .Select(entry => new ClipboardPreview(Describe(entry.Payload), entry.Pinned,
                        Action("复制", Operation.Copy, entry.Id),
                        Action(entry.Pinned ? "取消固定" : "固定", Operation.TogglePin, entry.Id),
                        entry.Payload.Kind, entry.Payload.Png ?? [], entry.Id, entry.AddedAt))
                    .ToArray();
                await publisher.PublishAsync(new IslandEvent(Id, IslandEventType.Clipboard, warning ?? "剪贴板",
                    clipboard: new ClipboardSnapshot(previews, Action("清空历史和系统剪贴板", Operation.Clear))),
                    cancellationToken);
            }

            await ReportAsync();
            await foreach (var command in commands.Reader.ReadAllAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryIndex = command.Operation is Operation.Copy or Operation.TogglePin
                    ? entries.FindIndex(entry => entry.Id == command.EntryId) : -1;
                bool changed = false;
                try
                {
                    switch (command.Operation)
                    {
                        case Operation.Observe:
                            Interlocked.Exchange(ref pendingObservation, 0);
                            var captured = await OnClipboardAsync(() => ClipboardFormats.Read(Clipboard.GetDataObject()), cancellationToken);
                            if (ClipboardFormats.Finish(captured) is { } content)
                            {
                                changed = Remember(entries, content);
                            }
                            break;
                        case Operation.Copy when entryIndex >= 0:
                            var copy = entries[entryIndex].Payload;
                            await OnClipboardAsync(() => { Clipboard.SetDataObject(ClipboardFormats.Create(copy), copy: true); return true; }, cancellationToken);
                            changed = Remember(entries, copy);
                            break;
                        case Operation.TogglePin when entryIndex >= 0:
                            entries[entryIndex] = entries[entryIndex] with { Pinned = !entries[entryIndex].Pinned };
                            changed = true;
                            break;
                        case Operation.Clear:
                            await OnClipboardAsync(() => { Clipboard.Clear(); return true; }, cancellationToken);
                            entries.Clear();
                            changed = true;
                            break;
                    }
                    if (changed)
                    {
                        Trim(entries);
                        LocalDataStore.WriteProtected(FileName, entries);
                        warning = null;
                        await ReportAsync();
                    }
                }
                catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException
                    or CryptographicException or NotSupportedException or FormatException or ArgumentException)
                {
                    warning = ex is ExternalException ? "剪贴板暂时被占用，请重试" : "历史保存失败";
                    Trace.TraceWarning("Clipboard operation failed ({0}).", ex.GetType().Name);
                    await ReportAsync();
                }
            }
        }
        finally
        {
            commands.Writer.TryComplete();
            if (!window.Dispatcher.HasShutdownStarted)
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    window.RemoveHook(OnMessage);
                    RemoveClipboardFormatListener(window.Handle);
                });
            }
        }
    }

    internal static bool CanIncludeInHistory(object? flag)
    {
        Span<byte> buffer = stackalloc byte[4];
        return flag switch
        {
            int value => value == 1,
            uint value => value == 1,
            byte[] bytes when bytes.Length >= 4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 1,
            MemoryStream stream when stream.Read(buffer) == 4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 1,
            _ => false
        };
    }

    private async Task<T> OnClipboardAsync<T>(Func<T> operation, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await window.Dispatcher.InvokeAsync(operation, DispatcherPriority.Background, token);
            }
            catch (ExternalException) when (attempt < 3)
            {
                await Task.Delay(45, token);
            }
        }
    }

    internal static bool Remember(List<Entry> entries, ClipboardFormats.Payload payload)
    {
        string hash = Hash(payload);
        int index = entries.FindIndex(entry => (entry.Fingerprint ?? Hash(entry.Payload)) == hash);
        if (index == 0)
        {
            return false;
        }
        var entry = index < 0 ? new Entry(Guid.NewGuid(), "", false, payload, hash, DateTimeOffset.UtcNow)
            : entries[index] with { AddedAt = DateTimeOffset.UtcNow };
        if (index > 0)
        {
            entries.RemoveAt(index);
        }
        entries.Insert(0, entry);
        return true;
    }

    internal static void Trim(List<Entry> entries)
    {
        int recent = 0;
        long bytes = 0;
        entries.RemoveAll(entry =>
        {
            recent++;
            if (entry.Pinned) return false;
            var data = entry.Payload;
            bytes += (data.Png?.LongLength ?? 0) + 2L * (data.Text.Length + (data.Rtf?.Length ?? 0)
                + (data.Html?.Length ?? 0) + (data.Csv?.Length ?? 0) + (data.Files?.Sum(path => path.Length) ?? 0));
            return recent > RecentLimit || bytes > RecentByteLimit;
        });
    }

    private static string Hash(ClipboardFormats.Payload data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(data.Text);
        Add(data.Html);
        Add(data.Rtf);
        Add(data.Csv);
        Add(data.Files is null ? null : string.Join("\0", data.Files));
        if (data.Png is not null) hash.AppendData(data.Png);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Describe(ClipboardFormats.Payload data) => data.Kind switch
    {
        ClipboardKind.Image => $"图片 {data.Width}×{data.Height}",
        ClipboardKind.Files when data.Files is { Length: 1 } files => $"文件 · {Preview(Path.GetFileName(files[0]))}",
        ClipboardKind.Files => $"{data.Files!.Length} 个文件或文件夹",
        _ when data.Text.Length > 0 => Preview(data.Text),
        _ => "富文本"
    };

    internal static string Preview(string text)
    {
        if (Sensitive().IsMatch(text))
        {
            return "••••••  敏感内容";
        }
        var preview = text.Length > 135 ? text[..135] + "…" : text;
        return preview.ReplaceLineEndings(" ").Trim();
    }

    [GeneratedRegex(@"(?:^|\D)\d{17}[\dXx](?:$|\D)|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(?:sk[-_]|gh[pousr]_|AKIA|AIza|xox[baprs]-)[A-Za-z0-9_\-]{12,}|(?:api[_ -]?key|secret|access[_ -]?token|authorization)\s*[:=]\s*['""]?(?:Bearer\s+)?\S{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Sensitive();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
