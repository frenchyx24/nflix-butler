using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NflixButler.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Api;

/**
 * Online music catalogue for the Nflix app: clean metadata and square covers (catalogue), audio
 * resolved and proxied server-side, and background downloads that file tracks into the music
 * library with tags and artwork. Every route needs a signed-in user; downloads need an
 * administrator unless AllowUserDownloads is on.
 */
[ApiController]
[Authorize]
[Route("Plugins/NflixButler/music")]
public class MusicController : ControllerBase
{
    private static readonly Regex IdRx = new("^[0-9]{1,20}$", RegexOptions.Compiled);

    private readonly MusicCatalog _catalog;
    private readonly MusicLibraryService _music;
    private readonly YtMusicService _yt;
    private readonly ILogger<MusicController> _logger;
    private readonly LyricsService _lyrics;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime At, object Value)> FeedCache = new();

    public MusicController(MusicCatalog catalog, MusicLibraryService music, YtMusicService yt, LyricsService lyrics, ILibraryManager library, IUserManager users, ILogger<MusicController> logger)
    {
        _lyrics = lyrics;
        _library = library;
        _users = users;
        _catalog = catalog;
        _music = music;
        _yt = yt;
        _logger = logger;
    }

    private static bool IsId(string? value) => value != null && IdRx.IsMatch(value);

    private object TrackJson(CatalogTrack t)
    {
        var itemId = _music.ItemOf(t.Id);
        var job = _music.Job(t.Id);
        return new
        {
            id = t.Id,
            title = t.Title,
            artist = t.Artist,
            artistId = t.ArtistId,
            album = t.Album,
            albumId = t.AlbumId,
            cover = t.Cover,
            duration = t.Duration,
            trackNumber = t.TrackNumber,
            itemId = itemId?.ToString("N"),
            state = itemId != null || _music.FileOf(t.Id) != null ? "done" : job?.State ?? "idle",
        };
    }

    private static object AlbumJson(CatalogAlbum a) => new
    {
        id = a.Id,
        title = a.Title,
        artist = a.Artist,
        artistId = a.ArtistId,
        cover = a.Cover,
        year = a.Year,
        type = a.Type,
        trackCount = a.TrackCount,
    };

    private static object ArtistJson(CatalogArtist a) => new
    {
        id = a.Id,
        name = a.Name,
        picture = a.Picture,
        fans = a.Fans,
        albumCount = a.AlbumCount,
    };

    private IActionResult Upstream(Exception error)
    {
        if (error is OperationCanceledException && HttpContext.RequestAborted.IsCancellationRequested)
            return new EmptyResult();
        _logger.LogWarning("music catalogue: {Path} failed: {Message}", Request.Path.Value, error.Message);
        return StatusCode(502, new { message = "Catalogue musical indisponible, réessayez." });
    }

    private bool MayDownload() =>
        CallerIdentity.IsAdmin(User) || Plugin.Instance?.Configuration.AllowUserDownloads == true;

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { tracks = Array.Empty<object>(), albums = Array.Empty<object>(), artists = Array.Empty<object>() });
        try
        {
            var (tracks, albums, artists) = await _catalog.SearchAsync(q, limit, ct);
            _yt.Store.RecordUserSearch(CallerIdentity.HistoryKey(User), q);
            _yt.Store.Save();
            return Ok(new
            {
                tracks = tracks.Select(TrackJson),
                albums = albums.Select(AlbumJson),
                artists = artists.Select(ArtistJson),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /** Artist page: by catalogue id, or by name (best match) for library artists and actors. */
    [HttpGet("artist")]
    public async Task<IActionResult> Artist([FromQuery] string? id, [FromQuery] string? name, CancellationToken ct = default)
    {
        try
        {
            CatalogArtist? artist = null;
            if (IsId(id)) artist = await _catalog.ArtistAsync(id!, ct);
            else if (!string.IsNullOrWhiteSpace(name) && name.Length <= 200) artist = await _catalog.FindArtistAsync(name, ct);
            if (artist == null || artist.Id.Length == 0) return NotFound(new { message = "Artiste introuvable." });
            var top = _catalog.ArtistTopAsync(artist.Id, 20, ct);
            var albums = _catalog.ArtistAlbumsAsync(artist.Id, ct);
            var related = _catalog.RelatedAsync(artist.Id, ct);
            await Task.WhenAll(top, albums, related.ContinueWith(_ => { }, TaskScheduler.Default));
            return Ok(new
            {
                artist = ArtistJson(artist),
                top = top.Result.Select(TrackJson),
                albums = albums.Result.Select(AlbumJson),
                related = related.IsCompletedSuccessfully ? related.Result.Select(ArtistJson) : Enumerable.Empty<object>(),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    [HttpGet("album")]
    public async Task<IActionResult> Album([FromQuery] string id, CancellationToken ct = default)
    {
        if (!IsId(id)) return BadRequest(new { message = "id invalide" });
        try
        {
            var album = await _catalog.AlbumAsync(id, ct);
            return Ok(new
            {
                album = AlbumJson(album),
                tracks = album.Tracks.Select(TrackJson),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /** Plays a catalogue track: resolves its audio once, then proxies it (Range supported). */
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string trackId, CancellationToken ct)
    {
        if (!IsId(trackId)) return BadRequest(new { message = "trackId invalide" });
        string videoId;
        try
        {
            videoId = _yt.Store.CatalogVideo(trackId)
                ?? await _music.ResolveAsync(await _catalog.TrackAsync(trackId, ct), ct);
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
        _yt.Touch(videoId, CallerIdentity.HistoryKey(User));
        return await YtMusicController.ProxyAudio(this, _yt, videoId, ct);
    }

    public sealed class DownloadRequest
    {
        public List<string> TrackIds { get; set; } = new();
        public string AlbumId { get; set; } = "";
    }

    /** Queues tracks (or a whole album) for the library. Answers at once; poll /states. */
    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] DownloadRequest request, CancellationToken ct)
    {
        if (!MayDownload()) return StatusCode(403, new { message = "Ajout à la bibliothèque réservé aux administrateurs." });
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.YtMusicFolder))
            return StatusCode(412, new { message = "Aucun dossier musique configuré." });
        var ids = (request?.TrackIds ?? new List<string>()).Where(IsId).Distinct().Take(300).ToList();
        var albumId = request?.AlbumId ?? "";
        if (ids.Count == 0 && !IsId(albumId)) return BadRequest(new { message = "trackIds ou albumId requis" });
        try
        {
            var tracks = new List<CatalogTrack>();
            if (IsId(albumId)) tracks.AddRange((await _catalog.AlbumAsync(albumId, ct)).Tracks);
            foreach (var id in ids.Where(id => tracks.All(t => t.Id != id)))
                tracks.Add(await _catalog.TrackAsync(id, ct));
            var queued = _music.Enqueue(tracks);
            return Ok(new { queued, total = tracks.Count, ids = tracks.Select(t => t.Id) });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /** Library / queue state of catalogue tracks, one call per list. */
    [HttpGet("states")]
    public IActionResult States([FromQuery] string? ids)
    {
        var result = new Dictionary<string, object>();
        foreach (var id in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsId).Distinct().Take(300))
        {
            var itemId = _music.ItemOf(id);
            var job = _music.Job(id);
            var state = itemId != null || _music.FileOf(id) != null ? "done" : job?.State ?? "idle";
            result[id] = new { state, itemId = itemId?.ToString("N"), message = job?.Message ?? "" };
        }
        return Ok(new { states = result, canDownload = MayDownload() });
    }

    /**
     * Online suggestions for the music home: "Pour toi" (top tracks of the artists the user
     * listens to), artist mixes (radio), new releases, similar artists and trends. Square covers
     * come with the catalogue. Cached 30 min per user.
     */
    [HttpGet("feed")]
    public async Task<IActionResult> Feed([FromQuery] bool refresh = false, CancellationToken ct = default)
    {
        var userId = CallerIdentity.UserId(User);
        var now = DateTime.UtcNow;
        if (!refresh && FeedCache.TryGetValue(userId, out var cached) && now - cached.At < TimeSpan.FromMinutes(30))
            return Ok(cached.Value);
        try
        {
            var seeds = HeardArtists(userId, 6);
            var artists = new List<CatalogArtist>();
            foreach (var name in seeds)
            {
                if (artists.Count >= 4) break;
                try
                {
                    var found = await _catalog.FindArtistAsync(name, ct);
                    if (found != null && found.Id.Length > 0 && ArtistNames.Key(found.Name) == ArtistNames.Key(name) && artists.All(a => a.Id != found.Id))
                        artists.Add(found);
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }

            var releases = _catalog.NewReleasesAsync(16, ct);
            var chart = _catalog.ChartTracksAsync(20, ct);
            var tops = artists.Select(a => _catalog.ArtistTopAsync(a.Id, 6, ct)).ToList();
            var radios = artists.Take(3).Select(a => _catalog.ArtistRadioAsync(a.Id, 25, ct)).ToList();
            var related = artists.Count > 0 ? _catalog.RelatedAsync(artists[0].Id, ct) : Task.FromResult(new List<CatalogArtist>());
            async Task<T?> Safe<T>(Task<T> task) where T : class { try { return await task; } catch (Exception error) when (error is not OperationCanceledException) { return null; } }

            var sections = new List<object>();
            // Pour toi: one track of each heard artist in turn, then their radios.
            var forYou = new List<CatalogTrack>();
            var topLists = new List<List<CatalogTrack>>();
            foreach (var t in tops) topLists.Add(await Safe(t) ?? new List<CatalogTrack>());
            var radioLists = new List<List<CatalogTrack>>();
            foreach (var r in radios) radioLists.Add(await Safe(r) ?? new List<CatalogTrack>());
            for (var i = 0; i < 6; i++) foreach (var list in topLists) if (i < list.Count) forYou.Add(list[i]);
            var heardKeys = artists.Select(a => ArtistNames.Key(a.Name)).ToHashSet();
            foreach (var list in radioLists) forYou.AddRange(list.Where(t => !heardKeys.Contains(ArtistNames.Key(t.Artist))).Take(4));
            forYou = forYou.GroupBy(t => t.Id).Select(g => g.First()).Take(24).ToList();
            if (forYou.Count > 0)
                sections.Add(new { key = "for-you", title = "Pour toi", subtitle = "D'après vos écoutes", type = "tracks", tracks = forYou.Select(TrackJson) });

            var mixes = new List<object>();
            for (var i = 0; i < radioLists.Count; i++)
            {
                if (radioLists[i].Count == 0) continue;
                mixes.Add(new
                {
                    id = artists[i].Id,
                    title = "Mix " + artists[i].Name,
                    subtitle = string.Join(", ", radioLists[i].Select(t => t.Artist).Where(a => a.Length > 0).Distinct().Take(3)) + " et plus",
                    cover = artists[i].Picture,
                    tracks = radioLists[i].Select(TrackJson),
                });
            }
            if (mixes.Count > 0)
                sections.Add(new { key = "mixes", title = "Vos mix du jour", subtitle = "Basés sur vos artistes", type = "mixes", mixes });

            // Latest releases of the user's own artists first, then the general ones.
            var ownLatest = new List<CatalogAlbum>();
            var minYear = (DateTime.UtcNow.Year - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var a in artists.Take(4))
            {
                var albums = await Safe(_catalog.ArtistAlbumsAsync(a.Id, ct));
                if (albums != null) ownLatest.AddRange(albums.Where(al => string.CompareOrdinal(al.Year, minYear) >= 0).Take(2));
            }
            var newAlbums = ownLatest.OrderByDescending(al => al.Year).Concat(await Safe(releases) ?? new List<CatalogAlbum>())
                .GroupBy(al => al.Id).Select(g => g.First()).Take(20).ToList();
            if (newAlbums is { Count: > 0 })
                sections.Add(new { key = "releases", title = "Nouveautés", subtitle = "Les sorties du moment", type = "albums", albums = newAlbums.Select(AlbumJson) });

            var similar = await Safe(related);
            if (similar is { Count: > 0 } && artists.Count > 0)
                sections.Add(new { key = "similar", title = "Artistes similaires à " + artists[0].Name, subtitle = "", type = "artists", artists = similar.Select(ArtistJson) });

            var trends = await Safe(chart);
            if (trends is { Count: > 0 })
                sections.Add(new { key = "trends", title = "Tendances", subtitle = "Les titres les plus écoutés", type = "tracks", tracks = trends.Select(TrackJson) });

            var payload = new { sections };
            if (FeedCache.Count > 500) FeedCache.Clear();
            FeedCache[userId] = (now, payload);
            return Ok(payload);
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /// <summary>Main artists the user listens to: Jellyfin play history first, then online listens.</summary>
    private List<string> HeardArtists(Guid userId, int limit)
    {
        var names = new List<string>();
        if (userId != Guid.Empty)
        {
            try
            {
                var user = _users.GetUserById(userId);
                if (user != null)
                {
                    var query = new InternalItemsQuery(user)
                    {
                        Recursive = true,
                        IncludeItemTypes = new[] { BaseItemKind.Audio },
                        IsPlayed = true,
                        Limit = 200,
                        OrderBy = new[] { (ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) },
                    };
                    foreach (var item in _library.GetItemList(query))
                        if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
                            names.AddRange((audio.AlbumArtists?.Count > 0 ? audio.AlbumArtists : audio.Artists ?? Array.Empty<string>()).Take(1));
                }
            }
            catch (Exception error) { _logger.LogDebug("music feed: play history unavailable: {Message}", error.Message); }
            var recent = _yt.Store.Items(_yt.Store.UserRecentList(userId.ToString("N"), 40).Select(r => r.VideoId));
            names.AddRange(recent.Where(i => i.Artist.Length > 0).Select(i => i.Artist));
        }
        if (names.Count == 0)
        {
            // New user: the library's own artists.
            try
            {
                var query = new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { BaseItemKind.Audio }, Limit = 200, OrderBy = new[] { (ItemSortBy.DateCreated, Jellyfin.Database.Implementations.Enums.SortOrder.Descending) } };
                foreach (var item in _library.GetItemList(query))
                    if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
                        names.AddRange((audio.AlbumArtists?.Count > 0 ? audio.AlbumArtists : audio.Artists ?? Array.Empty<string>()).Take(1));
            }
            catch { }
        }
        return names.Select(ArtistNames.Primary).Where(n => n.Length > 1)
            .GroupBy(ArtistNames.Key).OrderByDescending(g => g.Count())
            .Select(g => g.First()).Take(limit).ToList();
    }

    /**
     * Lyrics of a track, synced when a source has them. Works for library and online tracks alike
     * (lookup by artist + title, album and duration refine the match). Cached server-side.
     */
    [HttpGet("lyrics")]
    public async Task<IActionResult> Lyrics([FromQuery] string? artist, [FromQuery] string? title, [FromQuery] string? album, [FromQuery] int duration = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || artist.Length > 300 || title.Length > 300)
            return BadRequest(new { message = "artist et title requis" });
        try
        {
            var entry = await _lyrics.GetAsync(artist, title, album ?? "", Math.Clamp(duration, 0, 7200), ct);
            return Ok(new
            {
                synced = entry.Synced,
                instrumental = entry.Instrumental,
                lines = entry.Lines.Select(l => new { startMs = l.StartMs, text = l.Text }),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /**
     * Square cover for an artist + title, with the catalogue fallbacks (Deezer, iTunes, Cover Art
     * Archive). Used by the app for tracks that have no cover yet; cached server-side.
     */
    [HttpGet("cover")]
    public async Task<IActionResult> Cover([FromQuery] string? artist, [FromQuery] string? title, [FromQuery] string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || artist.Length > 300 || title.Length > 300)
            return BadRequest(new { message = "artist et title requis" });
        try
        {
            var cover = await _catalog.FindCoverAsync(artist, title, album ?? "", ct);
            return Ok(new { cover });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /**
     * Artist pictures for library artists that have none: names separated by "|", exact name
     * match only. Cached forever server-side; at most 20 new lookups per call (catalogue rate).
     */
    [HttpGet("pictures")]
    public async Task<IActionResult> Pictures([FromQuery] string? names, CancellationToken ct = default)
    {
        var wanted = (names ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length is > 0 and <= 200).Distinct(StringComparer.OrdinalIgnoreCase).Take(120).ToList();
        var result = new Dictionary<string, string>();
        var lookups = 0;
        foreach (var name in wanted)
        {
            var key = ArtistNames.Key(name);
            if (key.Length == 0) continue;
            var known = _yt.Store.ArtistPicture(key);
            if (known == null && lookups < 20)
            {
                lookups++;
                try
                {
                    var artist = await _catalog.FindArtistAsync(name, ct);
                    known = artist != null && ArtistNames.Key(artist.Name) == key && artist.Picture.StartsWith("https://", StringComparison.Ordinal)
                        && !artist.Picture.Contains("/artist//", StringComparison.Ordinal) ? artist.Picture : "";
                    _yt.Store.PutArtistPicture(key, known);
                }
                catch (Exception error) when (error is not OperationCanceledException) { known = null; }
            }
            if (!string.IsNullOrEmpty(known)) result[name] = known;
        }
        if (lookups > 0) _yt.Store.Save();
        return Ok(new { pictures = result });
    }

    /** Re-files the online-music folder (main artist only, tags, covers, lyrics). Admin only. */
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("migrate")]
    public async Task<IActionResult> Migrate(CancellationToken ct)
    {
        try
        {
            var files = await _music.MigrateAsync(ct);
            return Ok(new { count = files.Count, files = files.Select(f => new { from = f.From, to = f.To, artist = f.Artist, album = f.Album, matched = f.Matched, lyrics = f.Lyrics }) });
        }
        catch (InvalidOperationException error)
        {
            return StatusCode(409, new { message = error.Message });
        }
    }
}
