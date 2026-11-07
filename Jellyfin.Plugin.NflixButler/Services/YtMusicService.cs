using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One search hit: a track, or an album/artist suggestion (browse id) from YT Music tabs.</summary>
public sealed class YtHit
{
    public string Kind { get; set; } = "track"; // track | album | artist
    public string VideoId { get; set; } = "";
    public string BrowseId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

/**
 * YouTube Music access through the standalone yt-dlp binary, with a persistent server-side cache
 * ([YtStore]): searches are cached 24 h, full item metadata is fetched once per videoId and kept,
 * and every played/seen track lands in the recents list. Results are ranked so official audio
 * beats karaoke/slowed/covers.
 */
public sealed class YtMusicService
{
    private static readonly string Binary =
        System.IO.File.Exists("/config/bin/yt-dlp") ? "/config/bin/yt-dlp" : "yt-dlp";

    private static readonly ConcurrentDictionary<string, (DateTime At, List<YtHit> Hits)> MemCache = new();
    private static readonly SemaphoreSlim RunGate = new(2, 2);

    private static readonly string[] BadWords = { "karaoke", "slowed", "reverb", "nightcore", "instrumental", "cover", "8d", "sped up", "bass boosted", "piano", "tribute", "remix" };
    private static readonly string[] GoodWords = { "official audio", "official music video", "audio officiel", "lyrics" };

    private readonly YtStore _store;

    public YtMusicService(YtStore store) => _store = store;

    public YtStore Store => _store;

