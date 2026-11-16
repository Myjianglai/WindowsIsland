using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Island.App.Persistence;

internal static class LocalDataStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIsland");

    public static T? ReadProtected<T>(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        try
        {
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<T>(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
    }

    public static void WriteProtected<T>(string name, T value)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            WriteAtomic(Path.Combine(DirectoryPath, name),
                ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
