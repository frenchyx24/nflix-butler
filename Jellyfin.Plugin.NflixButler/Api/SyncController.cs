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
        var service = request.Service.ToLowerInvariant();
        if (service is not ("deezer" or "spotify"))
            return BadRequest(new { message = "service doit être deezer ou spotify" });
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { message = "code et userId requis" });

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
                if (!parts.TryGetValue("access_token", out var token))
                    return StatusCode(502, new { message = "Deezer a refusé le code : " + tokenText });
                var expires = parts.TryGetValue("expires", out var exp) && int.TryParse(exp, out var e) ? e : 3600;
                var profile = await DeezerGet("/user", token, ct);
                account = new SyncAccount
                {
                    Service = service,
                    AccessToken = token,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expires),
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
                if (!json.IsSuccessStatusCode) return StatusCode(502, new { message = "Spotify a refusé le code : " + text });
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", root.GetProperty("access_token").GetString());
                var profileText = await Http.SendAsync(profileReq, ct).Result.Content.ReadAsStringAsync(ct);
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
    public IActionResult Status([FromQuery] string userId) =>
        Ok(new { accounts = _store.ForUser(userId ?? "").Select(a => new { a.Service, a.ProfileName, a.ProfileId, a.LinkedAtUtc }) });

    [HttpDelete("unlink")]
    public IActionResult Unlink([FromQuery] string userId, [FromQuery] string service)
    {
        _store.Remove(userId ?? "", service.ToLowerInvariant());
        _store.Save();
        return Ok(new { unlinked = true });
    }

    /// <summary>Playlists of the linked account.</summary>
    [HttpGet("playlists")]
    public async Task<IActionResult> Playlists([FromQuery] string service, [FromQuery] string userId, CancellationToken ct)
    {
        var account = _store.Get(userId ?? "", service.ToLowerInvariant());
        if (account == null) return StatusCode(412, new { message = "Compte " + service + " non lié." });
        try
        {
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
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/playlists?limit=50");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
                var text = await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(p => new
                {
                    id = p.GetProperty("id").GetString() ?? "",
                    title = p.GetProperty("name").GetString() ?? "",
                    tracks = p.GetProperty("tracks").GetProperty("total").GetInt32(),
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
        var account = _store.Get(request.UserId ?? "", request.Service.ToLowerInvariant());
        if (account == null) return StatusCode(412, new { message = "Compte non lié." });

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
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/playlists/" + request.PlaylistId + "/tracks?limit=100");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
                var text = await (await Http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
                {
                    if (!item.TryGetProperty("track", out var t) || t.ValueKind != JsonValueKind.Object) continue;
                    tracks.Add(new TrackRef
                    {
                        Title = t.GetProperty("name").GetString() ?? "",
                        Artist = t.GetProperty("artists").EnumerateArray().FirstOrDefault().GetProperty("name").GetString() ?? "",
                    });
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

    /// <summary>Matches one track against the local library: Jellyfin search "artist title", best normalized-title hit wins.</summary>
    private MediaBrowser.Controller.Entities.BaseItem? MatchLocal(string title, string artist)
    {
        try
        {
            var wanted = Normalize(title);
            if (wanted.Length < 2) return null;
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
                SearchTerm = artist + " " + title,
                Limit = 8,
            };
            var candidates = _library.GetItemList(query);
            return candidates
                .OrderByDescending(c => Score(Normalize(c.Name), wanted))
                .FirstOrDefault(c => Score(Normalize(c.Name), wanted) >= 0.6);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "sync: match local failed for {Title}", title);
            return null;
        }
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

    internal static string Normalize(string value)
    {
        var sb = new StringBuilder(value.ToLowerInvariant().Length);
        foreach (var c in value.ToLowerInvariant())
            if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Public Deezer search (no token needed): used to map a local track to a Deezer song id for export.</summary>
    [HttpGet("deezer-search")]
    public async Task<IActionResult> DeezerSearch([FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(new { tracks = Array.Empty<object>() });
        var json = await Http.GetStringAsync("https://api.deezer.com/search?q=" + Uri.EscapeDataString(q) + "&limit=5", ct);
        using var doc = JsonDocument.Parse(json);
        var tracks = doc.RootElement.GetProperty("data").EnumerateArray().Select(t => new
        {
            id = t.GetProperty("id").GetRawText(),
            title = t.GetProperty("title").GetString() ?? "",
            artist = t.GetProperty("artist").GetProperty("name").GetString() ?? "",
        }).ToList();
        return Ok(new { tracks });
    }

    /**
     * Creates a playlist on the user's Deezer account and fills it: each local track is searched on
     * Deezer by "artist title" and the best match is added. Spotify export is not possible (their API
     * forbids playlist creation), only import.
     */
    [HttpPost("export-deezer")]
    public async Task<IActionResult> ExportDeezer([FromBody] ExportRequest request, CancellationToken ct)
    {
        var account = _store.Get(request.UserId ?? "", "deezer");
        if (account == null) return StatusCode(412, new { message = "Compte Deezer non lié." });

        try
        {
            var createText = await Http.GetStringAsync(
                "https://api.deezer.com/user/playlists?access_token=" + account.AccessToken + "&request_method=POST&name=" + Uri.EscapeDataString(request.Name), ct);
            using var createDoc = JsonDocument.Parse(createText);
            var deezerPlaylistId = createDoc.RootElement.GetProperty("id").GetInt64();

            var added = 0;
            var missing = new List<object>();
            foreach (var track in request.Tracks)
            {
                var q = "artist:\"" + track.Artist + "\" track:\"" + track.Title + "\"";
                var searchJson = await Http.GetStringAsync("https://api.deezer.com/search?q=" + Uri.EscapeDataString(q) + "&limit=1", ct);
                using var sdoc = JsonDocument.Parse(searchJson);
                var first = sdoc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
                if (first.ValueKind != JsonValueKind.Object) { missing.Add(new { track.Title, track.Artist }); continue; }
                var songId = first.GetProperty("id").GetInt64();
                var addResp = await Http.GetStringAsync(
                    "https://api.deezer.com/playlist/" + deezerPlaylistId + "/songs?access_token=" + account.AccessToken + "&request_method=POST&song_id=" + songId, ct);
                if (addResp.Trim() == "true") added++;
            }
            return Ok(new { created = true, deezerPlaylistId, added, missing });
        }
        catch (Exception error)
        {
            return StatusCode(502, new { message = error.Message });
        }
    }

    private static async Task<JsonElement> DeezerGet(string path, string token, CancellationToken ct)
    {
        var text = await Http.GetStringAsync("https://api.deezer.com" + path + (path.Contains('?') ? "&" : "?") + "access_token=" + token, ct);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
