using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One lyric line; StartMs is -1 for unsynced lyrics.</summary>
public sealed class LyricLineEntry
{
    public long StartMs { get; set; } = -1;
    public string Text { get; set; } = "";
}

/// <summary>Lyrics of one track as cached on the server (empty Lines = known to have none).</summary>
public sealed class LyricsEntry
{
    public bool Synced { get; set; }
    public bool Instrumental { get; set; }
    public List<LyricLineEntry> Lines { get; set; } = new();
    public string Lrc { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

/**
 * Lyrics from public sources: LRCLIB (synced LRC first, then plain), lyrics.ovh as a plain-text
 * fallback. Results are cached in the YtStore (found: forever, not found: 3 days) so a track is
 * looked up once. Only fixed HTTPS hosts are ever contacted; user input only lands in the query
 * string, escaped.
 */
public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(3, 3);
    private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"\s*[\(\[][^\)\]]*(feat|ft\.|with|remaster|radio edit|version|edit|live|mono|stereo|bonus)[^\)\]]*[\)\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DashSuffix = new(@"\s+-\s+.*(remaster|version|edit|live|mix|mono|stereo).*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly YtStore _store;

    public LyricsService(YtStore store) => _store = store;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NflixButler/0.6 (https://github.com/frenchyx24/nflix-butler)");
        return client;
    }

    public static string Key(string artist, string title) =>
        YtStore.Normalize(ArtistNames.Primary(artist)) + "|" + YtStore.Normalize(CleanTitle(title));

    /// <summary>Title without featuring/remaster/edit decorations (lookup only).</summary>
    public static string CleanTitle(string title)
    {
        var clean = Brackets.Replace(title ?? "", "");
        clean = DashSuffix.Replace(clean, "");
        return clean.Trim();
    }

    public async Task<LyricsEntry> GetAsync(string artist, string title, string album, int durationSeconds, CancellationToken ct)
    {
        artist = (artist ?? "").Trim();
        title = (title ?? "").Trim();
        if (artist.Length > 200) artist = artist[..200];
        if (title.Length > 300) title = title[..300];
        album = (album ?? "").Trim();
        if (album.Length > 300) album = album[..300];
        if (artist.Length == 0 || title.Length == 0) return new LyricsEntry();

        var key = Key(artist, title);
        var cached = _store.Lyrics(key);
        if (cached != null && (cached.Lines.Count > 0 || cached.Instrumental || DateTime.UtcNow - cached.AtUtc < TimeSpan.FromDays(3)))
            return cached;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = await FetchAsync(artist, title, album, durationSeconds, ct).ConfigureAwait(false);
            entry.AtUtc = DateTime.UtcNow;
            _store.PutLyrics(key, entry);
            _store.Save();
            return entry;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<LyricsEntry> FetchAsync(string artist, string title, string album, int duration, CancellationToken ct)
    {
        var primary = ArtistNames.Primary(artist);
        var cleanTitle = CleanTitle(title);
        LyricsEntry? plainCandidate = null;

        // 1. LRCLIB exact signature (artist + title + album + duration) gives the best synced match.
        foreach (var (a, t) in new[] { (primary, title), (primary, cleanTitle) }.Distinct())
        {
            var query = "artist_name=" + Uri.EscapeDataString(a) + "&track_name=" + Uri.EscapeDataString(t);
            if (album.Length > 0) query += "&album_name=" + Uri.EscapeDataString(album);
            if (duration > 0) query += "&duration=" + duration.ToString(CultureInfo.InvariantCulture);
            var found = FromLrclib(await GetJsonAsync("https://lrclib.net/api/get?" + query, ct).ConfigureAwait(false));
            if (found?.Synced == true || found?.Instrumental == true) return found;
            plainCandidate ??= found;
        }

        // 2. LRCLIB search: best synced hit by title/artist match and duration distance.
        var wantedTitle = YtStore.Normalize(cleanTitle);
        var wantedArtist = YtStore.Normalize(primary);
        var search = await GetJsonAsync("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(cleanTitle) + "&artist_name=" + Uri.EscapeDataString(primary), ct).ConfigureAwait(false);
        if (search is { ValueKind: JsonValueKind.Array } hits)
        {
            JsonElement? best = null;
            var bestScore = int.MinValue;
            foreach (var hit in hits.EnumerateArray())
            {
                var hitTitle = YtStore.Normalize(CleanTitle(Str(hit, "trackName")));
                var hitArtist = YtStore.Normalize(Str(hit, "artistName"));
                if (!(hitTitle == wantedTitle || hitTitle.StartsWith(wantedTitle, StringComparison.Ordinal) || wantedTitle.StartsWith(hitTitle, StringComparison.Ordinal))) continue;
                if (!(hitArtist.Contains(wantedArtist, StringComparison.Ordinal) || wantedArtist.Contains(hitArtist, StringComparison.Ordinal))) continue;
                var score = hitTitle == wantedTitle ? 50 : 20;
                if (Str(hit, "syncedLyrics").Length > 0) score += 100;
                if (duration > 0 && hit.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                {
                    var delta = Math.Abs(d.GetDouble() - duration);
                    if (delta > 15) continue;
                    score -= (int)delta;
                }
                if (score > bestScore) { bestScore = score; best = hit; }
            }
            if (best != null)
            {
                var found = FromLrclib(best.Value);
                if (found?.Synced == true) return found;
                plainCandidate ??= found;
            }
        }
        if (plainCandidate != null && plainCandidate.Lines.Count > 0) return plainCandidate;

        // 3. lyrics.ovh: plain text only.
        var ovh = await GetJsonAsync("https://api.lyrics.ovh/v1/" + Uri.EscapeDataString(primary) + "/" + Uri.EscapeDataString(cleanTitle), ct).ConfigureAwait(false);
        var text = ovh is { ValueKind: JsonValueKind.Object } o ? Str(o, "lyrics") : "";
        if (text.Trim().Length > 20)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
            // lyrics.ovh sometimes starts with a "Paroles de la chanson ... par ..." banner.
            if (lines.Count > 0 && lines[0].StartsWith("Paroles de la chanson", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(0);
            return Plain(string.Join('\n', lines));
        }
        return new LyricsEntry();
    }

    private static LyricsEntry? FromLrclib(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e) return null;
        if (e.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True)
            return new LyricsEntry { Instrumental = true };
        var synced = Str(e, "syncedLyrics");
        if (synced.Length > 0)
        {
            var parsed = ParseLrc(synced);
            if (parsed.Count > 0) return new LyricsEntry { Synced = true, Lines = parsed, Lrc = synced.Replace("\r\n", "\n").Trim() };
        }
        var plain = Str(e, "plainLyrics");
        return plain.Trim().Length > 0 ? Plain(plain) : null;
    }

    private static LyricsEntry Plain(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => new LyricLineEntry { Text = l.Trim() }).ToList();
        while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return new LyricsEntry { Synced = false, Lines = lines };
    }

    /// <summary>LRC → ordered lines; several stamps on one line give several entries.</summary>
    public static List<LyricLineEntry> ParseLrc(string lrc)
    {
        var result = new List<LyricLineEntry>();
        foreach (var raw in lrc.Replace("\r\n", "\n").Split('\n'))
        {
            var matches = Stamp.Matches(raw);
            if (matches.Count == 0) continue;
            var text = Stamp.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                var minutes = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                long fraction = 0;
                if (m.Groups[3].Success)
                {
                    var f = m.Groups[3].Value;
                    fraction = f.Length switch { 1 => long.Parse(f, CultureInfo.InvariantCulture) * 100, 2 => long.Parse(f, CultureInfo.InvariantCulture) * 10, _ => long.Parse(f[..3], CultureInfo.InvariantCulture) };
                }
                result.Add(new LyricLineEntry { StartMs = (minutes * 60 + seconds) * 1000 + fraction, Text = text });
            }
        }
        return result.OrderBy(l => l.StartMs).ToList();
    }

    /// <summary>Contents of a sidecar file for Jellyfin: LRC when synced, else plain text.</summary>
    public static (string Extension, string Content)? Sidecar(LyricsEntry entry)
    {
        if (entry.Synced && entry.Lines.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var line in entry.Lines)
            {
                var ts = TimeSpan.FromMilliseconds(line.StartMs);
                sb.Append('[').Append(((int)ts.TotalMinutes).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                  .Append(ts.Seconds.ToString("00", CultureInfo.InvariantCulture)).Append('.')
                  .Append((ts.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture)).Append(']')
                  .Append(line.Text).Append('\n');
            }
            return (".lrc", sb.ToString());
        }
        if (entry.Lines.Count > 0)
            return (".txt", string.Join('\n', entry.Lines.Select(l => l.Text)) + "\n");
        return null;
    }

    private static async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 2_000_000) return null;
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
