using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Island.App.Persistence;

internal sealed class SettingsStore
{
    public static bool SourceEnabled(IReadOnlyDictionary<string, bool> sources, string id) =>
        sources.GetValueOrDefault(id, id is "music" or "device-battery");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WindowsIsland";
    private readonly string _path = Path.Combine(LocalDataStore.DirectoryPath, "settings.json");

    public Dictionary<string, bool> LoadSources()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 初次启动或配置不可读取时，使用各数据源的默认值。
            return new();
        }
    }

    public void SaveSources(Dictionary<string, bool> sources) =>
        LocalDataStore.WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(sources));

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    public void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new SecurityException("无法打开当前用户的启动项。");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取应用路径。");
            key.SetValue(RunValue, $"\"{executable}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
