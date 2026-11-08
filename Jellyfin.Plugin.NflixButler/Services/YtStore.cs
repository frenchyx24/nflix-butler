using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>Full metadata of one YouTube Music track, persisted on the server.</summary>
public sealed class YtItem
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumBrowseId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelTitle { get; set; } = "";
    public int Duration { get; set; }
    public string Thumbnail { get; set; } = "";
    public string DownloadedPath { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One past search: query, when, which video ids came back.</summary>
public sealed class YtSearchRecord
{
    public string Query { get; set; } = "";
    public string Normalized { get; set; } = "";
    public DateTime AtUtc { get; set; }
    public List<string> VideoIds { get; set; } = new();
}

/// <summary>A track the user played, searched in detail or downloaded.</summary>
public sealed class YtRecentRecord
{
    public string VideoId { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

/// <summary>What the butler remembers about YouTube Music: full item metadata, past searches, recents.</summary>
public sealed class YtState
{
    public Dictionary<string, YtItem> Items { get; set; } = new(StringComparer.Ordinal);
    public List<YtSearchRecord> Searches { get; set; } = new();
    public List<YtRecentRecord> Recent { get; set; } = new();
    /// <summary>Per-user search history (user id "N" → newest first): never shared between users.</summary>
    public Dictionary<string, List<YtSearchRecord>> UserSearches { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Per-user listens (user id "N" → newest first).</summary>
    public Dictionary<string, List<YtRecentRecord>> UserRecent { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Catalogue track id → resolved audio id.</summary>
    public Dictionary<string, string> CatalogVideo { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Catalogue track id → downloaded file in the music library.</summary>
    public Dictionary<string, string> CatalogFiles { get; set; } = new(StringComparer.Ordinal);
}

/**
 * JSON store for YouTube Music data (yt.json next to state.json). Everything the app shows about
 * YT Music comes from here once fetched: searches are cached 24 h, item metadata never expires
 * until refreshed, recents keep the last 200.
 */
public sealed class YtStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public YtStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "yt.json");
        State = Load() ?? new YtState();
    }

    public YtState State { get; private set; }

    private YtState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<YtState>(File.ReadAllText(_path), Options);
        }
        catch
        {
            return null;
        }
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

    /// <summary>Cached search results younger than [ttl], or null.</summary>
    public YtSearchRecord? CachedSearch(string normalized, TimeSpan ttl)
    {
        lock (_gate)
        {
            return State.Searches.FirstOrDefault(s => s.Normalized == normalized && DateTime.UtcNow - s.AtUtc < ttl);
        }
    }

    public void RecordSearch(string query, string normalized, List<string> videoIds)
    {
        lock (_gate)
        {
            State.Searches.RemoveAll(s => s.Normalized == normalized);
            State.Searches.Insert(0, new YtSearchRecord
            {
                Query = query,
                Normalized = normalized,
                AtUtc = DateTime.UtcNow,
                VideoIds = videoIds,
            });
            if (State.Searches.Count > 100) State.Searches.RemoveRange(100, State.Searches.Count - 100);
        }
    }

    public List<YtSearchRecord> Searches(int limit)
    {
        lock (_gate) { return State.Searches.Take(Math.Clamp(limit, 1, 100)).ToList(); }
    }

    /// <summary>Records [query] in the history of [userKey] (no-op without a user).</summary>
    public void RecordUserSearch(string userKey, string query)
    {
        if (string.IsNullOrEmpty(userKey) || string.IsNullOrWhiteSpace(query)) return;
        var normalized = Normalize(query);
        lock (_gate)
        {
            State.UserSearches ??= new(StringComparer.Ordinal);
            if (!State.UserSearches.TryGetValue(userKey, out var list)) State.UserSearches[userKey] = list = new();
            list.RemoveAll(s => s.Normalized == normalized);
            list.Insert(0, new YtSearchRecord { Query = query.Trim(), Normalized = normalized, AtUtc = DateTime.UtcNow });
            if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
        }
    }

    public List<YtSearchRecord> UserSearchList(string userKey, int limit)
    {
        lock (_gate)
        {
            return State.UserSearches != null && State.UserSearches.TryGetValue(userKey, out var list)
                ? list.Take(Math.Clamp(limit, 1, 50)).ToList()
                : new List<YtSearchRecord>();
        }
    }

    public List<YtRecentRecord> UserRecentList(string userKey, int limit)
    {
        lock (_gate)
        {
            return State.UserRecent != null && State.UserRecent.TryGetValue(userKey, out var list)
                ? list.Take(Math.Clamp(limit, 1, 200)).ToList()
                : new List<YtRecentRecord>();
        }
    }

    public YtItem? Item(string videoId)
    {
        lock (_gate) { return State.Items.TryGetValue(videoId, out var item) ? item : null; }
    }

    public void PutItem(YtItem item)
    {
        item.UpdatedAtUtc = DateTime.UtcNow;
        lock (_gate) { State.Items[item.VideoId] = item; }
    }

    public List<YtItem> Items(IEnumerable<string> videoIds)
    {
        lock (_gate)
        {
            return videoIds
                .Select(id => State.Items.TryGetValue(id, out var item) ? item : null)
                .Where(i => i != null)
                .Cast<YtItem>()
                .ToList();
        }
    }

    public void Touch(string videoId, string userKey = "")
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(userKey))
            {
                State.UserRecent ??= new(StringComparer.Ordinal);
                if (!State.UserRecent.TryGetValue(userKey, out var mine)) State.UserRecent[userKey] = mine = new();
                mine.RemoveAll(r => r.VideoId == videoId);
                mine.Insert(0, new YtRecentRecord { VideoId = videoId, AtUtc = DateTime.UtcNow });
                if (mine.Count > 200) mine.RemoveRange(200, mine.Count - 200);
            }
            State.Recent.RemoveAll(r => r.VideoId == videoId);
            State.Recent.Insert(0, new YtRecentRecord { VideoId = videoId, AtUtc = DateTime.UtcNow });
            if (State.Recent.Count > 200) State.Recent.RemoveRange(200, State.Recent.Count - 200);
        }
    }

    public List<YtRecentRecord> Recent(int limit)
    {
        lock (_gate) { return State.Recent.Take(Math.Clamp(limit, 1, 200)).ToList(); }
    }

    public string? CatalogVideo(string trackId)
    {
        lock (_gate) { return State.CatalogVideo != null && State.CatalogVideo.TryGetValue(trackId, out var v) ? v : null; }
    }

    public void SetCatalogVideo(string trackId, string videoId)
    {
        lock (_gate) { (State.CatalogVideo ??= new(StringComparer.Ordinal))[trackId] = videoId; }
    }

    public string? CatalogFile(string trackId)
    {
        lock (_gate) { return State.CatalogFiles != null && State.CatalogFiles.TryGetValue(trackId, out var v) ? v : null; }
    }

    public void SetCatalogFile(string trackId, string path)
    {
        lock (_gate) { (State.CatalogFiles ??= new(StringComparer.Ordinal))[trackId] = path; }
    }

    public static string Normalize(string value) =>
        new string(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}
