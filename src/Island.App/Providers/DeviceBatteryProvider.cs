using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Island.Core.Events;
using Island.Core.Providers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace Island.App.Providers;

internal sealed class DeviceBatteryProvider(Dispatcher dispatcher) : IIslandProvider
{
    public const string SourceId = "device-battery";
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string BatteryLife = "System.Devices.BatteryLife";
    private static readonly string[] BatteryProperties = [BatteryLife, BatteryProperty];
    public string Id => SourceId;

    public async Task RunAsync(IIslandEventPublisher publisher, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var devices = ReadControllers();
            try { devices.AddRange(await LogitechBattery.ReadAsync(cancellationToken)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scan.CancelAfter(TimeSpan.FromSeconds(22));
            try
            {
                var byName = await ReadDevicesAsync(scan.Token);
                devices.AddRange(byName);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
            await publisher.PublishAsync(new IslandEvent(Id, IslandEventType.DeviceBattery,
                devices.Count > 0 ? "设备电量" : "未发现可显示的已连接设备",
                devices: devices.OrderByDescending(device => device.Low).ThenByDescending(device => device.Percent.HasValue).ToArray()), cancellationToken);
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task<DeviceBattery[]> ReadDevicesAsync(CancellationToken token)
    {
        var names = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        var systemProperties = await ReadSystemPropertiesAsync(token);
        DeviceBattery? Known(DeviceInformation device)
        {
            var name = DisplayName(device.Name);
            if (BatteryValue(device) is { } percent)
                return new DeviceBattery(name, $"{percent}%", percent <= 20, percent, "蓝牙");
            return systemProperties.GetValueOrDefault(name);
        }
        try
        {
            var classic = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), BatteryProperties).AsTask(token);
            foreach (var device in classic)
            {
                if (device.Pairing.IsPaired && !string.IsNullOrWhiteSpace(device.Name))
                {
                    var name = DisplayName(device.Name);
                    names[name] = Known(device) ?? new DeviceBattery(name, "未知", false, Connection: "蓝牙");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
        try
        {
            var le = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), BatteryProperties).AsTask(token);
            // 只查询已连接的配对设备。每件设备给独立超时，避免一个设备拖住整次轮询。
            var queries = le.Where(device => device.Pairing.IsPaired).Select(device =>
                Known(device) is { } cached ? Task.FromResult(cached) : ReadBatteryAsync(device, token));
            foreach (var battery in await Task.WhenAll(queries))
            {
                if (battery.Percent.HasValue || !names.ContainsKey(battery.Name)) names[battery.Name] = battery;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
        return names.Values.OrderByDescending(item => item.Low).ThenBy(item => item.Name).ToArray();
    }

    private static async Task<Dictionary<string, DeviceBattery>> ReadSystemPropertiesAsync(CancellationToken token)
    {
        var results = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 蓝牙经典设备的读数经常挂在 Hands-Free 音频节点上，而不是配对端点上。
            var nodes = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                BatteryProperties, DeviceInformationKind.Device).AsTask(token);
            foreach (var node in nodes)
            {
                if (BatteryValue(node) is not { } value || string.IsNullOrWhiteSpace(node.Name)) continue;
                var name = DisplayName(node.Name);
                results[name] = new DeviceBattery(name, $"{value}%", value <= 20, value, "蓝牙");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or ArgumentException) { }
        return results;
    }

    internal static int? BatteryValue(DeviceInformation data)
    {
        foreach (string name in BatteryProperties)
        {
            if (!data.Properties.TryGetValue(name, out var raw)) continue;
            int value = raw switch { byte b => b, ushort u => u, uint u when u <= 100 => (int)u, int i => i, _ => -1 };
            if (value is >= 0 and <= 100) return value;
        }
        return null;
    }

    private static string DisplayName(string name)
    {
        foreach (var suffix in new[] { " Hands-Free AG Audio", " Hands-Free AG", " Hands-Free HF", " Hands-Free", " Stereo", " Avrcp Transport" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length].Trim();
        return name.Trim();
    }

    private async Task<DeviceBattery> ReadBatteryAsync(DeviceInformation device, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var deadline = timeout.Token;
        var name = string.IsNullOrWhiteSpace(device.Name) ? "蓝牙 LE 设备" : DisplayName(device.Name);
        try
        {
            var opening = await dispatcher.InvokeAsync(() => BluetoothLEDevice.FromIdAsync(device.Id).AsTask(deadline),
                DispatcherPriority.Background, deadline);
            using var bluetooth = await opening;
            if (bluetooth is not null)
            {
                var services = await bluetooth.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(deadline);
                if (services.Status == GattCommunicationStatus.Success)
                {
                    var levels = new List<byte>();
                    try
                    {
                        foreach (var service in services.Services)
                        {
                            var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                                BluetoothCacheMode.Cached).AsTask(deadline);
                            if (characteristics.Status != GattCommunicationStatus.Success)
                            {
                                continue;
                            }
                            foreach (var characteristic in characteristics.Characteristics)
                            {
                                var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(deadline);
                                if (value.Status == GattCommunicationStatus.Success && value.Value.Length >= 1)
                                {
                                    using var reader = DataReader.FromBuffer(value.Value);
                                    byte level = reader.ReadByte();
                                    if (level <= 100) levels.Add(level);
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var service in services.Services) service.Dispose();
                    }
                    if (levels.Count > 0)
                    {
                        return new DeviceBattery(name, string.Join(" / ", levels.Select(level => $"{level}%")), levels.Min() <= 20,
                            levels.Min(), "蓝牙 LE");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException) { }
        return new DeviceBattery(name, "未知", false, Connection: "蓝牙 LE");
    }

    private static List<DeviceBattery> ReadControllers()
    {
        var devices = new List<DeviceBattery>();
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetBatteryInformation(slot, 0, out var battery) != 0 || battery.Type is 0 or 1)
            {
                continue;
            }
            string level = battery.Type == 255 ? "未知" : battery.Level switch
            {
                0 => "即将耗尽", 1 => "低", 2 => "中", 3 => "高", _ => "未知"
            };
            devices.Add(new DeviceBattery($"XInput 手柄 {slot + 1}", level, battery.Type != 255 && battery.Level <= 1,
                Connection: "XInput 无线"));
        }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBattery { public byte Type, Level; }
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetBatteryInformation(uint slot, byte deviceType, out XInputBattery battery);
}
