using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Island.App.Persistence;

namespace Island.Mac;

internal sealed record AgentSession(string Id, string Source, string Title, string State,
    DateTimeOffset Time, string? Model, string? Workspace, bool Orca);

internal static class AgentHooks
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Events = Path.Combine(LocalDataStore.DirectoryPath, "AgentEvents");
    internal static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Home, ".codex");
    private static string ClaudeHome => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home, ".claude");

    public static void Receive(string source)
    {
        if (source is not ("Codex" or "Claude")) return;
        try
        {
            using var document = JsonDocument.Parse(Console.In.ReadToEnd());
            var input = document.RootElement;
            var id = Read(input, "session_id");
            var kind = Read(input, "hook_event_name");
            string? state = kind switch
            {
                "UserPromptSubmit" => "运行中", "PermissionRequest" => "待确认", "Stop" or "SessionEnd" => "已完成",
                "StopFailure" => "出错", "Interrupt" => "已中断", _ => null
            };
            if (id is null || state is null) return;
            var workspace = Read(input, "cwd");
            var customTitle = source == "Codex" ? ThreadTitle(id) : null;
            var model = Read(input, "model");
            bool orca = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORCA_PANE_KEY"));
            var session = new AgentSession(id, source, customTitle ?? (workspace is null ? source : Path.GetFileName(workspace.TrimEnd('/'))),
                state, DateTimeOffset.UtcNow, model, workspace, orca);
            LocalDataStore.WriteAtomic(Path.Combine(Events, $"{Guid.NewGuid():N}.json"), JsonSerializer.SerializeToUtf8Bytes(session));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public static bool ReadEvents(Dictionary<(string, string), AgentSession> sessions)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
        bool changed = false;
        foreach (var key in sessions.Where(row => row.Value.Time < cutoff).Select(row => row.Key).ToArray())
            changed |= sessions.Remove(key);
        if (!Directory.Exists(Events)) return changed;
        foreach (string file in Directory.EnumerateFiles(Events, "*.json").OrderBy(File.GetLastWriteTimeUtc).Take(500))
        {
            AgentSession? item;
            try { item = JsonSerializer.Deserialize<AgentSession>(File.ReadAllBytes(file)); }
            catch (JsonException) { item = null; }
            if (item is not null && item.Time >= cutoff)
            {
                var key = (item.Source, item.Id);
                if (!sessions.TryGetValue(key, out var previous) || previous.Time < item.Time)
                {
                    sessions[key] = item with { Model = item.Model ?? previous?.Model, Orca = item.Orca || previous?.Orca == true };
                    changed = true;
                }
            }
            if (changed) LocalDataStore.WriteProtected("agent-sessions.json", sessions.Values.ToArray());
            File.Delete(file);
        }
        if (changed) LocalDataStore.WriteProtected("agent-sessions.json", sessions.Values.ToArray());
        return changed;
    }

    public static void Install()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("请在 macOS 上安装钩子。");
        var executable = Environment.ProcessPath ?? throw new IOException("找不到程序路径。");
        var shellPath = "'" + executable.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        InstallFor(Path.Combine(CodexHome, "hooks.json"), shellPath, "Codex",
            ["UserPromptSubmit", "PermissionRequest", "Stop", "Interrupt"]);
        InstallFor(Path.Combine(ClaudeHome, "settings.json"), shellPath, "Claude",
            ["UserPromptSubmit", "PermissionRequest", "Stop", "StopFailure", "SessionEnd"]);
    }

    private static void InstallFor(string file, string executable, string source, string[] events)
    {
        var root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject();
        root["hooks"] ??= new JsonObject();
        var hooks = root["hooks"]!.AsObject();
        foreach (var name in events)
        {
            hooks[name] ??= new JsonArray();
            var groups = hooks[name]!.AsArray();
            var command = $"{executable} --agent-hook {source}";
            if (groups.OfType<JsonObject>().Any(group => group["hooks"] is JsonArray entries &&
                entries.OfType<JsonObject>().Any(entry => (string?)entry["command"] == command))) continue;
            groups.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }) });
        }
        if (File.Exists(file)) File.Copy(file, file + ".island-" + Guid.NewGuid().ToString("N") + ".bak");
        LocalDataStore.WriteAtomic(file, Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
    }

    public static string? Read(JsonElement input, string key) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string? ThreadTitle(string id)
    {
        var index = Path.Combine(CodexHome, "session_index.jsonl");
        if (!File.Exists(index)) return null;
        foreach (var line in File.ReadLines(index))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (Read(json.RootElement, "id") == id) return Read(json.RootElement, "thread_name");
            }
            catch (JsonException) { }
        }
        return null;
    }
}
