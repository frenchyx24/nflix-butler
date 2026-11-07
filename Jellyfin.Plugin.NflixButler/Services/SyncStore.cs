using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One linked streaming account (Deezer / Spotify) for one Jellyfin user.</summary>
public sealed class SyncAccount
{
    public string Service { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public DateTime LinkedAtUtc { get; set; }
}

/// <summary>All linked accounts: userId -> service -> account.</summary>
public sealed class SyncState
{
    public Dictionary<string, Dictionary<string, SyncAccount>> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/** Persistent store for linked streaming accounts (sync.json). Tokens never leave the server. */
public sealed class SyncStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SyncStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "sync.json");
        State = Load() ?? new SyncState();
    }

    public SyncState State { get; }

    private SyncState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(_path), Options);
        }
        catch { return null; }
    }

    public void Save()
    {
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, Options));
            File.Move(temp, _path, true);
        }
    }

    public SyncAccount? Get(string userId, string service)
    {
        lock (_gate)
        {
            return State.Accounts.TryGetValue(userId, out var byService) && byService.TryGetValue(service, out var account)
                ? account
                : null;
        }
    }

    public void Put(string userId, SyncAccount account)
    {
        lock (_gate)
        {
            if (!State.Accounts.TryGetValue(userId, out var byService))
                State.Accounts[userId] = byService = new Dictionary<string, SyncAccount>(StringComparer.OrdinalIgnoreCase);
            byService[account.Service] = account;
        }
    }

    public void Remove(string userId, string service)
    {
        lock (_gate)
        {
            if (State.Accounts.TryGetValue(userId, out var byService)) byService.Remove(service);
        }
    }

    public List<SyncAccount> ForUser(string userId)
    {
        lock (_gate)
        {
            return State.Accounts.TryGetValue(userId, out var byService) ? new List<SyncAccount>(byService.Values) : new List<SyncAccount>();
        }
    }
}