    private static async Task<string> RunAsync(Action<System.Collections.ObjectModel.Collection<string>> args, CancellationToken ct)
    {
        await RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Binary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            args(psi.ArgumentList);
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException("yt-dlp timeout"); }
            var output = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await stderr.ConfigureAwait(false);
                throw new InvalidOperationException("yt-dlp: " + error.Split('\n').LastOrDefault(l => l.Length > 0)?.Trim());
            }
            return output;
        }
        finally
        {
            RunGate.Release();
        }
    }

    private static int Score(YtHit hit)
    {
        var t = (hit.Title + " " + hit.Subtitle).ToLowerInvariant();
        var score = 0;
        foreach (var w in GoodWords) if (t.Contains(w)) score += 50;
        foreach (var w in BadWords) if (t.Contains(w)) score -= 40;
        if (hit.Kind == "track" && hit.Subtitle.Contains(" - Topic")) score += 20;
        return score;
    }

    /** Searches YouTube Music; cached 24 h in the persistent store, ranked server-side. */
    public async Task<List<YtHit>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<YtHit>();
        limit = Math.Clamp(limit <= 0 ? 12 : limit, 1, 25);
        var normalized = YtStore.Normalize(query);
        var key = limit + ":" + normalized;

        if (MemCache.TryGetValue(key, out var mem) && DateTime.UtcNow - mem.At < TimeSpan.FromMinutes(10))
            return mem.Hits;

        var cached = _store.CachedSearch(normalized, TimeSpan.FromHours(24));
        if (cached != null)
        {
            var hits = ParseSearchEntries(cached.VideoIds, limit);
            MemCache[key] = (DateTime.UtcNow, hits);
            return hits;
        }

        var json = await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-J");
            list.Add("--flat-playlist");
            list.Add("--playlist-items");
            list.Add("1-" + (limit * 2));
            list.Add("https://music.youtube.com/search?q=" + query);
        }, ct).ConfigureAwait(false);

        var parsed = ParseSearchJson(json, limit);
        var ids = parsed.Where(h => h.Kind == "track").Select(h => h.VideoId).ToList();
        _store.RecordSearch(query, normalized, ids);
        foreach (var hit in parsed.Where(h => h.Kind == "track"))
        {
            _store.PutItem(new YtItem
            {
                VideoId = hit.VideoId,
                Title = hit.Title,
                Artist = hit.Subtitle,
                Thumbnail = "https://i.ytimg.com/vi/" + hit.VideoId + "/hqdefault.jpg",
            });
        }
        _store.Save();
        MemCache[key] = (DateTime.UtcNow, parsed);
        return parsed;
    }

    private List<YtHit> ParseSearchEntries(List<string> videoIds, int limit)
    {
        var items = _store.Items(videoIds);
        return items.Take(limit).Select(i => new YtHit
        {
            Kind = "track",
            VideoId = i.VideoId,
            Title = i.Title,
            Subtitle = i.Artist,
        }).OrderByDescending(Score).ToList();
    }

    private static List<YtHit> ParseSearchJson(string json, int limit)
    {
        var hits = new List<YtHit>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries)) return hits;
        foreach (var entry in entries.EnumerateArray())
        {
            var ieKey = entry.TryGetProperty("ie_key", out var ie) ? ie.GetString() : "";
            var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            var title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var id = entry.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            if (ieKey == "Youtube" && !string.IsNullOrEmpty(id) && id.Length <= 32)
            {
                hits.Add(new YtHit { Kind = "track", VideoId = id, Title = title, Subtitle = "" });
            }
            else if (ieKey == "YoutubeTab" && url.Contains("/browse/"))
            {
                var browseId = url.Split("/browse/").LastOrDefault()?.Split('?').FirstOrDefault() ?? "";
                if (browseId.StartsWith("MPREb_"))
                    hits.Add(new YtHit { Kind = "album", BrowseId = browseId, Title = title });
                else if (browseId.StartsWith("UC"))
                    hits.Add(new YtHit { Kind = "artist", BrowseId = browseId, Title = title });
            }
        }
        return hits.Where(h => h.Kind == "track").OrderByDescending(Score).Take(limit)
            .Concat(hits.Where(h => h.Kind != "track").Take(4))
            .ToList();
    }

    /// <summary>Full metadata of one track (title, artist, album, channel). Fetched once, then cached forever.</summary>
    public async Task<YtItem> InfoAsync(string videoId, CancellationToken ct)
    {
        var cached = _store.Item(videoId);
        if (cached != null && (cached.Artist.Length > 0 || cached.Album.Length > 0)) return cached;

        var json = await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-J");
            list.Add("--no-playlist");
            list.Add("https://www.youtube.com/watch?v=" + videoId);
        }, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Get(params string[] names)
        {
            foreach (var name in names)
                if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString() ?? "";
            return "";
        }
        var item = new YtItem
        {
            VideoId = videoId,
            Title = Get("title"),
            Artist = Get("artist", "creator", "channel"),
            Album = Get("album"),
            ChannelId = Get("channel_id"),
            ChannelTitle = Get("channel", "uploader"),
            Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
            Thumbnail = "https://i.ytimg.com/vi/" + videoId + "/maxresdefault.jpg",
        };
        if (cached != null)
        {
            // Keep the search-time fields when the watch page lacks them.
            if (item.Title.Length == 0) item.Title = cached.Title;
            if (item.Artist.Length == 0) item.Artist = cached.Artist;
            item.DownloadedPath = cached.DownloadedPath;
        }
        _store.PutItem(item);
        _store.Save();
        return item;
    }

    /** Top tracks of an artist channel (music.youtube.com browse page). */
    public async Task<List<YtHit>> ArtistAsync(string channelId, int limit, CancellationToken ct)
    {
        var json = await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-J");
            list.Add("--flat-playlist");
            list.Add("--playlist-items");
            list.Add("1-" + Math.Clamp(limit, 1, 50));
            list.Add("https://music.youtube.com/browse/" + channelId);
        }, ct).ConfigureAwait(false);

        var hits = new List<YtHit>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("entries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id) || id.Length > 32) continue;
                hits.Add(new YtHit
                {
                    Kind = "track",
                    VideoId = id!,
                    Title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Subtitle = entry.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "",
                });
            }
        }
        return hits;
    }

    /** Tracks of an album (music.youtube.com browse page, MPREb_ id). */
    public Task<List<YtHit>> AlbumAsync(string browseId, int limit, CancellationToken ct) =>
        BrowseTracksAsync(browseId, limit, ct);

    private async Task<List<YtHit>> BrowseTracksAsync(string browseId, int limit, CancellationToken ct)
    {
        var json = await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-J");
            list.Add("--flat-playlist");
            list.Add("--playlist-items");
            list.Add("1-" + Math.Clamp(limit, 1, 200));
            list.Add("https://music.youtube.com/browse/" + browseId);
        }, ct).ConfigureAwait(false);

        var hits = new List<YtHit>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("entries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var ieKey = entry.TryGetProperty("ie_key", out var ie) ? ie.GetString() : "";
                var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (ieKey != "Youtube" || string.IsNullOrEmpty(id) || id.Length > 32) continue;
                hits.Add(new YtHit
                {
                    Kind = "track",
                    VideoId = id!,
                    Title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Subtitle = entry.TryGetProperty("channel", out var c) ? c.GetString() ?? "" : "",
                });
            }
        }
        return hits;
    }

    /** Resolves a fresh signed audio stream URL for [videoId] (itag 140 m4a preferred). */
    public async Task<string> StreamUrlAsync(string videoId, CancellationToken ct)
    {
        var url = (await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-f");
            list.Add("bestaudio[ext=m4a]/bestaudio");
            list.Add("--print");
            list.Add("%(url)s");
            list.Add("https://www.youtube.com/watch?v=" + videoId);
        }, ct).ConfigureAwait(false)).Trim().Split('\n').LastOrDefault();
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("No audio stream for " + videoId);
        return url;
    }

    /// <summary>Marks a track as seen (played/searched) in the persistent recents list.</summary>
    public void Touch(string videoId)
    {
        _store.Touch(videoId);
        _store.Save();
    }
}
