using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;
using Microsoft.Win32.SafeHandles;

namespace Island.App.Providers;

internal static class LogitechBattery
{
    public static async Task<List<DeviceBattery>> ReadAsync(CancellationToken token)
    {
        var devices = new List<DeviceBattery>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in ReceiverPaths())
        {
            using var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var preparsed)) continue;
            Caps caps;
            try { if (HidP_GetCaps(preparsed, out caps) < 0) continue; }
            finally { HidD_FreePreparsedData(preparsed); }
            if (caps.UsagePage != 0xFF00 || caps.Usage != 2 || caps.Input < 20 || caps.Output < 20) continue;
            string kind = path.Contains("pid_c548", StringComparison.OrdinalIgnoreCase) ? "Logi Bolt 接收器" : "罗技 Unifying 接收器";
            if (!touched.Add(kind)) continue;
            using var io = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            var protocol = new Client(io, caps.Input, caps.Output);
            int countBefore = devices.Count;
            for (byte slot = 1; slot <= 6 && !token.IsCancellationRequested; slot++)
            {
                var page = await protocol.FeatureAsync(slot, 0x1004, token);
                byte level;
                if (page > 0 && await protocol.CallAsync(slot, page, 0, [], token) is { } capabilities
                    && (capabilities[5] & 2) != 0
                    && await protocol.CallAsync(slot, page, 1, [], token) is { } status)
                {
                    level = status[4];
                }
                else
                {
                    page = await protocol.FeatureAsync(slot, 0x1000, token);
                    if (page == 0 || await protocol.CallAsync(slot, page, 0, [], token) is not { } old) continue;
                    level = old[4];
                }
                if (level > 100) continue;
                var name = await protocol.NameAsync(slot, token) ?? $"罗技设备 {slot}";
                devices.Add(new DeviceBattery(name, $"{level}%", level <= 20, level, kind));
            }
            if (devices.Count == countBefore)
                devices.Add(new DeviceBattery(kind, "未知", false, Connection: "USB 接收器"));
        }
        return devices;
    }

    // HID++ 2.0 只发送特性查询、设备名称和电量读取请求。
    private sealed class Client(FileStream io, int receiveSize, int sendSize)
    {
        private byte _client = 1;

        public async Task<byte> FeatureAsync(byte slot, int feature, CancellationToken token)
        {
            var response = await CallAsync(slot, 0, 0, [(byte)(feature >> 8), (byte)feature], token);
            return response is null ? (byte)0 : response[4];
        }

        public async Task<string?> NameAsync(byte slot, CancellationToken token)
        {
            byte page = await FeatureAsync(slot, 5, token);
            if (page == 0 || await CallAsync(slot, page, 0, [], token) is not { } size || size[4] is 0 or > 64) return null;
            var name = new List<byte>();
            while (name.Count < size[4])
            {
                if (await CallAsync(slot, page, 1, [(byte)name.Count], token) is not { } chunk) break;
                int count = Math.Min(size[4] - name.Count, chunk[0] == 0x10 ? 3 : 16);
                name.AddRange(chunk.AsSpan(4, count).ToArray());
            }
            return name.Count == size[4] ? Encoding.UTF8.GetString(name.ToArray()).Trim('\0', ' ') : null;
        }

        public async Task<byte[]?> CallAsync(byte slot, byte feature, byte function, byte[] arguments, CancellationToken token)
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Bolt 中的休眠外设唤醒可能超过半秒。短超时会稳定漏掉已连接的设备。
            requestTimeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var request = new byte[sendSize];
            request[0] = 0x11;
            request[1] = slot;
            request[2] = feature;
            _client = (byte)(_client % 15 + 1);
            request[3] = (byte)((function << 4) | _client);
            arguments.CopyTo(request, 4);
            try
            {
                await io.WriteAsync(request, requestTimeout.Token);
                var reply = new byte[receiveSize];
                while (await io.ReadAsync(reply, requestTimeout.Token) is var length && length >= 7)
                {
                    if (reply[0] is not (0x10 or 0x11) || reply[1] != slot) continue;
                    if (reply[2] is 0xFF or 0x8F && reply[3] == feature && reply[4] == request[3]) return null;
                    if (reply[2] == feature && reply[3] == request[3]) return reply;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
            return null;
        }
    }

    private static IEnumerable<string> ReceiverPaths()
    {
        HidD_GetHidGuid(out Guid hid);
        var devices = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var item = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref hid, index, ref item)) break;
                SetupDiGetDeviceInterfaceDetail(devices, ref item, IntPtr.Zero, 0, out int bytes, IntPtr.Zero);
                if (bytes <= 8 || bytes > 16384) continue;
                var detail = Marshal.AllocHGlobal(bytes);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref item, detail, bytes, out _, IntPtr.Zero)) continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path is not null && (path.Contains("vid_046d&pid_c548", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("vid_046d&pid_c52b", StringComparison.OrdinalIgnoreCase)
                        || path.Contains("vid_046d&pid_c532", StringComparison.OrdinalIgnoreCase))) yield return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Class; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Caps { public ushort Usage, UsagePage, Input, Output; }
    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr window, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData item);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData item, IntPtr detail, int detailSize,
        out int required, IntPtr info);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, int share, IntPtr security, int mode, uint flags, IntPtr template);
}
