using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Services;
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

    public MusicController(MusicCatalog catalog, MusicLibraryService music, YtMusicService yt, ILogger<MusicController> logger)
    {
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
}
