using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NflixButler.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Api;

/**
 * YouTube Music for the Nflix app, all backed by the persistent server-side cache (YtStore):
 * search (24 h), full item metadata (forever), recents, artist/album pages, and download into
 * the music library so a YT track becomes a real Jellyfin item (playlists, offline, artwork).
 * Any authenticated Jellyfin user may use them; the app never sees signed Google URLs.
 */
[ApiController]
[Authorize]
[Route("Plugins/NflixButler/yt")]
public class YtMusicController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, (DateTime At, string Url)> UrlCache = new();
    private static readonly SemaphoreSlim StreamGate = new(4, 4);
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    private readonly YtMusicService _yt;
    private readonly IProviderManager _providers;
    private readonly ILibraryManager _library;
    private readonly IDirectoryService _directoryService;
    private readonly IUserManager _users;
    private readonly ILogger<YtMusicController> _logger;
    private readonly MusicCatalog _catalog;
    private readonly MusicLibraryService _music;

    public YtMusicController(YtMusicService yt, IProviderManager providers, ILibraryManager library, IDirectoryService directoryService, IUserManager users, MusicCatalog catalog, MusicLibraryService music, ILogger<YtMusicController> logger)
    {
        _catalog = catalog;
        _music = music;
        _yt = yt;
        _providers = providers;
        _library = library;
        _directoryService = directoryService;
        _users = users;
        _logger = logger;
    }

    private object HitJson(YtHit h) => new
    {
        kind = h.Kind,
        videoId = h.VideoId,
        browseId = h.BrowseId,
        title = h.Title,
        subtitle = h.Subtitle,
        thumbnail = h.VideoId.Length > 0 ? CoverOf(_yt.Store.Item(h.VideoId), h.VideoId) : null,
    };

    /// <summary>Square catalogue cover when known, a square platform picture otherwise: never a video still.</summary>
    private static string? CoverOf(YtItem? item, string videoId) =>
        item != null && ImageUrl(item.Cover) ? item.Cover
        : item != null && ImageUrl(item.Thumbnail) ? item.Thumbnail
        : null;

    private static bool ImageUrl(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) && !IsVideoStill(url);

    /** Video stills are never shown: a track either has a square catalogue cover or no image. */
    private static bool IsVideoStill(string url) =>
        url.Contains("ytimg.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("ggpht.com", StringComparison.OrdinalIgnoreCase);

    private static object ItemJson(YtItem i, bool squareOnly = false) => new
    {
        videoId = i.VideoId,
        title = i.Title,
        artist = i.Artist,
        album = i.Album,
        albumBrowseId = i.AlbumBrowseId,
        channelId = i.ChannelId,
        channelTitle = i.ChannelTitle,
        duration = i.Duration,
        thumbnail = squareOnly
            ? i.Cover.StartsWith("https://", StringComparison.Ordinal) ? i.Cover
                : i.Thumbnail.StartsWith("https://", StringComparison.Ordinal) && !IsVideoStill(i.Thumbnail) ? i.Thumbnail : null
            : CoverOf(i, i.VideoId),
        downloaded = i.DownloadedPath.Length > 0,
    };

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 12, CancellationToken ct = default)
    {
        // No per-IP throttle: behind the reverse proxy every user shares one address, and the
        // client already debounces. YtMusicService serialises yt-dlp runs (2 at a time) and the
        // 24 h search cache keeps the real load low.
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { results = Array.Empty<object>(), albums = Array.Empty<object>(), artists = Array.Empty<object>() });

        try
        {
            var hits = await _yt.SearchAsync(q, limit, ct);
            _yt.Store.RecordUserSearch(CallerIdentity.HistoryKey(User), q);
            return Ok(new
            {
                results = hits.Where(h => h.Kind == "track").Select(HitJson),
                albums = hits.Where(h => h.Kind == "album").Select(HitJson),
                artists = hits.Where(h => h.Kind == "artist").Select(HitJson),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /// <summary>Full metadata of one track; fetched from YouTube once, then served from the server cache.</summary>
    [HttpGet("info")]
    public async Task<IActionResult> Info([FromQuery] string videoId, CancellationToken ct = default)
    {
        if (!CallerIdentity.IsVideoId(videoId))
            return BadRequest(new { message = "videoId invalide" });
        try
        {
            var item = await _yt.InfoAsync(videoId, ct);
            await ResolveCoverAsync(item, ct);
            return Ok(ItemJson(item));
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /// <summary>Past searches, most recent first.</summary>
    [HttpGet("history")]
    public IActionResult History([FromQuery] int limit = 20) =>
        Ok(new
        {
            searches = _yt.Store.UserSearchList(CallerIdentity.HistoryKey(User), limit).Select(s => new { query = s.Query, atUtc = s.AtUtc }),
        });

    /// <summary>Recently played/seen tracks with their cached metadata, square covers only.</summary>
    [HttpGet("recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var recents = _yt.Store.UserRecentList(CallerIdentity.HistoryKey(User), Math.Clamp(limit, 1, 100));
        var items = _yt.Store.Items(recents.Select(r => r.VideoId)).ToList();
        var pending = items.Where(i => !i.Cover.StartsWith("https://", StringComparison.Ordinal)).Take(12).ToList();
        using (var gate = new SemaphoreSlim(3, 3))
        {
            await Task.WhenAll(pending.Select(async item =>
            {
                await gate.WaitAsync(ct);
                try { await ResolveCoverAsync(item, ct); }
                catch (OperationCanceledException) { }
                catch (Exception error) { _logger.LogDebug("recent cover: {Message}", error.Message); }
                finally { gate.Release(); }
            }));
        }
        return Ok(new { items = items.Select(i => ItemJson(i, squareOnly: true)) });
    }

    /// <summary>Artist page: top tracks for an artist name (YT Music search, ranked).</summary>
    [HttpGet("artist")]
    public async Task<IActionResult> Artist([FromQuery] string name, [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "name requis" });
        try
        {
            var hits = await _yt.SearchAsync(name, limit, ct);
            return Ok(new
            {
                results = hits.Where(h => h.Kind == "track").Select(HitJson),
                albums = hits.Where(h => h.Kind == "album").Select(HitJson),
                artists = hits.Where(h => h.Kind == "artist").Select(HitJson),
            });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /// <summary>Tracks of an album (MPREb_ browse id).</summary>
    [HttpGet("album")]
    public async Task<IActionResult> Album([FromQuery] string browseId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!CallerIdentity.IsBrowseId(browseId)) return BadRequest(new { message = "browseId invalide" });
        try
        {
            var hits = await _yt.AlbumAsync(browseId, limit, ct);
            return Ok(new { results = hits.Select(HitJson) });
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    /**
     * Which of these tracks already sit in the music library. One call per list, so every row can
     * show its state without asking for the full metadata of each track.
     */
    [HttpGet("downloaded")]
    public IActionResult Downloaded([FromQuery] string? ids)
    {
        var wanted = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(CallerIdentity.IsVideoId)
            .Distinct()
            .Take(200);
        var present = new List<string>();
        foreach (var id in wanted)
        {
            var item = _yt.Store.Item(id);
            if (item != null && item.DownloadedPath.Length > 0 && System.IO.File.Exists(item.DownloadedPath))
                present.Add(id);
        }
        return Ok(new { ids = present });
    }

    /// <summary>Proxies the audio stream of one video; Range forwarded so the player can seek.</summary>
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string videoId, CancellationToken ct)
    {
        if (!CallerIdentity.IsVideoId(videoId))
            return BadRequest(new { message = "videoId invalide" });

        _yt.Touch(videoId, CallerIdentity.HistoryKey(User));
        return await ProxyAudio(this, _yt, videoId, ct);
    }

    /// <summary>Audio proxy shared with the catalogue routes: Range forwarded, signed URL never exposed.</summary>
    internal static async Task<IActionResult> ProxyAudio(ControllerBase controller, YtMusicService yt, string videoId, CancellationToken ct)
    {
        var Request = controller.Request;
        var Response = controller.Response;
        var HttpContext = controller.HttpContext;

        await StreamGate.WaitAsync(ct);
        try
        {
            if (!UrlCache.TryGetValue(videoId, out var hit) || DateTime.UtcNow - hit.At > TimeSpan.FromMinutes(5))
            {
                hit = (DateTime.UtcNow, await yt.StreamUrlAsync(videoId, ct));
                if (UrlCache.Count > 2000) UrlCache.Clear();
                UrlCache[videoId] = hit;
            }
            var upstream = new HttpRequestMessage(HttpMethod.Get, hit.Url);
            upstream.Headers.TryAddWithoutValidation("User-Agent",
                "com.google.android.youtube/20.10.38 (Linux; U; Android 14) gzip");
            if (Request.Headers.TryGetValue("Range", out var range) && System.Net.Http.Headers.RangeHeaderValue.TryParse(range.ToString(), out var parsed))
                upstream.Headers.Range = parsed;
            var response = await Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Expired or refused signed URL: forget it so the next try resolves a fresh one.
                UrlCache.TryRemove(videoId, out _);
                response.Dispose();
                return controller.StatusCode(400, new { message = "Source audio indisponible (" + (int)response.StatusCode + ")." });
            }

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "audio/mp4";
            foreach (var header in response.Headers)
            {
                if (header.Key is "Content-Length" or "Content-Range" or "Accept-Ranges")
                    Response.Headers[header.Key] = string.Join(",", header.Value);
            }
            foreach (var header in response.Content.Headers)
            {
                if (header.Key is "Content-Length" or "Content-Range" or "Accept-Ranges")
                    Response.Headers[header.Key] = string.Join(",", header.Value);
            }
            Response.Headers["Accept-Ranges"] = "bytes";
            // The response body streams after this action returns: dispose the upstream message then.
            HttpContext.Response.RegisterForDispose(response);
            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            return new FileStreamResult(upstreamStream, Response.ContentType);
        }
        catch (Exception)
        {
            return controller.StatusCode(400, new { message = "Source musicale indisponible." });
        }
        finally
        {
            StreamGate.Release();
        }
    }

    /// <summary>
    /// Personalized feed: artists from the user's recent Jellyfin plays and recent online listens,
    /// each expanded into a top-tracks section. Cached 15 min in memory.
    /// </summary>
    [HttpGet("feed")]
    public async Task<IActionResult> Feed(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var userId = CallerUserId();
        if (userId != Guid.Empty && FeedCache.TryGetValue(userId, out var cached) && now - cached.At < TimeSpan.FromMinutes(15))
            return Ok(cached.Value);

        try
        {
            // 1. Artists the user actually listens to (Jellyfin play history, audio only).
            var playedArtists = new List<string>();
            if (userId != Guid.Empty) try
            {
                var query = new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    IsPlayed = true,
                    Limit = 200,
                    User = userId != Guid.Empty ? _users.GetUserById(userId) : null,
                };
                foreach (var item in _library.GetItemList(query))
                {
                    if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
                        playedArtists.AddRange(audio.Artists ?? Array.Empty<string>());
                }
            }
            catch (Exception error) { _logger.LogWarning(error, "feed: play history unavailable"); }

            // 2. Artists from recent online listens (persistent cache).
            var recentItems = userId == Guid.Empty
                ? new List<YtItem>()
                : _yt.Store.Items(_yt.Store.UserRecentList(userId.ToString("N"), 30).Select(r => r.VideoId)).ToList();
            var recentArtists = recentItems.Where(i => i.Artist.Length > 0).Select(i => FirstArtist(i.Artist)).ToList();

            var artists = playedArtists.Concat(recentArtists)
                .Select(a => a.Trim()).Where(a => a.Length > 1)
                .GroupBy(a => a.ToLowerInvariant()).OrderByDescending(g => g.Count())
                .Select(g => g.First()).Take(4).ToList();

            var sections = new List<object>();
            foreach (var artist in artists)
            {
                var hits = (await _yt.SearchAsync(artist, 8, ct)).Where(h => h.Kind == "track").ToList();
                if (hits.Count == 0) continue;
                sections.Add(new
                {
                    title = artist,
                    subtitle = "Parce que vous l'écoutez",
                    tracks = hits.Select(HitJson),
                });
            }
            if (recentItems.Count > 0)
            {
                sections.Insert(0, new
                {
                    title = "Écoutés récemment",
                    subtitle = "Repris là où vous vous êtes arrêté",
                    tracks = recentItems.Select(i => new
                    {
                        kind = "track",
                        videoId = i.VideoId,
                        browseId = "",
                        title = i.Title,
                        subtitle = i.Artist,
                        thumbnail = CoverOf(i, i.VideoId),
                    }),
                });
            }
            var payload = new { sections };
            if (userId != Guid.Empty) FeedCache[userId] = (now, payload);
            return Ok(payload);
        }
        catch (Exception error)
        {
            return Upstream(error);
        }
    }

    private static readonly ConcurrentDictionary<Guid, (DateTime At, object Value)> FeedCache = new();

    /// <summary>Jellyfin user id of the caller, when the auth handler exposes one as a claim.</summary>
    private Guid CallerUserId()
    {
        return CallerIdentity.UserId(User);
    }

    /// <summary>Generic 502: the upstream error (which names the source site) goes to the log only.</summary>
    private IActionResult Upstream(Exception error)
    {
        if (error is OperationCanceledException && HttpContext.RequestAborted.IsCancellationRequested)
            return new EmptyResult();
        _logger.LogWarning("online music: {Path} failed: {Message}", Request.Path.Value, error.Message);
        return StatusCode(400, new { message = error is TimeoutException ? "Source trop lente, réessayez." : "Source musicale indisponible." });
    }

    private static string FirstArtist(string artist) => artist.Split(',')[0].Split(" feat")[0].Trim();

    public sealed class DownloadRequest
    {
        public string VideoId { get; set; } = "";
    }

    /**
     * Downloads one track (m4a, with embedded metadata) into the configured music folder and asks
     * Jellyfin to refresh that folder: the track then exists as a real library item, addable to
     * playlists and available offline. Admin only: it writes into the media library.
     */
    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] DownloadRequest request, CancellationToken ct)
    {
        if (request == null || !CallerIdentity.IsVideoId(request.VideoId))
            return BadRequest(new { message = "videoId invalide" });

        var config = Plugin.Instance?.Configuration;
        var folder = config?.YtMusicFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return StatusCode(412, new { message = "Aucun dossier musique configuré (YtMusicFolder)." });

        YtItem item;
        try
        {
            item = await _yt.InfoAsync(request.VideoId, ct);
        }
        catch (Exception error)
        {
            _logger.LogWarning("YT download metadata failed for {VideoId}: {Message}", request.VideoId, error.Message);
            return StatusCode(400, new { message = "Métadonnées introuvables." });
        }
        if (item.DownloadedPath.Length > 0 && System.IO.File.Exists(item.DownloadedPath))
        {
            _yt.Touch(request.VideoId, CallerIdentity.HistoryKey(User));
            return Ok(new { downloaded = true, alreadyPresent = true, path = item.DownloadedPath, title = item.Title, artist = item.Artist });
        }
        // Same pipeline as the catalogue: main artist folder, album, clean tags, square cover,
        // lyrics sidecar. Without a catalogue match the track is filed under its main artist.
        CatalogTrack? track = null;
        try { track = await _catalog.FindTrackAsync(item.Artist, item.Title, ct); } catch (Exception error) when (error is not OperationCanceledException) { }
        track ??= new CatalogTrack
        {
            Id = "yt-" + request.VideoId,
            Title = item.Title,
            Artist = ArtistNames.Primary(item.Artist),
            Album = item.Album,
            Duration = item.Duration,
        };
        if (track.Cover.Length == 0)
            track.Cover = await _catalog.FindCoverAsync(track.Artist, track.Title, track.Album, ct);
        string file;
        try
        {
            file = await _music.DownloadWithAudioAsync(track, request.VideoId, ct);
        }
        catch (Exception error)
        {
            _logger.LogWarning("YT download failed for {VideoId}: {Message}", request.VideoId, error.Message);
            return Upstream(error);
        }
        item = _yt.Store.Item(request.VideoId) ?? item;
        item.DownloadedPath = file;
        if (track.Cover.StartsWith("https://", StringComparison.Ordinal)) item.Cover = track.Cover;
        _yt.Store.PutItem(item);
        _yt.Store.Save();
        _yt.Touch(request.VideoId, CallerIdentity.HistoryKey(User));
        _logger.LogInformation("online download: {File}", file);
        return Ok(new { downloaded = true, path = file, title = track.Title, artist = track.Artist });
    }

    /// <summary>Finds the square album cover of an online track once, then caches it in the store.</summary>
    private async Task ResolveCoverAsync(YtItem item, CancellationToken ct)
    {
        if (item.Cover.Length > 0 || item.Title.Length == 0) return;
        try
        {
            var cover = await _catalog.FindCoverAsync(item.Artist, item.Title, item.Album, ct);
            if (cover.Length == 0 && item.Thumbnail.Contains("googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                cover = SquarePicture(item.Thumbnail);
            item.Cover = cover.Length > 0 ? cover : "-";
            if (item.Album.Length == 0)
            {
                var match = await _catalog.FindTrackAsync(item.Artist, item.Title, ct);
                if (match != null) item.Album = match.Album;
            }
            _yt.Store.PutItem(item);
            _yt.Store.Save();
        }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    /// <summary>Re-asks a googleusercontent picture at 1000x1000; platform album art is square.</summary>
    private static string SquarePicture(string url)
    {
        var cut = url.IndexOf('=');
        var baseUrl = cut > 0 ? url[..cut] : url;
        return baseUrl + "=w1000-h1000-l90-rj";
    }

    private static async Task<string> YtMusicServiceRun(Action<System.Collections.ObjectModel.Collection<string>> args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.IO.File.Exists("/config/bin/yt-dlp") ? "/config/bin/yt-dlp" : "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        args(psi.ArgumentList);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new TimeoutException("yt-dlp timeout"); }
        var output = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("yt-dlp: " + (await stderr).Split('\n').LastOrDefault(l => l.Length > 0)?.Trim());
        return output;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.').Trim();
        if (clean.Length > 120) clean = clean[..120].Trim();
        return clean is "" or "." or ".." ? "" : clean;
    }
}
