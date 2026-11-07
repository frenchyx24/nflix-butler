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

    public void Touch(string videoId)
    {
        lock (_gate)
        {
            State.Recent.RemoveAll(r => r.VideoId == videoId);
            State.Recent.Insert(0, new YtRecentRecord { VideoId = videoId, AtUtc = DateTime.UtcNow });
            if (State.Recent.Count > 200) State.Recent.RemoveRange(200, State.Recent.Count - 200);
        }
    }

    public List<YtRecentRecord> Recent(int limit)
    {
        lock (_gate) { return State.Recent.Take(Math.Clamp(limit, 1, 200)).ToList(); }
    }

    public static string Normalize(string value) =>
        new string(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}
