using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Island.Core.Events;

namespace Island.App.Providers;

internal static class ClipboardFormats
{
    private const int MaxText = 1_000_000;
    private const int MaxImage = 24 * 1024 * 1024;
    private const int MaxPixels = 45_000_000;
    internal sealed record Payload(string Text = "", string? Html = null, string? Rtf = null, string? Csv = null,
        string[]? Files = null, byte[]? Png = null, int Width = 0, int Height = 0)
    {
        public ClipboardKind Kind => Files is { Length: > 0 } ? ClipboardKind.Files
            : Png is { Length: > 0 } ? ClipboardKind.Image : ClipboardKind.Text;
    }

    internal sealed record Capture(Payload Data, BitmapSource? Bitmap);

    public static Capture? Read(IDataObject? data)
    {
        if (data is null || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)
            || data.GetDataPresent("Clipboard Viewer Ignore", false)
            || (data.GetDataPresent("CanIncludeInClipboardHistory", false)
                && !ClipboardProvider.CanIncludeInHistory(data.GetData("CanIncludeInClipboardHistory", false))))
            return null;

        string? ReadText(string format, bool convert = false) =>
            data.GetDataPresent(format, convert) && data.GetData(format, convert) is string { Length: > 0 and <= MaxText } value ? value : null;
        var text = ReadText(DataFormats.UnicodeText, true) ?? "";
        var html = ReadText(DataFormats.Html);
        var rtf = ReadText(DataFormats.Rtf);
        var csv = ReadText(DataFormats.CommaSeparatedValue);
        var files = data.GetDataPresent(DataFormats.FileDrop, false) && data.GetData(DataFormats.FileDrop, false) is string[] paths
            ? paths.Where(Path.IsPathFullyQualified).Take(1024).ToArray() : null;
        BitmapSource? bitmap = null;
        byte[]? png = null;
        if (files is not { Length: > 0 })
        {
            foreach (string format in new[] { "PNG", "image/png" })
            {
                if (!data.GetDataPresent(format, false)) continue;
                png = data.GetData(format, false) switch
                {
                    MemoryStream stream when stream.Length <= MaxImage => stream.ToArray(),
                    byte[] bytes when bytes.Length <= MaxImage => bytes.ToArray(),
                    _ => null
                };
                if (png is not null && TryPngSize(png, out _, out _)) break;
                png = null;
            }
            if (png is null && data.GetDataPresent(DataFormats.Bitmap, true)
                && data.GetData(DataFormats.Bitmap, true) is BitmapSource source
                && (long)source.PixelWidth * source.PixelHeight <= MaxPixels)
            {
                // 快照在 STA 上冻结，压缩交给历史记录线程。
                bitmap = source.CloneCurrentValue();
                bitmap.Freeze();
            }
            if (png is null && bitmap is null)
            {
                foreach (var format in new[] { "DeviceIndependentBitmapV5", DataFormats.Dib })
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    var dib = data.GetData(format, false) switch
                    {
                        MemoryStream stream when stream.Length <= MaxImage => stream.ToArray(),
                        byte[] bytes when bytes.Length <= MaxImage => bytes,
                        _ => null
                    };
                    if (dib is not null && ReadDib(dib) is { } decoded)
                    {
                        bitmap = decoded;
                        break;
                    }
                }
            }
        }
        var payload = new Payload(text, html, rtf, csv, files, png);
        return text.Length > 0 || html is not null || rtf is not null || csv is not null
            || files is { Length: > 0 } || png is not null || bitmap is not null ? new Capture(payload, bitmap) : null;
    }

    public static Payload? Finish(Capture? captured)
    {
        if (captured is null) return null;
        var payload = captured.Data;
        var bitmap = captured.Bitmap;
        if (payload.Png is { } encoded)
        {
            // 校验 PNG 文件头和尺寸，避免把未知格式交给图片解码器。
            if (TryPngSize(encoded, out int width, out int height))
                return payload with { Width = width, Height = height };
            payload = payload with { Png = null };
        }
        if (bitmap is not null)
        {
            using var buffer = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(buffer);
            if (buffer.Length <= MaxImage)
                return payload with { Png = buffer.ToArray(), Width = bitmap.PixelWidth, Height = bitmap.PixelHeight };
        }
        return payload.Kind != ClipboardKind.Text || payload.Text.Length > 0 || payload.Html is not null
            || payload.Rtf is not null || payload.Csv is not null ? payload : null;
    }

    public static DataObject Create(Payload item)
    {
        var data = new DataObject();
        if (item.Text.Length > 0) data.SetData(DataFormats.UnicodeText, item.Text);
        if (item.Html is not null) data.SetData(DataFormats.Html, item.Html, false);
        if (item.Rtf is not null) data.SetData(DataFormats.Rtf, item.Rtf, false);
        if (item.Csv is not null) data.SetData(DataFormats.CommaSeparatedValue, item.Csv, false);
        if (item.Files is { Length: > 0 }) data.SetData(DataFormats.FileDrop, item.Files, true);
        if (item.Png is { } png)
        {
            data.SetData("PNG", new MemoryStream(png, writable: false), false);
            using var input = new MemoryStream(png, writable: false);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            data.SetImage(frame);
        }
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]), false);
        return data;
    }

    private static bool TryPngSize(byte[] png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return width > 0 && height > 0 && (long)width * height <= MaxPixels;
    }

    private static BitmapSource? ReadDib(byte[] bytes)
    {
        if (bytes.Length < 40) return null;
        int size = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        long height = Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        uint palette = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32));
        if (size is not (40 or 52 or 56 or 108 or 124) || width <= 0 || height <= 0 || width * height > MaxPixels
            || bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6)) return null;
        if (palette == 0 && bits <= 8) palette = 1u << bits;
        long offset = size + palette * 4L + (size == 40 && compression != 0 ? (compression == 3 ? 12 : 16) : 0);
        if (offset >= bytes.Length) return null;
        using var bmp = new MemoryStream(bytes.Length + 14);
        using var writer = new BinaryWriter(bmp);
        writer.Write((ushort)0x4D42);
        writer.Write(bytes.Length + 14);
        writer.Write(0);
        writer.Write((int)offset + 14);
        writer.Write(bytes);
        bmp.Position = 0;
        try
        {
            var frame = BitmapFrame.Create(bmp, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
