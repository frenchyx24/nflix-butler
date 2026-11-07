using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One YouTube Music search result, as the app displays it.</summary>
public sealed class YtResult
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public int Duration { get; set; }
    public string Thumbnail => "https://i.ytimg.com/vi/" + VideoId + "/hqdefault.jpg";
}

/**
 * Talks to YouTube Music through the standalone yt-dlp binary kept in /config/bin.
 * Search results are cached 10 minutes; stream URLs only 5 minutes because Google
 * signs them per-IP and they expire about 6 hours after issue.
 */
public sealed class YtMusicService
{
    private static readonly string Binary =
        System.IO.File.Exists("/config/bin/yt-dlp") ? "/config/bin/yt-dlp" : "yt-dlp";

    private static readonly ConcurrentDictionary<string, (DateTime At, List<YtResult> Items)> SearchCache = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, string Url)> UrlCache = new();
    private static readonly SemaphoreSlim RunGate = new(2, 2);

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
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
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

    /** Searches YouTube (music results first) and maps the flat playlist to [YtResult]. */
    public async Task<List<YtResult>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return new List<YtResult>();
        limit = Math.Clamp(limit <= 0 ? 10 : limit, 1, 25);
        var key = limit + ":" + query.ToLowerInvariant();
        if (SearchCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10))
            return hit.Items;

        var json = await RunAsync(list =>
        {
            list.Add("-q");
            list.Add("--no-warnings");
            list.Add("-J");
            list.Add("--flat-playlist");
            list.Add("ytsearch" + limit + ":" + query);
        }, ct).ConfigureAwait(false);

        var results = new List<YtResult>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("entries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                results.Add(new YtResult
                {
                    VideoId = id!,
                    Title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Artist = entry.TryGetProperty("uploader", out var u) ? u.GetString() ?? "" :
                             entry.TryGetProperty("channel", out var ch) ? ch.GetString() ?? "" : "",
                    Duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
                });
            }
        }
        SearchCache[key] = (DateTime.UtcNow, results);
        return results;
    }

    /** Resolves a fresh signed audio stream URL for [videoId] (itag 140 m4a preferred). */
    public async Task<string> StreamUrlAsync(string videoId, CancellationToken ct)
    {
        if (UrlCache.TryGetValue(videoId, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
            return hit.Url;

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
        UrlCache[videoId] = (DateTime.UtcNow, url);
        return url;
    }

}
