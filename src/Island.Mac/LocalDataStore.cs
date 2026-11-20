using System.Text.Json;

namespace Island.App.Persistence;

internal static class LocalDataStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "IslandMac");

    public static T? ReadProtected<T>(string name)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllBytes(Path.Combine(DirectoryPath, name))); }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
    }

    public static void WriteProtected<T>(string name, T value) =>
        WriteAtomic(Path.Combine(DirectoryPath, name), JsonSerializer.SerializeToUtf8Bytes(value));

    public static void WriteAtomic(string path, byte[] content)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsMacOS())
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
            if (OperatingSystem.IsMacOS()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new FileStream(temp, options)) writer.Write(content);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
