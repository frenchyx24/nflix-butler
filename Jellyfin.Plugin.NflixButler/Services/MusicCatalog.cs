using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>One catalogue track (clean metadata, square album cover).</summary>
public sealed class CatalogTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public string Cover { get; set; } = "";
    public int Duration { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; } = 1;
    public string Isrc { get; set; } = "";
}

public sealed class CatalogAlbum
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string Cover { get; set; } = "";
    public string Year { get; set; } = "";
    public string Type { get; set; } = "album"; // album | single | ep | compile
    public int TrackCount { get; set; }
    public List<CatalogTrack> Tracks { get; set; } = new();
}

public sealed class CatalogArtist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Picture { get; set; } = "";
    public int Fans { get; set; }
    public int AlbumCount { get; set; }
}

/**
 * Music metadata catalogue backed by the public Deezer API (no account, no key): clean titles,
 * album/artist links, square covers and artist pictures. Audio itself is resolved separately
 * (see MusicResolver). Responses are cached in memory; the API allows ~50 requests / 5 s, so a
 * small gate keeps bursts (album pages, feeds) well under it.
 */
public sealed class MusicCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), BaseAddress = new Uri("https://api.deezer.com/") };
    private static readonly SemaphoreSlim Gate = new(6, 6);
    private static readonly ConcurrentDictionary<string, (DateTime At, string Json)> Cache = new();

    private static async Task<JsonElement> GetAsync(string path, TimeSpan ttl, CancellationToken ct)
    {
        if (Cache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.At < ttl)
            return JsonDocument.Parse(hit.Json).RootElement.Clone();
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var text = await Http.GetStringAsync(path, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException("catalogue: error on " + path.Split('?')[0]);
            if (Cache.Count > 5000) Cache.Clear();
            Cache[path] = (DateTime.UtcNow, text);
            return doc.RootElement.Clone();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static IEnumerable<JsonElement> Data(JsonElement root, string name = "data") =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string Cover(JsonElement album) =>
        Str(album, "cover_xl") is { Length: > 0 } xl ? xl : Str(album, "cover_big");

    private static CatalogTrack Track(JsonElement t, JsonElement? albumOverride = null)
    {
        var album = albumOverride ?? Obj(t, "album");
        var artist = Obj(t, "artist");
        return new CatalogTrack
        {
            Id = Str(t, "id"),
            Title = Str(t, "title"),
            Artist = Str(artist, "name"),
            ArtistId = Str(artist, "id"),
            Album = Str(album, "title"),
            AlbumId = Str(album, "id"),
            Cover = Cover(album),
            Duration = Int(t, "duration"),
            TrackNumber = Int(t, "track_position"),
            DiscNumber = Math.Max(1, Int(t, "disk_number")),
            Isrc = Str(t, "isrc"),
        };
    }

    private static CatalogAlbum Album(JsonElement a, JsonElement? artistOverride = null)
    {
        var artist = artistOverride ?? Obj(a, "artist");
        var date = Str(a, "release_date");
        return new CatalogAlbum
        {
            Id = Str(a, "id"),
            Title = Str(a, "title"),
            Artist = Str(artist, "name"),
            ArtistId = Str(artist, "id"),
            Cover = Cover(a),
            Year = date.Length >= 4 ? date[..4] : "",
            Type = Str(a, "record_type") is { Length: > 0 } rt ? rt : "album",
            TrackCount = Int(a, "nb_tracks"),
        };
    }

    private static CatalogArtist Artist(JsonElement a) => new()
    {
        Id = Str(a, "id"),
        Name = Str(a, "name"),
        Picture = Str(a, "picture_xl") is { Length: > 0 } p ? p : Str(a, "picture_big"),
        Fans = Int(a, "nb_fan"),
        AlbumCount = Int(a, "nb_album"),
    };

    public async Task<(List<CatalogTrack> Tracks, List<CatalogAlbum> Albums, List<CatalogArtist> Artists)> SearchAsync(string query, int limit, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length > 200) query = query[..200];
        var q = Uri.EscapeDataString(query);
        limit = Math.Clamp(limit, 1, 50);
        var ttl = TimeSpan.FromHours(6);
        var tracks = GetAsync("search/track?q=" + q + "&limit=" + limit, ttl, ct);
        var albums = GetAsync("search/album?q=" + q + "&limit=12", ttl, ct);
        var artists = GetAsync("search/artist?q=" + q + "&limit=6", ttl, ct);
        await Task.WhenAll(tracks, albums, artists).ConfigureAwait(false);
        return (
            Data(tracks.Result).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList(),
            Data(albums.Result).Select(a => Album(a)).Where(a => a.Id.Length > 0).ToList(),
            Data(artists.Result).Select(Artist).Where(a => a.Id.Length > 0).ToList());
    }

    public async Task<CatalogArtist> ArtistAsync(string id, CancellationToken ct) =>
        Artist(await GetAsync("artist/" + id, TimeSpan.FromHours(24), ct).ConfigureAwait(false));

    public async Task<List<CatalogTrack>> ArtistTopAsync(string id, int limit, CancellationToken ct)
    {
        var root = await GetAsync("artist/" + id + "/top?limit=" + Math.Clamp(limit, 1, 50), TimeSpan.FromHours(12), ct).ConfigureAwait(false);
        return Data(root).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList();
    }

    /** Whole discography (albums, EPs, singles), newest first. */
    public async Task<List<CatalogAlbum>> ArtistAlbumsAsync(string id, CancellationToken ct)
    {
        var all = new List<CatalogAlbum>();
        for (var index = 0; index < 500; index += 100)
        {
            var root = await GetAsync("artist/" + id + "/albums?limit=100&index=" + index, TimeSpan.FromHours(12), ct).ConfigureAwait(false);
            var page = Data(root).Select(a => Album(a)).Where(a => a.Id.Length > 0).ToList();
            all.AddRange(page);
            if (page.Count < 100) break;
        }
        return all.GroupBy(a => a.Id).Select(g => g.First())
            .OrderByDescending(a => a.Year).ToList();
    }

    public async Task<List<CatalogArtist>> RelatedAsync(string id, CancellationToken ct)
    {
        var root = await GetAsync("artist/" + id + "/related?limit=12", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        return Data(root).Select(Artist).Where(a => a.Id.Length > 0).ToList();
    }

    public async Task<CatalogAlbum> AlbumAsync(string id, CancellationToken ct)
    {
        var root = await GetAsync("album/" + id, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var album = Album(root);
        if (root.TryGetProperty("tracks", out var tracks))
        {
            var position = 0;
            foreach (var t in Data(tracks))
            {
                position++;
                var track = Track(t, root);
                if (track.TrackNumber == 0) track.TrackNumber = position;
                album.Tracks.Add(track);
            }
        }
        // The album endpoint lists at most 25 tracks inline: page the rest.
        if (album.TrackCount > album.Tracks.Count)
        {
            for (var index = album.Tracks.Count; index < Math.Min(album.TrackCount, 300); index += 50)
            {
                var more = await GetAsync("album/" + id + "/tracks?limit=50&index=" + index, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
                var page = Data(more).ToList();
                foreach (var t in page)
                {
                    var track = Track(t, root);
                    if (track.TrackNumber == 0) track.TrackNumber = album.Tracks.Count + 1;
                    album.Tracks.Add(track);
                }
                if (page.Count == 0) break;
            }
        }
        foreach (var t in album.Tracks)
        {
            if (t.Artist.Length == 0) { t.Artist = album.Artist; t.ArtistId = album.ArtistId; }
        }
        return album;
    }

    public async Task<CatalogTrack> TrackAsync(string id, CancellationToken ct)
    {
        var root = await GetAsync("track/" + id, TimeSpan.FromDays(7), ct).ConfigureAwait(false);
        return Track(root);
    }

    /** Best artist for a name (exact normalized match first, otherwise the most followed). */
    public async Task<CatalogArtist?> FindArtistAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var root = await GetAsync("search/artist?q=" + Uri.EscapeDataString(name.Trim()) + "&limit=10", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var list = Data(root).Select(Artist).ToList();
        var wanted = Norm(name);
        return list.Where(a => Norm(a.Name) == wanted).OrderByDescending(a => a.Fans).FirstOrDefault()
            ?? list.FirstOrDefault();
    }

    /// <summary>Most played tracks right now (public chart).</summary>
    public async Task<List<CatalogTrack>> ChartTracksAsync(int limit, CancellationToken ct)
    {
        var root = await GetAsync("chart/0/tracks?limit=" + Math.Clamp(limit, 1, 50), TimeSpan.FromHours(3), ct).ConfigureAwait(false);
        return Data(root).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList();
    }

    /// <summary>New releases picked by the editors (albums).</summary>
    public async Task<List<CatalogAlbum>> NewReleasesAsync(int limit, CancellationToken ct)
    {
        var root = await GetAsync("editorial/0/releases?limit=" + Math.Clamp(limit, 1, 50), TimeSpan.FromHours(6), ct).ConfigureAwait(false);
        var list = Data(root).Select(a => Album(a)).Where(a => a.Id.Length > 0).ToList();
        if (list.Count > 0) return list;
        // The editorial list is sometimes empty: fall back to the album chart.
        root = await GetAsync("chart/0/albums?limit=" + Math.Clamp(limit, 1, 50), TimeSpan.FromHours(6), ct).ConfigureAwait(false);
        return Data(root).Select(a => Album(a)).Where(a => a.Id.Length > 0).ToList();
    }

    /// <summary>Artist radio: tracks by this artist and similar ones.</summary>
    public async Task<List<CatalogTrack>> ArtistRadioAsync(string id, int limit, CancellationToken ct)
    {
        var root = await GetAsync("artist/" + id + "/radio?limit=" + Math.Clamp(limit, 1, 50), TimeSpan.FromHours(6), ct).ConfigureAwait(false);
        return Data(root).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList();
    }

    /// <summary>Best catalogue track for an artist + title (title match required).</summary>
    public async Task<CatalogTrack?> FindTrackAsync(string artist, string title, CancellationToken ct)
    {
        var primary = ArtistNames.Primary(artist);
        var cleanTitle = LyricsService.CleanTitle(title);
        if (cleanTitle.Length == 0) return null;
        var q = primary.Length > 0
            ? "artist:\"" + primary.Replace("\"", "") + "\" track:\"" + cleanTitle.Replace("\"", "") + "\""
            : cleanTitle;
        var root = await GetAsync("search/track?q=" + Uri.EscapeDataString(q) + "&limit=10", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
        var list = Data(root).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList();
        if (list.Count == 0 && primary.Length > 0)
        {
            root = await GetAsync("search/track?q=" + Uri.EscapeDataString(primary + " " + cleanTitle) + "&limit=10", TimeSpan.FromHours(24), ct).ConfigureAwait(false);
            list = Data(root).Select(t => Track(t)).Where(t => t.Id.Length > 0).ToList();
        }
        var wanted = Norm(cleanTitle);
        var wantedArtist = ArtistNames.Key(primary);
        return list
            .Where(t => { var n = Norm(LyricsService.CleanTitle(t.Title)); return n == wanted || n.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(n, StringComparison.Ordinal); })
            .OrderByDescending(t => wantedArtist.Length > 0 && ArtistNames.Key(t.Artist) == wantedArtist)
            .ThenByDescending(t => Norm(LyricsService.CleanTitle(t.Title)) == wanted)
            .FirstOrDefault();
    }

    internal static string Norm(string value) => Api.SyncController.Normalize(value);
}
