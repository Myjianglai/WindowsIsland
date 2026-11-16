using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;
using Island.Core.Providers;

namespace Island.App.Providers;

internal sealed class SystemMonitorProvider : IIslandProvider
{
    public const string SourceId = "system-monitor";
    public string Id => SourceId;

    public async Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken)
    {
        var time = Stopwatch.GetTimestamp();
        var cpu = ReadCpu();
        var net = ReadNetwork();
        var cores = ReadCores();
        var processes = ReadProcesses();
        double downloadTotal = 0, uploadTotal = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var elapsed = Stopwatch.GetElapsedTime(time).TotalSeconds;
            time = Stopwatch.GetTimestamp();
            var nextCpu = ReadCpu();
            double? cpuUsage = null;
            if (nextCpu is { } current && cpu is { } previous && current.Total > previous.Total && current.Idle >= previous.Idle)
            {
                cpuUsage = 100 * Math.Clamp(1 - (double)(current.Idle - previous.Idle) / (current.Total - previous.Total), 0, 1);
            }
            cpu = nextCpu;
            var mem = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            bool hasMemory = GlobalMemoryStatusEx(ref mem);
            uint? memory = hasMemory ? mem.Load : null;
            var nextCores = ReadCores();
            var perCore = nextCores.Length == cores.Length
                ? nextCores.Select((next, index) => Busy(cores[index], next)).ToArray() : [];
            cores = nextCores;
            var nextProcesses = ReadProcesses();
            var ranked = nextProcesses.Select(pair =>
            {
                var sample = pair.Value;
                double? usage = null;
                if (sample.Cpu is { } currentTime && processes.TryGetValue(pair.Key, out var previous)
                    && previous.Name == sample.Name && previous.Started == sample.Started && previous.Cpu is { } priorTime
                    && currentTime >= priorTime)
                    usage = Math.Clamp((currentTime - priorTime).TotalSeconds * 100 / (elapsed * Environment.ProcessorCount), 0, 100);
                return new ProcessMetric(sample.Name, usage, sample.Memory);
            }).ToArray();
            processes = nextProcesses;
            var nextNet = ReadNetwork();
            double received = 0, sent = 0;
            bool hasNetworkSample = false;
            foreach (var (id, counters) in nextNet)
            {
                if (net.TryGetValue(id, out var old))
                {
                    received += Math.Max(0, counters.Received - old.Received);
                    sent += Math.Max(0, counters.Sent - old.Sent);
                    hasNetworkSample = true;
                }
            }
            net = nextNet;
            downloadTotal += received;
            uploadTotal += sent;
            var (battery, charging) = DeviceBatteryProvider.ReadComputerBattery();
            publisher.TryPublish(new IslandEvent(Id, IslandEventType.SystemMetrics, "系统监控",
                metrics: new SystemMetrics(cpuUsage, memory, hasNetworkSample ? received / elapsed : null,
                    hasNetworkSample ? sent / elapsed : null, perCore, ranked,
                    hasMemory ? mem.TotalPhysical - Math.Min(mem.AvailablePhysical, mem.TotalPhysical) : null,
                    hasMemory ? mem.TotalPhysical : null, downloadTotal, uploadTotal, battery, charging)));
        }
    }

    private sealed record ProcessSample(string Name, long Memory, TimeSpan? Cpu, DateTime? Started);

    private static Dictionary<int, ProcessSample> ReadProcesses()
    {
        var data = new Dictionary<int, ProcessSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0) continue;
                    TimeSpan? cpu = null;
                    DateTime? started = null;
                    try { started = process.StartTime; cpu = process.TotalProcessorTime; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    data[process.Id] = new ProcessSample(process.ProcessName, process.WorkingSet64, cpu, started);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return data;
    }

    private static double Busy(CoreCounters previous, CoreCounters next)
    {
        long total = next.Kernel + next.User - previous.Kernel - previous.User;
        return total > 0 ? 100 * Math.Clamp(1 - (double)(next.Idle - previous.Idle) / total, 0, 1) : 0;
    }

    private static readonly CoreQuery? Query = LoadCoreQuery();

    private static CoreQuery? LoadCoreQuery()
    {
        return NativeLibrary.TryLoad("ntdll.dll", out var library) && NativeLibrary.TryGetExport(library, "NtQuerySystemInformation", out IntPtr entry)
            ? Marshal.GetDelegateForFunctionPointer<CoreQuery>(entry) : null;
    }

    private static CoreCounters[] ReadCores()
    {
        if (Query is null) return [];
        int size = Marshal.SizeOf<CoreCounters>();
        var rows = new CoreCounters[Math.Max(Environment.ProcessorCount, 256)];
        int result = Query(8, rows, rows.Length * size, out int length);
        return result >= 0 && length > 0 && length <= rows.Length * size ? rows[..(length / size)] : [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoreCounters
    {
        public long Idle, Kernel, User, Reserved1, Reserved2;
        public uint Reserved3;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CoreQuery(int kind, [Out] CoreCounters[] data, int bytes, out int written);

    private static (ulong Idle, ulong Total)? ReadCpu() =>
        GetSystemTimes(out ulong idle, out ulong kernel, out ulong user) ? (idle, kernel + user) : null;

    private static Dictionary<string, (long Received, long Sent)> ReadNetwork()
    {
        var results = new Dictionary<string, (long, long)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                try
                {
                    var stats = adapter.GetIPStatistics();
                    results[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return results;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
