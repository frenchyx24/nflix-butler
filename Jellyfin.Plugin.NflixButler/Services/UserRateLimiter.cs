using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>A named limit: at most [Max] calls per [Window] for one user.</summary>
public sealed record RateBucket(string Name, int Max, TimeSpan Window)
{
    /// <summary>Catalogue / YT / Deezer searches and artist pages (shared: they all hit external quotas).</summary>
    public static readonly RateBucket Search = new("search", 60, TimeSpan.FromMinutes(1));

    /// <summary>Artist pictures (each call may run up to 20 catalogue lookups).</summary>
    public static readonly RateBucket Pictures = new("pictures", 20, TimeSpan.FromMinutes(1));

    public static readonly RateBucket Lyrics = new("lyrics", 60, TimeSpan.FromMinutes(1));

    public static readonly RateBucket Cover = new("cover", 120, TimeSpan.FromMinutes(1));

    /// <summary>Share-link imports (several external pages per call).</summary>
    public static readonly RateBucket ImportUrl = new("import-url", 10, TimeSpan.FromMinutes(1));

    /// <summary>Deezer export: up to 1000 outbound calls per request.</summary>
    public static readonly RateBucket ExportDeezer = new("export-deezer", 3, TimeSpan.FromHours(1));
}

/// <summary>
/// Sliding-window rate limiter keyed by Jellyfin user and bucket. Behind the reverse proxy every
/// user shares one IP address, so limits are per user (claims), never per address. Callers
/// without a user id (API keys) share one key per bucket.
/// </summary>
public sealed class UserRateLimiter
{
    public const string Message = "Trop de requêtes, réessayez dans un instant.";

    /// <summary>Above this many keys, expired entries are pruned before a new key is added.</summary>
    public const int MaxKeys = 10000;

    public static readonly UserRateLimiter Shared = new();

    private sealed class Entry
    {
        public Queue<DateTime> Hits { get; } = new();
        public TimeSpan Window { get; init; }
    }

    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public UserRateLimiter(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public int KeyCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Records one call of [userKey] in [bucket]; false when the limit is reached (call refused).</summary>
    public bool TryAcquire(string? userKey, RateBucket bucket)
    {
        var key = bucket.Name + ":" + (string.IsNullOrEmpty(userKey) ? "-" : userKey);
        var now = _now();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                if (_entries.Count >= MaxKeys) Prune(now);
                _entries[key] = entry = new Entry { Window = bucket.Window };
            }
            while (entry.Hits.Count > 0 && now - entry.Hits.Peek() >= bucket.Window) entry.Hits.Dequeue();
            if (entry.Hits.Count >= bucket.Max) return false;
            entry.Hits.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var key in _entries.Where(kv => kv.Value.Hits.Count == 0 || now - kv.Value.Hits.Last() >= kv.Value.Window)
                     .Select(kv => kv.Key).ToList())
            _entries.Remove(key);
        // Still full (a flood of live keys): start over rather than grow without bound.
        if (_entries.Count >= MaxKeys) _entries.Clear();
    }

    /// <summary>Shared limiter check for the caller of a controller action.</summary>
    public static bool Allow(ClaimsPrincipal? user, RateBucket bucket) =>
        Shared.TryAcquire(CallerIdentity.HistoryKey(user), bucket);
}
