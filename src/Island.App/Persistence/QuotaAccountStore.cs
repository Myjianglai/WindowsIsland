using System;
using System.Collections.Generic;
using System.IO;

namespace Island.App.Persistence;

internal enum QuotaService { Codex, Claude, WorkBuddy, DeepSeek, Cursor }

internal sealed record QuotaAccount(Guid Id, QuotaService Service, string Name, string Color, bool Pinned,
    string LocalPath = "", string ProfilePath = "", string ApiKey = "", decimal? ManualCredits = null, string[]? RingKeys = null);

internal sealed class QuotaAccountStore
{
    private const string FileName = "quota-accounts.bin";
    public static string DefaultClaudeStatus => Path.Combine(LocalDataStore.DirectoryPath, "claude-status.json");

    public List<QuotaAccount> Load() => LocalDataStore.ReadProtected<List<QuotaAccount>>(FileName) ?? [];
    public void Save(List<QuotaAccount> accounts) => LocalDataStore.WriteProtected(FileName, accounts);
}
