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

    public SyncController(SyncStore store, ILibraryManager library, ILogger<SyncController> logger)
    {
        _store = store;
        _library = library;
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
    }

    [HttpGet("config")]
    public IActionResult Config_() => Ok(new
    {
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
                    return StatusCode(502, new { message = "Deezer a refusé le code." });
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
                if (!json.IsSuccessStatusCode) return StatusCode(502, new { message = "Spotify a refusé le code (" + (int)json.StatusCode + ")." });
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
            return StatusCode(502, new { message = error.Message });
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
                    return StatusCode(502, new { message = "Réponse Spotify inattendue." });
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
            return StatusCode(502, new { message = error.Message });
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
            if (account.Service == "deezer")
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
            return StatusCode(502, new { message = error.Message });
        }

        var matched = new List<object>();
        var missing = new List<object>();
        foreach (var track in tracks)
        {
            var item = MatchLocal(track.Title, track.Artist);
            if (item != null)
                matched.Add(new { itemId = item.Id.ToString("N"), title = track.Title, artist = track.Artist, localName = item.Name });
            else
                missing.Add(new { title = track.Title, artist = track.Artist });
        }
        return Ok(new { total = tracks.Count, matched, missing });
    }

    /// <summary>
    /// Matches one track against the local library: Jellyfin search "artist title", then a title-only
    /// fallback. Best normalized title hit wins, with the artist as a tie-breaker.
    /// </summary>
    private MediaBrowser.Controller.Entities.BaseItem? MatchLocal(string title, string artist)
    {
        var wanted = Normalize(title);
        if (wanted.Length < 2) return null;
        var wantedArtist = Normalize(artist);
        try
        {
            var candidates = SearchCandidates(artist + " " + title);
            if (candidates.Count == 0) candidates = SearchCandidates(title);
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
        catch (Exception error)
        {
            _logger.LogWarning(error, "sync: match local failed for {Title}", title);
            return null;
        }
    }

    private List<MediaBrowser.Controller.Entities.BaseItem> SearchCandidates(string term)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            SearchTerm = term,
            Limit = 12,
        };
        return _library.GetItemList(query).ToList();
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
            return StatusCode(502, new { message = error.Message });
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
            return StatusCode(502, new { message = error.Message });
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
