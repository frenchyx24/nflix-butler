using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One thing the butler saw, and what it did about it.</summary>
public sealed class IntakeEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = string.Empty;
    public string? Destination { get; set; }
    public string Kind { get; set; } = "movie";
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int Season { get; set; }
    public int Episode { get; set; }
    public string Library { get; set; } = string.Empty;
    public string Action { get; set; } = "detected";
    public string Status { get; set; } = "pending";
    public string Message { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public long SizeBytes { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Everything the butler remembers across restarts.</summary>
public sealed class ButlerState
{
    public DateTime? StartedUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public int Runs { get; set; }
    public int Filed { get; set; }
    public int Repaired { get; set; }
    public string? LastError { get; set; }
    public List<IntakeEntry> Entries { get; set; } = new();
    public Dictionary<string, long> Seen { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Atomic JSON store for the butler state and the per-file snapshots that make detection
/// restart-safe. Every write goes to a sibling temp file then moves over the target.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "state.json");
        State = Load() ?? new ButlerState();
    }

    public ButlerState State { get; private set; }

    public void Save()
    {
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(State, Options));
            File.Move(temp, _path, true);
        }
    }

    /// <summary>Adds an entry, trims the log, and returns it.</summary>
    public IntakeEntry Add(IntakeEntry entry, int keep)
    {
        lock (_gate)
        {
            State.Entries.Insert(0, entry);
            if (State.Entries.Count > Math.Max(50, keep))
            {
                State.Entries.RemoveRange(keep, State.Entries.Count - keep);
            }

            // A snapshot older than the last 20k files is not worth keeping: paths can be reused.
            if (State.Seen.Count > 20_000)
            {
                State.Seen.Clear();
            }
        }

        return entry;
    }

    /// <summary>True when this exact file version was already handled.</summary>
    public bool AlreadySeen(string path, long size, DateTime modifiedUtc)
    {
        var key = path + "|" + size + "|" + modifiedUtc.Ticks;
        lock (_gate)
        {
            return State.Seen.ContainsKey(key);
        }
    }

    public void MarkSeen(string path, long size, DateTime modifiedUtc)
    {
        var key = path + "|" + size + "|" + modifiedUtc.Ticks;
        lock (_gate)
        {
            State.Seen[key] = DateTime.UtcNow.Ticks;
        }
    }

    public IReadOnlyList<IntakeEntry> Recent(int limit) =>
        State.Entries.Take(Math.Clamp(limit, 1, 1000)).ToList();

    private ButlerState? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ButlerState>(json, Options);
        }
        catch (JsonException)
        {
            // A corrupt store must never stop the server: start clean and keep the broken file for inspection.
            TryKeepCorrupt();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void TryKeepCorrupt()
    {
        try
        {
            File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
        }
        catch (IOException)
        {
            // Nothing else to do; the next save overwrites it.
        }
    }
}
