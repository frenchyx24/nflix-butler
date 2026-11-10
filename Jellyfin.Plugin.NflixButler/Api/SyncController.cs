using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Configuration;
using Jellyfin.Plugin.NflixButler.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Api;

/**
 * Deezer / Spotify account linking, playlist import (with artist+title matching against the local
 * Jellyfin library) and Deezer export (create the playlist on the user's Deezer account).
 * OAuth codes come from the in-app web flow; tokens are exchanged and stored server-side only.
 */
[ApiController]
[Authorize]
[Route("Plugins/NflixButler/sync")]
public class SyncController : ControllerBase
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SyncStore _store;
    private readonly ILibraryManager _library;
    private readonly ILogger<SyncController> _logger;
    private readonly PendingMusicService _pending;

    public SyncController(SyncStore store, ILibraryManager library, PendingMusicService pending, ILogger<SyncController> logger)
    {
        _store = store;
        _library = library;
        _pending = pending;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Guards a userId taken from the body or the query string: a signed-in user may only read or
    /// change their own linked accounts; only an administrator may name another user. A request
    /// without a resolvable caller (or without a valid userId) is refused.
    /// </summary>
    private bool IsOtherUser(string? requested) =>
        !Guid.TryParse(requested, out _) || CallerIdentity.EffectiveUser(User, requested) == null;

    /// <summary>Refreshes a Spotify access token when it is about to expire (one hour lifetime).</summary>
    private async Task<SyncAccount> FreshAsync(string userId, SyncAccount account, CancellationToken ct)
    {
        if (account.Service == "youtube") return await FreshGoogleAsync(userId, account, ct);
        if (account.Service != "spotify" || account.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2)) return account;
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) return account;
        try
        {
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = account.RefreshToken,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token") { Content = body };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Config.SpotifyClientId + ":" + Config.SpotifyClientSecret)));
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("sync: Spotify refresh failed {Status}", (int)res.StatusCode);
                return account;
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("access_token", out var token)) account.AccessToken = token.GetString() ?? account.AccessToken;
            if (root.TryGetProperty("refresh_token", out var rotated)) account.RefreshToken = rotated.GetString() ?? account.RefreshToken;
            account.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(
                root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetInt32() : 3600);
            _store.Put(userId, account);
            _store.Save();
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "sync: Spotify refresh failed");
        }
        return account;
    }

    public sealed class LinkRequest
    {
        public string Service { get; set; } = "";
        public string Code { get; set; } = "";
        public string UserId { get; set; } = "";
    }

    public sealed class PendingRequest
    {
        public string UserId { get; set; } = "";
        public string PlaylistId { get; set; } = "";
        public List<PendingItem> Tracks { get; set; } = new();
    }

    public sealed class PendingItem
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string VideoId { get; set; } = "";
        public int Position { get; set; }
    }

    /**
     * Keeps the tracks of an imported playlist that the library lacks: they stay in the playlist
     * (played online) and are fetched by Lidarr in the background, then swapped for the real file.
     */
    [HttpPost("pending")]
    public IActionResult AddPending([FromBody] PendingRequest request)
    {
        if (IsOtherUser(request.UserId)) return Forbid();
        if (!Guid.TryParse(request.PlaylistId, out var playlistGuid)) return BadRequest(new { message = "playlistId invalide" });
        var tracks = (request.Tracks ?? new()).Take(1000)
            .Where(t => !string.IsNullOrWhiteSpace(t.Title) && t.Title.Length <= 300 && (t.Artist ?? "").Length <= 300)
            .Select(t => new PendingTrack
            {
                PlaylistId = playlistGuid.ToString("N"),
                UserId = Guid.Parse(request.UserId).ToString("N"),
                Position = t.Position,
                Title = t.Title.Trim(),
                Artist = (t.Artist ?? "").Trim(),
                VideoId = CallerIdentity.IsVideoId(t.VideoId) ? t.VideoId : "",
            }).ToList();
        _pending.Add(tracks);
        return Ok(new { queued = tracks.Count, downloads = PendingMusicService.LidarrConfigured });
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending([FromQuery] string userId, [FromQuery] string playlistId, CancellationToken ct)
    {
        if (IsOtherUser(userId)) return Forbid();
        if (!Guid.TryParse(playlistId, out var playlistGuid)) return BadRequest(new { message = "playlistId invalide" });
        var owner = Guid.Parse(userId).ToString("N");
        var list = _pending.ForPlaylist(playlistGuid.ToString("N")).Where(x => x.UserId == owner).ToList();
        var status = await _pending.StatusAsync(list, ct);
        return Ok(new
        {
            tracks = list.Select(t => new
            {
                status = status.TryGetValue(t.Id, out var st) ? st.Label : "",
                progress = status.TryGetValue(t.Id, out var sp) ? sp.Percent : -1,
                id = t.Id,
                title = t.Title,
                artist = t.Artist,
                album = t.Album,
                cover = t.Cover,
                duration = t.Duration,
                videoId = t.VideoId,
                position = t.Position,
                state = t.State,
            }),
        });
    }

    [HttpDelete("pending")]
    public IActionResult DeletePending([FromQuery] string userId, [FromQuery] string playlistId, [FromQuery] string id)
    {
        if (IsOtherUser(userId)) return Forbid();
        if (!Guid.TryParse(playlistId, out var playlistGuid) || !CallerIdentity.IsPlaylistId(id)) return BadRequest(new { message = "id invalide" });
        var owned = _pending.ForPlaylist(playlistGuid.ToString("N")).Any(x => x.Id == id && x.UserId == Guid.Parse(userId).ToString("N"));
        return owned && _pending.Remove(playlistGuid.ToString("N"), id) ? NoContent() : NotFound();
    }

    public sealed class ImportRequest
    {
        public string Service { get; set; } = "";
        public string PlaylistId { get; set; } = "";
        public string UserId { get; set; } = "";
    }

    public sealed class ExportRequest
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<TrackRef> Tracks { get; set; } = new();
    }

    public sealed class TrackRef
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string VideoId { get; set; } = "";
    }

    public sealed class GoogleRequest
    {
        public string UserId { get; set; } = "";
    }

    private const string GoogleScope = "https://www.googleapis.com/auth/youtube.readonly";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string DeviceCode, DateTime Expires)> GoogleDevices = new();

    /**
     * Starts the Google device flow (no redirect, works on any phone): the app shows the code and
     * opens google.com/device; the device code itself stays on the server.
     */
    [HttpPost("google/start")]
    public async Task<IActionResult> GoogleStart([FromBody] GoogleRequest request, CancellationToken ct)
    {
        if (IsOtherUser(request?.UserId)) return Forbid();
        if (string.IsNullOrWhiteSpace(Config.GoogleClientId) || string.IsNullOrWhiteSpace(Config.GoogleClientSecret))
            return StatusCode(412, new { message = "Compte musique non configuré côté serveur." });
        try
        {
            using var res = await Http.PostAsync("https://oauth2.googleapis.com/device/code", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Config.GoogleClientId,
                ["scope"] = GoogleScope,
            }), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // 4xx, not 502: the CDN replaces a 502 body with its own error page, the app would lose this message.
                _logger.LogWarning("sync: device code refused by upstream, status {Status}", (int)res.StatusCode);
                return StatusCode(400, new { message = "Le service de musique en ligne a refusé la demande." });
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var deviceCode = root.GetProperty("device_code").GetString() ?? "";
            var expires = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 1800;
            GoogleDevices[request!.UserId.ToLowerInvariant()] = (deviceCode, DateTime.UtcNow.AddSeconds(expires));
            return Ok(new
            {
                userCode = root.GetProperty("user_code").GetString(),
                verificationUrl = root.TryGetProperty("verification_url", out var vu) ? vu.GetString() : "https://www.google.com/device",
                interval = root.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number ? iv.GetInt32() : 5,
                expiresIn = expires,
            });
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: google start failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Service de musique en ligne injoignable." });
        }
    }

    /** One poll of the device flow: pending, slow_down, linked or expired. */
    [HttpPost("google/poll")]
    public async Task<IActionResult> GooglePoll([FromBody] GoogleRequest request, CancellationToken ct)
    {
        if (IsOtherUser(request?.UserId)) return Forbid();
        var key = request!.UserId.ToLowerInvariant();
        if (!GoogleDevices.TryGetValue(key, out var device) || device.Expires < DateTime.UtcNow)
            return Ok(new { status = "expired" });
        try
        {
            using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Config.GoogleClientId,
                ["client_secret"] = Config.GoogleClientSecret,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!res.IsSuccessStatusCode)
            {
                var error = root.TryGetProperty("error", out var er) ? er.GetString() ?? "" : "";
                if (error is "authorization_pending" or "slow_down") return Ok(new { status = error == "slow_down" ? "slow_down" : "pending" });
                GoogleDevices.TryRemove(key, out _);
                return Ok(new { status = error == "access_denied" ? "denied" : "expired" });
            }
            GoogleDevices.TryRemove(key, out _);
            var account = new SyncAccount
            {
                Service = "youtube",
                AccessToken = root.GetProperty("access_token").GetString() ?? "",
                RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt32() : 3600),
                LinkedAtUtc = DateTime.UtcNow,
            };
            try
            {
                var me = await GoogleGetAsync("/youtube/v3/channels?mine=true&part=snippet", account, ct);
                using var meDoc = JsonDocument.Parse(me);
                if (meDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var first = items[0];
                    account.ProfileId = first.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "";
                    account.ProfileName = first.TryGetProperty("snippet", out var sn) && sn.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                }
            }
            catch { }
            _store.Put(request.UserId, account);
            _store.Save();
            _logger.LogInformation("Sync: compte youtube lié pour {User}", request.UserId);
            return Ok(new { status = "linked", profileName = account.ProfileName });
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: google poll failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Service de musique en ligne injoignable." });
        }
    }

    private async Task<SyncAccount> FreshGoogleAsync(string userId, SyncAccount account, CancellationToken ct)
    {
        if (account.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2) || string.IsNullOrWhiteSpace(account.RefreshToken)) return account;
        try
        {
            using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Config.GoogleClientId,
                ["client_secret"] = Config.GoogleClientSecret,
                ["refresh_token"] = account.RefreshToken,
                ["grant_type"] = "refresh_token",
            }), ct);
            if (!res.IsSuccessStatusCode) { _logger.LogWarning("sync: Google refresh failed {Status}", (int)res.StatusCode); return account; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            account.AccessToken = doc.RootElement.GetProperty("access_token").GetString() ?? account.AccessToken;
            account.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600);
            _store.Put(userId, account);
            _store.Save();
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: Google refresh failed: {Message}", error.Message);
        }
        return account;
    }

    private static async Task<string> GoogleGetAsync(string path, SyncAccount account, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("service en ligne a répondu " + (int)res.StatusCode);
        return text;
    }

    [HttpGet("config")]
    public IActionResult Config_() => Ok(new
    {
        youtubeConfigured = !string.IsNullOrWhiteSpace(Config.GoogleClientId) && !string.IsNullOrWhiteSpace(Config.GoogleClientSecret),
        redirectUri = Config.SyncRedirectUri,
        deezerConfigured = !string.IsNullOrWhiteSpace(Config.DeezerAppId) && !string.IsNullOrWhiteSpace(Config.DeezerAppSecret),
        deezerAppId = Config.DeezerAppId,
        spotifyConfigured = !string.IsNullOrWhiteSpace(Config.SpotifyClientId) && !string.IsNullOrWhiteSpace(Config.SpotifyClientSecret),
        spotifyClientId = Config.SpotifyClientId,
    });

    /// <summary>Exchanges an OAuth code for a token and stores the linked account.</summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest request, CancellationToken ct)
    {
        var service = (request.Service ?? "").ToLowerInvariant();
        if (service is not ("deezer" or "spotify"))
            return BadRequest(new { message = "service doit être deezer ou spotify" });
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { message = "code et userId requis" });
        if (IsOtherUser(request.UserId))
            return Forbid();

        try
        {
            SyncAccount account;
            if (service == "deezer")
            {
                if (string.IsNullOrWhiteSpace(Config.DeezerAppId) || string.IsNullOrWhiteSpace(Config.DeezerAppSecret))
                    return StatusCode(412, new { message = "App Deezer non configurée côté serveur (DeezerAppId/DeezerAppSecret)." });
                var tokenUrl = "https://connect.deezer.com/oauth/access_token.php?app_id=" + Config.DeezerAppId
                    + "&secret=" + Config.DeezerAppSecret + "&code=" + Uri.EscapeDataString(request.Code);
                var tokenText = await Http.GetStringAsync(tokenUrl, ct);
                var parts = tokenText.Split('&')
                    .Select(p => p.Split('='))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0], p => p[1]);
                if (!parts.TryGetValue("access_token", out var token) || string.IsNullOrWhiteSpace(token))
                    return StatusCode(400, new { message = "Deezer a refusé le code." });
                // With the offline_access permission Deezer returns no "expires": the token does not expire.
                var expires = parts.TryGetValue("expires", out var exp) && int.TryParse(exp, out var e) ? e : 0;
                var profile = await DeezerGet("/user", token, ct);
                account = new SyncAccount
                {
                    Service = service,
                    AccessToken = token,
                    ExpiresAtUtc = expires > 0 ? DateTime.UtcNow.AddSeconds(expires) : DateTime.MaxValue,
                    ProfileId = profile.GetProperty("id").GetRawText(),
                    ProfileName = profile.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    LinkedAtUtc = DateTime.UtcNow,
                };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Config.SpotifyClientId) || string.IsNullOrWhiteSpace(Config.SpotifyClientSecret))
                    return StatusCode(412, new { message = "App Spotify non configurée côté serveur." });
                var body = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = request.Code,
                    ["redirect_uri"] = Config.SyncRedirectUri,
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token") { Content = body };
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(Config.SpotifyClientId + ":" + Config.SpotifyClientSecret)));
                var json = await Http.SendAsync(req, ct);
                var text = await json.Content.ReadAsStringAsync(ct);
                if (!json.IsSuccessStatusCode) return StatusCode(400, new { message = "Spotify a refusé le code (" + (int)json.StatusCode + ")." });
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root.GetProperty("access_token").GetString());
                using var profileRes = await Http.SendAsync(profileReq, ct);
                var profileText = await profileRes.Content.ReadAsStringAsync(ct);
                using var pdoc = JsonDocument.Parse(profileText);
                account = new SyncAccount
                {
                    Service = service,
                    AccessToken = root.GetProperty("access_token").GetString()!,
                    RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number ? ei.GetInt32() : 3600),
                    ProfileId = pdoc.RootElement.GetProperty("id").GetString() ?? "",
                    ProfileName = pdoc.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                    LinkedAtUtc = DateTime.UtcNow,
                };
            }

            _store.Put(request.UserId, account);
            _store.Save();
            _logger.LogInformation("Sync: compte {Service} lié pour {User} ({Name})", service, request.UserId, account.ProfileName);
            return Ok(new { linked = true, service, profileName = account.ProfileName, profileId = account.ProfileId });
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: upstream call failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Le service en ligne a renvoyé une erreur." });
        }
    }

    [HttpGet("status")]
    public IActionResult Status([FromQuery] string userId)
    {
        if (IsOtherUser(userId)) return Forbid();
        return Ok(new
        {
            accounts = _store.ForUser(userId ?? "").Select(a => new
            {
                service = a.Service,
                profileName = a.ProfileName,
                profileId = a.ProfileId,
                linkedAtUtc = a.LinkedAtUtc,
            }),
        });
    }

    [HttpDelete("unlink")]
    public IActionResult Unlink([FromQuery] string userId, [FromQuery] string service)
    {
        if (IsOtherUser(userId)) return Forbid();
        _store.Remove(userId ?? "", (service ?? "").ToLowerInvariant());
        _store.Save();
        return Ok(new { unlinked = true });
    }

    /// <summary>Playlists of the linked account.</summary>
    [HttpGet("playlists")]
    public async Task<IActionResult> Playlists([FromQuery] string service, [FromQuery] string userId, CancellationToken ct)
    {
        if (IsOtherUser(userId)) return Forbid();
        var account = _store.Get(userId ?? "", (service ?? "").ToLowerInvariant());
        if (account == null) return StatusCode(412, new { message = "Compte " + service + " non lié." });
        try
        {
            account = await FreshAsync(userId ?? "", account, ct);
            if (account.Service == "youtube")
            {
                var lists = new List<object> { new { id = "LM", title = "Titres likés", tracks = 0 } };
                string? page = null;
                for (var i = 0; i < 4; i++)
                {
                    var text = await GoogleGetAsync("/youtube/v3/playlists?mine=true&part=snippet,contentDetails&maxResults=50" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : ""), account, ct);
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("items", out var items)) break;
                    foreach (var p in items.EnumerateArray())
                    {
                        var snippet = p.TryGetProperty("snippet", out var sn) ? sn : default;
                        lists.Add(new
                        {
                            id = p.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "",
                            title = snippet.ValueKind == JsonValueKind.Object && snippet.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            tracks = p.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("itemCount", out var ic) && ic.ValueKind == JsonValueKind.Number ? ic.GetInt32() : 0,
                        });
                    }
                    page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                    if (page == null) break;
                }
                return Ok(new { playlists = lists });
            }
            if (account.Service == "deezer")
            {
                var doc = await DeezerGet("/user/playlists?limit=100", account.AccessToken, ct);
                var data = doc.GetProperty("data").EnumerateArray().Select(p => new
                {
                    id = p.GetProperty("id").GetRawText(),
                    title = p.GetProperty("title").GetString() ?? "",
                    tracks = p.TryGetProperty("nb_tracks", out var nb) && nb.ValueKind == JsonValueKind.Number ? nb.GetInt32() : 0,
                }).ToList();
                return Ok(new { playlists = data });
            }
            else
            {
                var text = await SpotifyGetAsync(userId ?? "", "/v1/me/playlists?limit=50", account, ct);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("items", out var entries))
                    return StatusCode(400, new { message = "Réponse Spotify inattendue." });
                var items = entries.EnumerateArray().Select(p => new
                {
                    id = p.GetProperty("id").GetString() ?? "",
                    title = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    tracks = p.TryGetProperty("tracks", out var t) && t.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt32() : 0,
                }).ToList();
                return Ok(new { playlists = items });
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: upstream call failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Le service en ligne a renvoyé une erreur." });
        }
    }

    /**
     * Imports one streaming playlist: fetches its tracks, matches each against the local Jellyfin
     * library by artist + title, and returns the matched Jellyfin item ids (the app creates the
     * playlist) plus the missing tracks (downloadable).
     */
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
    {
        if (IsOtherUser(request.UserId)) return Forbid();
        if (!CallerIdentity.IsPlaylistId(request.PlaylistId))
            return BadRequest(new { message = "playlistId invalide" });
        var account = _store.Get(request.UserId ?? "", (request.Service ?? "").ToLowerInvariant());
        if (account == null) return StatusCode(412, new { message = "Compte non lié." });
        account = await FreshAsync(request.UserId ?? "", account, ct);

        var tracks = new List<TrackRef>();
        try
        {
            if (account.Service == "youtube")
            {
                // "LM" = the account's liked music.
                var playlistId = request.PlaylistId == "LM" ? "LL" : request.PlaylistId;
                string? page = null;
                for (var i = 0; i < 10; i++)
                {
                    var text = await GoogleGetAsync("/youtube/v3/playlistItems?part=snippet&maxResults=50&playlistId=" + Uri.EscapeDataString(playlistId) + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : ""), account, ct);
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("items", out var items)) break;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("snippet", out var sn)) continue;
                        var title = sn.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        var owner = sn.TryGetProperty("videoOwnerChannelTitle", out var o) ? o.GetString() ?? "" : "";
                        var videoId = sn.TryGetProperty("resourceId", out var rid) && rid.TryGetProperty("videoId", out var v) ? v.GetString() ?? "" : "";
                        if (title.Length == 0 || title is "Deleted video" or "Private video") continue;
                        var artist = owner.Replace(" - Topic", "").Trim();
                        // "Artist - Title" uploads: split when the channel is not the artist.
                        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
                        if (dash > 0 && !owner.EndsWith(" - Topic", StringComparison.Ordinal)) { artist = title[..dash].Trim(); title = title[(dash + 3)..].Trim(); }
                        tracks.Add(new TrackRef { Title = title, Artist = artist, VideoId = CallerIdentity.IsVideoId(videoId) ? videoId : "" });
                    }
                    page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                    if (page == null) break;
                }
            }
            else if (account.Service == "deezer")
            {
                var doc = await DeezerGet("/playlist/" + request.PlaylistId, account.AccessToken, ct);
                foreach (var t in doc.GetProperty("tracks").GetProperty("data").EnumerateArray())
                    tracks.Add(new TrackRef
                    {
                        Title = t.GetProperty("title").GetString() ?? "",
                        Artist = t.GetProperty("artist").GetProperty("name").GetString() ?? "",
                    });
            }
            else
            {
                for (var offset = 0; offset < 500; offset += 100)
                {
                    var text = await SpotifyGetAsync(request.UserId ?? "",
                        "/v1/playlists/" + request.PlaylistId + "/tracks?limit=100&offset=" + offset, account, ct);
                    using var doc = JsonDocument.Parse(text);
                    if (!doc.RootElement.TryGetProperty("items", out var items)) break;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("track", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                        var title = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var artist = "";
                        if (t.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                        {
                            var first = artists.EnumerateArray().FirstOrDefault();
                            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("name", out var an))
                                artist = an.GetString() ?? "";
                        }
                        if (title.Length > 0) tracks.Add(new TrackRef { Title = title, Artist = artist });
                    }
                    if (items.GetArrayLength() < 100) break;
                }
            }
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: upstream call failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Le service en ligne a renvoyé une erreur." });
        }

        return Ok(MatchAll(tracks, ""));
    }

    /// <summary>Splits a fetched track list into library hits and missing tracks, and queues the artists' discographies.</summary>
    private object MatchAll(List<TrackRef> tracks, string name)
    {
        var matched = new List<object>();
        var missing = new List<object>();
        for (var position = 0; position < tracks.Count; position++)
        {
            var track = tracks[position];
            var item = MatchLocal(track.Title, track.Artist);
            if (item != null)
                matched.Add(new { itemId = item.Id.ToString("N"), title = track.Title, artist = track.Artist, localName = item.Name, position });
            else
                missing.Add(new { title = track.Title, artist = track.Artist, videoId = track.VideoId, position });
        }
        _pending.QueueArtists(tracks.Select(t => t.Artist));
        return new { name, total = tracks.Count, matched, missing };
    }

    public sealed class UrlImportRequest
    {
        public string UserId { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private static readonly string[] LinkHosts =
    {
        "deezer.com", "www.deezer.com", "link.deezer.com", "deezer.page.link", "dzr.page.link",
        "open.spotify.com", "spotify.link", "spotify.app.link",
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be",
    };

    private static bool LinkAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && LinkHosts.Contains(uri.Host.ToLowerInvariant());

    /**
     * Imports a public playlist (or album) from its share link, with no account and no developer key:
     * Deezer public API, Spotify embed page, YouTube / YouTube Music through the downloader.
     */
    [HttpPost("import-url")]
    public async Task<IActionResult> ImportUrl([FromBody] UrlImportRequest request, CancellationToken ct)
    {
        if (IsOtherUser(request.UserId)) return Forbid();
        if (!Uri.TryCreate((request.Url ?? "").Trim(), UriKind.Absolute, out var uri) || !LinkAllowed(uri))
            return BadRequest(new { message = "Lien non reconnu : collez le lien de partage d'une playlist Deezer, Spotify ou YouTube Music." });
        try
        {
            // Short share links redirect to the real page.
            if (uri.Host is "link.deezer.com" or "deezer.page.link" or "dzr.page.link" or "spotify.link" or "spotify.app.link" or "youtu.be")
            {
                using var head = new HttpRequestMessage(HttpMethod.Get, uri);
                head.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var res = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
                var final = res.RequestMessage?.RequestUri;
                if (final == null || !LinkAllowed(final)) return BadRequest(new { message = "Lien de partage non reconnu." });
                uri = final;
            }
            var path = uri.AbsolutePath;
            var tracks = new List<TrackRef>();
            var name = "";
            System.Text.RegularExpressions.Match m;
            if (uri.Host.EndsWith("deezer.com", StringComparison.Ordinal) && (m = System.Text.RegularExpressions.Regex.Match(path, @"/(playlist|album)/(\d{1,15})")).Success)
            {
                var kind = m.Groups[1].Value;
                var id = m.Groups[2].Value;
                using (var info = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/" + kind + "/" + id, ct)))
                {
                    if (info.RootElement.TryGetProperty("error", out _)) return BadRequest(new { message = "Playlist Deezer privée ou introuvable." });
                    name = info.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                }
                for (var index = 0; index < 2000; index += 100)
                {
                    using var page = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/" + kind + "/" + id + "/tracks?limit=100&index=" + index, ct));
                    if (!page.RootElement.TryGetProperty("data", out var data)) break;
                    foreach (var tr in data.EnumerateArray())
                    {
                        var title = tr.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        var artist = tr.TryGetProperty("artist", out var ar) && ar.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        if (title.Length > 0) tracks.Add(new TrackRef { Title = title, Artist = artist });
                    }
                    if (data.GetArrayLength() < 100) break;
                }
            }
            else if (uri.Host == "open.spotify.com" && (m = System.Text.RegularExpressions.Regex.Match(path, @"/(playlist|album)/([A-Za-z0-9]{22})")).Success)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://open.spotify.com/embed/" + m.Groups[1].Value + "/" + m.Groups[2].Value);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                using var res = await Http.SendAsync(req, ct);
                var html = await res.Content.ReadAsStringAsync(ct);
                var json = System.Text.RegularExpressions.Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!json.Success) return BadRequest(new { message = "Playlist Spotify privée ou introuvable." });
                using var doc = JsonDocument.Parse(json.Groups[1].Value);
                var entity = doc.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("state").GetProperty("data").GetProperty("entity");
                name = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "" : entity.TryGetProperty("title", out var n2) ? n2.GetString() ?? "" : "";
                if (entity.TryGetProperty("trackList", out var list))
                    foreach (var tr in list.EnumerateArray())
                    {
                        var title = tr.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        var artist = tr.TryGetProperty("subtitle", out var st) ? (st.GetString() ?? "").Replace("\u00a0", " ") : "";
                        if (title.Length > 0) tracks.Add(new TrackRef { Title = title, Artist = artist });
                    }
            }
            else if (uri.Host.Contains("youtube", StringComparison.Ordinal) && System.Web.HttpUtility.ParseQueryString(uri.Query)["list"] is { } listId
                && System.Text.RegularExpressions.Regex.IsMatch(listId, "^[A-Za-z0-9_-]{2,64}$"))
            {
                var output = await YtMusicService.RunAsync(a =>
                {
                    a.Add("--flat-playlist"); a.Add("-J"); a.Add("--no-warnings");
                    a.Add("https://music.youtube.com/playlist?list=" + listId);
                }, ct);
                using var doc = JsonDocument.Parse(output);
                name = doc.RootElement.TryGetProperty("title", out var n) ? (n.GetString() ?? "").Replace("Album - ", "") : "";
                if (doc.RootElement.TryGetProperty("entries", out var entries))
                    foreach (var e in entries.EnumerateArray())
                    {
                        var title = e.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                        var owner = e.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() ?? ""
                            : e.TryGetProperty("uploader", out var up) && up.ValueKind == JsonValueKind.String ? up.GetString() ?? "" : "";
                        var videoId = e.TryGetProperty("id", out var vid) ? vid.GetString() ?? "" : "";
                        if (title.Length == 0 || title is "[Deleted video]" or "[Private video]") continue;
                        var artist = owner.Replace(" - Topic", "").Trim();
                        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
                        if (dash > 0 && !owner.EndsWith(" - Topic", StringComparison.Ordinal)) { artist = title[..dash].Trim(); title = title[(dash + 3)..].Trim(); }
                        // Video titles carry noise: "(Official Video)", "[Lyrics]", "(Clip officiel)"…
                        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\(\[][^\)\]]*(official|officiel|oficial|video|vidéo|lyrics?|paroles|audio|clip|visualizer|hd|4k)[^\)\]]*[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                        if (title.Length == 0) continue;
                        tracks.Add(new TrackRef { Title = title, Artist = artist, VideoId = CallerIdentity.IsVideoId(videoId) ? videoId : "" });
                    }
            }
            else
            {
                return BadRequest(new { message = "Ce lien ne pointe pas vers une playlist ou un album." });
            }
            if (tracks.Count == 0) return BadRequest(new { message = "Playlist vide, privée ou introuvable." });
            return Ok(MatchAll(tracks, name.Trim().Length > 0 ? name.Trim() : "Playlist importée"));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning("sync: link import failed: {Message}", error.Message);
            if (error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("private", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Playlist privée : passez-la en « publique » ou « non répertoriée », ou liez votre compte musique." });
            return BadRequest(new { message = "Impossible de lire cette playlist pour le moment." });
        }
    }

    /// <summary>
    /// Matches one track against the local library: Jellyfin search "artist title", then a title-only
    /// fallback. Best normalized title hit wins, with the artist as a tie-breaker.
    /// </summary>
    private MediaBrowser.Controller.Entities.BaseItem? MatchLocal(string title, string artist) => MatchLocal(_library, title, artist);

    internal static MediaBrowser.Controller.Entities.BaseItem? MatchLocal(ILibraryManager library, string title, string artist)
    {
        var wanted = Normalize(title);
        if (wanted.Length < 2) return null;
        var wantedArtist = Normalize(artist);
        try
        {
            var candidates = SearchCandidates(library, artist + " " + title);
            if (candidates.Count == 0) candidates = SearchCandidates(library, title);
            MediaBrowser.Controller.Entities.BaseItem? best = null;
            var bestScore = 0.0;
            foreach (var candidate in candidates)
            {
                var titleScore = Score(Normalize(candidate.Name), wanted);
                if (titleScore < 0.6) continue;
                var artistScore = 0.0;
                if (candidate is MediaBrowser.Controller.Entities.Audio.Audio audio && wantedArtist.Length > 0)
                {
                    artistScore = (audio.Artists ?? Array.Empty<string>())
                        .Select(a => Score(Normalize(a), wantedArtist))
                        .DefaultIfEmpty(0)
                        .Max();
                }
                var total = titleScore + (artistScore * 0.5);
                if (total > bestScore) { bestScore = total; best = candidate; }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<MediaBrowser.Controller.Entities.BaseItem> SearchCandidates(ILibraryManager library, string term)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            SearchTerm = term,
            Limit = 12,
        };
        return library.GetItemList(query).ToList();
    }

    private static double Score(string candidate, string wanted)
    {
        if (candidate == wanted) return 1.0;
        if (candidate.Contains(wanted) || wanted.Contains(candidate)) return 0.8;
        var cw = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ww = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var common = cw.Count(w => ww.Contains(w));
        return ww.Length == 0 ? 0 : (double)common / ww.Length;
    }

    /// <summary>Lowercase, accent-folded, punctuation-free form used for every comparison.</summary>
    internal static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value.ToLowerInvariant())
        {
            var c = raw switch
            {
                'à' or 'á' or 'â' or 'ä' or 'ã' or 'å' => 'a',
                'ç' => 'c',
                'è' or 'é' or 'ê' or 'ë' => 'e',
                'ì' or 'í' or 'î' or 'ï' => 'i',
                'ñ' => 'n',
                'ò' or 'ó' or 'ô' or 'ö' or 'õ' => 'o',
                'ù' or 'ú' or 'û' or 'ü' => 'u',
                'ý' or 'ÿ' => 'y',
                'œ' => 'o',
                'æ' => 'a',
                'ß' => 's',
                _ => char.IsLetterOrDigit(raw) ? raw : ' ',
            };
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Public Deezer search (no token needed): used to map a local track to a Deezer song id for export.</summary>
    [HttpGet("deezer-search")]
    public async Task<IActionResult> DeezerSearch([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(new { tracks = Array.Empty<object>() });
        try
        {
            var json = await Http.GetStringAsync("https://api.deezer.com/search?q=" + Uri.EscapeDataString(q) + "&limit=5", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return Ok(new { tracks = Array.Empty<object>() });
            var tracks = data.EnumerateArray().Select(t => new
            {
                id = t.GetProperty("id").GetRawText(),
                title = t.GetProperty("title").GetString() ?? "",
                artist = t.GetProperty("artist").GetProperty("name").GetString() ?? "",
            }).ToList();
            return Ok(new { tracks });
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: upstream call failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Le service en ligne a renvoyé une erreur." });
        }
    }

    /**
     * Creates a playlist on the user's Deezer account and fills it: each local track is searched on
     * Deezer by "artist title" and the best match is added. Spotify export is not possible (their API
     * forbids playlist creation), only import.
     */
    [HttpPost("export-deezer")]
    public async Task<IActionResult> ExportDeezer([FromBody] ExportRequest request, CancellationToken ct)
    {
        if (IsOtherUser(request.UserId)) return Forbid();
        var account = _store.Get(request.UserId ?? "", "deezer");
        if (account == null) return StatusCode(412, new { message = "Compte Deezer non lié." });

        try
        {
            var name = string.IsNullOrWhiteSpace(request.Name) ? "Nflix" : request.Name.Trim();
            if (name.Length > 100) name = name[..100];
            var createText = await Http.GetStringAsync(
                "https://api.deezer.com/user/playlists?access_token=" + Uri.EscapeDataString(account.AccessToken) + "&request_method=POST&name=" + Uri.EscapeDataString(name), ct);
            using var createDoc = JsonDocument.Parse(createText);
            var deezerPlaylistId = createDoc.RootElement.GetProperty("id").GetInt64();

            var added = 0;
            var missing = new List<object>();
            foreach (var track in (request.Tracks ?? new List<TrackRef>()).Take(500))
            {
                var q = "artist:\"" + track.Artist + "\" track:\"" + track.Title + "\"";
                var searchJson = await Http.GetStringAsync("https://api.deezer.com/search?q=" + Uri.EscapeDataString(q) + "&limit=1", ct);
                using var sdoc = JsonDocument.Parse(searchJson);
                var first = sdoc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object) { missing.Add(new { track.Title, track.Artist }); continue; }
                var songId = first.GetProperty("id").GetInt64();
                var addResp = await Http.GetStringAsync(
                    "https://api.deezer.com/playlist/" + deezerPlaylistId + "/songs?access_token=" + Uri.EscapeDataString(account.AccessToken) + "&request_method=POST&song_id=" + songId, ct);
                if (addResp.Trim() == "true") added++;
            }
            return Ok(new { created = true, deezerPlaylistId, added, missing });
        }
        catch (Exception error)
        {
            _logger.LogWarning("sync: upstream call failed: {Message}", error.Message);
            return StatusCode(400, new { message = "Le service en ligne a renvoyé une erreur." });
        }
    }

    /// <summary>GET on the Spotify Web API, refreshing the token once on a 401 (expired access token).</summary>
    private async Task<string> SpotifyGetAsync(string userId, string path, SyncAccount account, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com" + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt > 0) return text;
            account.ExpiresAtUtc = DateTime.MinValue;
            await FreshAsync(userId, account, ct);
        }
    }

    private static async Task<JsonElement> DeezerGet(string path, string token, CancellationToken ct)
    {
        var text = await Http.GetStringAsync("https://api.deezer.com" + path + (path.Contains('?') ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token), ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
