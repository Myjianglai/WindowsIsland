using System;
using System.Runtime.InteropServices;

namespace Island.App.Providers;

internal static class AudioVolume
{
    private static readonly Guid EnumeratorId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static double? Read() => WithEndpoint<IEndpointVolume>(endpoint =>
    {
        endpoint.GetMasterVolumeLevelScalar(out float value);
        return value;
    });

    public static bool Write(double value) => WithEndpoint<IEndpointVolume>(endpoint =>
    {
        endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(value, 0, 1), IntPtr.Zero);
        return value;
    }).HasValue;

    public static double? Peak() => WithEndpoint<IAudioMeter>(endpoint =>
    {
        endpoint.GetPeakValue(out float value);
        return value;
    });

    private static double? WithEndpoint<T>(Func<T, double> operation)
    {
        object? enumerator = null;
        IDevice? device = null;
        object? volume = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorId, throwOnError: true)!);
            ((IDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out device);
            var id = typeof(T).GUID;
            device.Activate(in id, 23, IntPtr.Zero, out volume);
            return operation((T)volume);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (volume is not null) Marshal.ReleaseComObject(volume);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeter
    {
        void GetPeakValue(out float value);
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        void Activate(in Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object volume);
    }

    // 按 Windows SDK 中的 COM 方法顺序声明已使用的方法及其前置槽位。
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr callback);
        void UnregisterControlChangeNotify(IntPtr callback);
        void GetChannelCount(out uint channels);
        void SetMasterVolumeLevel(float value, IntPtr context);
        void SetMasterVolumeLevelScalar(float value, IntPtr context);
        void GetMasterVolumeLevel(out float value);
        void GetMasterVolumeLevelScalar(out float value);
    }
}
