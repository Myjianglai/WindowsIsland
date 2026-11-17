using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Island.Core.Events;

namespace Island.App.Persistence;

internal sealed class QuotaBalanceHistory(string fileName = "quota-balances.bin")
{
    private Dictionary<Guid, Dictionary<string, decimal>>? _peaks;
    private bool _canSave = true;
    private bool _dirty;

    public QuotaSnapshot AttachReference(QuotaSnapshot snapshot)
    {
        var peaks = _peaks ??= Load();
        var retained = snapshot.Accounts.Where(account => account.Service == nameof(QuotaService.DeepSeek))
            .Select(account => account.Id).ToHashSet();
        foreach (var id in peaks.Keys.Where(id => !retained.Contains(id)).ToArray())
            _dirty |= peaks.Remove(id);

        var accounts = snapshot.Accounts.Select(account =>
        {
            if (account.Balances is not { Count: > 0 } balances) return account;
            if (!peaks.TryGetValue(account.Id, out var currencies))
                peaks.Add(account.Id, currencies = new());
            return account with { Balances = balances.Select(balance =>
            {
                var previous = currencies.GetValueOrDefault(balance.Currency);
                var reference = Math.Max(previous, Math.Max(0, balance.Amount));
                if (reference > previous)
                {
                    currencies[balance.Currency] = reference;
                    _dirty = true;
                }
                return balance with { Reference = reference };
            }).ToArray() };
        }).ToArray();
        if (_dirty && _canSave)
        {
            try
            {
                LocalDataStore.WriteProtected(fileName, peaks);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
        }
        return new QuotaSnapshot(accounts);
    }

    private Dictionary<Guid, Dictionary<string, decimal>> Load()
    {
        try { return LocalDataStore.ReadProtected<Dictionary<Guid, Dictionary<string, decimal>>>(fileName) ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _canSave = false;
            return [];
        }
    }
}
