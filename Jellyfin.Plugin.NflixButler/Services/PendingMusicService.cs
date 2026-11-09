using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// One track of an imported playlist that is not in the library yet. It stays playable online
/// (audio proxy) inside the playlist until the real file lands, then it is swapped for it.
/// </summary>
public sealed class PendingTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlaylistId { get; set; } = "";
    public string UserId { get; set; } = "";
    public int Position { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Cover { get; set; } = "";
    public string VideoId { get; set; } = "";
    public int Duration { get; set; }

    /// <summary>queued → requested (Lidarr searching) → done; or notfound / disabled.</summary>
    public string State { get; set; } = "queued";
    public int LidarrAlbumId { get; set; }
    public int Tries { get; set; }
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextUtc { get; set; } = DateTime.MinValue;
}

/// <summary>
/// Keeps the pending tracks, asks Lidarr for their album, and moves each one into its Jellyfin
/// playlist as soon as the library has it.
/// </summary>
public sealed class PendingMusicService : BackgroundService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<PendingTrack> _items;
    private readonly ILibraryManager _library;
    private readonly IPlaylistManager _playlists;
    private readonly MusicCatalog _catalog;
    private readonly ILogger<PendingMusicService> _logger;

    public PendingMusicService(IApplicationPaths paths, ILibraryManager library, IPlaylistManager playlists, MusicCatalog catalog, ILogger<PendingMusicService> logger)
    {
        var dir = Path.Combine(paths.PluginConfigurationsPath, "NflixButler");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "pending.json");
        _library = library;
        _playlists = playlists;
        _catalog = catalog;
        _logger = logger;
        try { _items = File.Exists(_path) ? JsonSerializer.Deserialize<List<PendingTrack>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch { _items = new(); }
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    public static bool LidarrConfigured => !string.IsNullOrWhiteSpace(Config.LidarrUrl) && !string.IsNullOrWhiteSpace(Config.LidarrApiKey);

    private void Save()
    {
        lock (_gate)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Options));
            File.Move(temp, _path, true);
        }
    }

    public void Add(IEnumerable<PendingTrack> tracks)
    {
        lock (_gate)
        {
            foreach (var t in tracks)
            {
                if (_items.Any(x => x.PlaylistId == t.PlaylistId && x.Title == t.Title && x.Artist == t.Artist)) continue;
                if (!LidarrConfigured) t.State = "disabled";
                _items.Add(t);
            }
        }
        Save();
        _wake.Release();
    }

    public List<PendingTrack> ForPlaylist(string playlistId)
    {
        lock (_gate) return _items.Where(x => x.PlaylistId == playlistId).OrderBy(x => x.Position).ToList();
    }

    public bool Remove(string playlistId, string id)
    {
        int n;
        lock (_gate) n = _items.RemoveAll(x => x.PlaylistId == playlistId && x.Id == id);
        if (n > 0) Save();
        return n > 0;
    }

    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { _logger.LogWarning("pending: pass failed: {Message}", error.Message); }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One pass: swap the tracks the library now has, then push the others to Lidarr.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<PendingTrack> snapshot;
        lock (_gate) snapshot = _items.ToList();
        var changed = false;
        var requestedAlbums = new HashSet<int>(snapshot.Where(x => x.LidarrAlbumId > 0).Select(x => x.LidarrAlbumId));
        foreach (var track in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            var local = SyncController.MatchLocal(_library, track.Title, track.Artist);
            if (local != null && Guid.TryParse(track.PlaylistId, out var pid) && Guid.TryParse(track.UserId, out var uid))
            {
                try
                {
                    int before;
                    lock (_gate) before = _items.Count(x => x.PlaylistId == track.PlaylistId && x.Position < track.Position);
                    // Original order: the slot after every earlier track already in the playlist.
                    try { await _playlists.AddItemToPlaylistAsync(pid, new[] { local.Id }, Math.Max(0, track.Position - before), uid).ConfigureAwait(false); }
                    catch (ArgumentException) { await _playlists.AddItemToPlaylistAsync(pid, new[] { local.Id }, null, uid).ConfigureAwait(false); }
                    lock (_gate) _items.RemoveAll(x => x.Id == track.Id);
                    changed = true;
                    _logger.LogInformation("pending: « {Title} » ajouté à sa playlist depuis la bibliothèque", track.Title);
                    continue;
                }
                catch (Exception error) { _logger.LogWarning("pending: playlist add failed: {Message}", error.Message); }
            }
            if (track.Cover.Length == 0 && track.Tries == 0)
            {
                try
                {
                    var hit = await _catalog.FindTrackAsync(track.Artist, track.Title, ct).ConfigureAwait(false);
                    if (hit != null) { track.Album = hit.Album; track.Cover = hit.Cover; if (hit.Duration > 0) track.Duration = hit.Duration; changed = true; }
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            if (track.NextUtc > DateTime.UtcNow) continue;
            if (track.State == "disabled" && LidarrConfigured) { track.State = "queued"; changed = true; }
            if (track.State != "queued") continue;
            try
            {
                await RequestAsync(track, requestedAlbums, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning("pending: Lidarr request failed for {Title}: {Message}", track.Title, error.Message);
                track.Tries++;
                track.NextUtc = DateTime.UtcNow.AddMinutes(Math.Min(60, 2 * track.Tries));
                if (track.Tries >= 15) track.State = "notfound";
            }
            changed = true;
        }
        if (changed) Save();
    }

    private static HttpRequestMessage LidarrRequest(HttpMethod method, string path, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, Config.LidarrUrl.TrimEnd('/') + "/api/v1/" + path);
        req.Headers.Add("X-Api-Key", Config.LidarrApiKey);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    private static async Task<JsonNode?> LidarrAsync(HttpMethod method, string path, CancellationToken ct, JsonNode? body = null)
    {
        using var req = LidarrRequest(method, path, body);
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Lidarr " + (int)res.StatusCode + " " + path.Split('?')[0]);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static string Norm(string s) => SyncController.Normalize(ArtistNames.Primary(s));

    /// <summary>Finds the album of the track, adds it to Lidarr (monitored) and starts its search.</summary>
    private async Task RequestAsync(PendingTrack track, HashSet<int> requestedAlbums, CancellationToken ct)
    {
        if (track.Album.Length == 0)
        {
            var hit = await _catalog.FindTrackAsync(track.Artist, track.Title, ct).ConfigureAwait(false);
            if (hit != null)
            {
                track.Album = hit.Album;
                if (track.Cover.Length == 0) track.Cover = hit.Cover;
                if (hit.Duration > 0) track.Duration = hit.Duration;
            }
        }
        var wantedArtist = Norm(track.Artist);
        JsonObject? pick = null;
        foreach (var term in new[] { track.Artist + " " + track.Album, track.Artist + " " + track.Title }.Where(t => t.Trim().Length > track.Artist.Length).Distinct())
        {
            var found = await LidarrAsync(HttpMethod.Get, "album/lookup?term=" + Uri.EscapeDataString(term), ct).ConfigureAwait(false) as JsonArray;
            var candidates = (found ?? new JsonArray()).OfType<JsonObject>()
                .Where(a => Norm(a["artist"]?["artistName"]?.GetValue<string>() ?? "") is var n && n.Length > 0 && (n == wantedArtist || n.Contains(wantedArtist) || wantedArtist.Contains(n)))
                .ToList();
            var wantedAlbum = SyncController.Normalize(term == track.Artist + " " + track.Album ? track.Album : track.Title);
            pick = candidates.FirstOrDefault(a => SyncController.Normalize(a["title"]?.GetValue<string>() ?? "") == wantedAlbum)
                ?? candidates.FirstOrDefault(a => SyncController.Normalize(a["title"]?.GetValue<string>() ?? "").Contains(wantedAlbum));
            if (pick != null) break;
        }
        if (pick == null)
        {
            track.Tries++;
            track.NextUtc = DateTime.UtcNow.AddHours(6);
            if (track.Tries >= 3) track.State = "notfound";
            return;
        }

        var foreignAlbumId = pick["foreignAlbumId"]?.GetValue<string>() ?? "";
        var existing = (await LidarrAsync(HttpMethod.Get, "album?foreignAlbumId=" + Uri.EscapeDataString(foreignAlbumId), ct).ConfigureAwait(false) as JsonArray)
            ?.OfType<JsonObject>().FirstOrDefault();
        int albumId;
        if (existing != null)
        {
            albumId = existing["id"]?.GetValue<int>() ?? 0;
            if (!requestedAlbums.Contains(albumId))
            {
                await LidarrAsync(HttpMethod.Put, "album/monitor", ct, new JsonObject { ["albumIds"] = new JsonArray(albumId), ["monitored"] = true }).ConfigureAwait(false);
                await LidarrAsync(HttpMethod.Post, "command", ct, new JsonObject { ["name"] = "AlbumSearch", ["albumIds"] = new JsonArray(albumId) }).ConfigureAwait(false);
            }
        }
        else
        {
            var root = await RootFolderAsync(ct).ConfigureAwait(false);
            pick["monitored"] = true;
            pick["addOptions"] = new JsonObject { ["searchForNewAlbum"] = true };
            if (pick["artist"] is JsonObject artist && (artist["id"]?.GetValue<int>() ?? 0) == 0)
            {
                artist["monitored"] = true;
                artist["monitorNewItems"] = "none";
                artist["qualityProfileId"] = root.Quality;
                artist["metadataProfileId"] = root.Metadata;
                artist["rootFolderPath"] = root.Path;
                artist["addOptions"] = new JsonObject { ["monitor"] = "none", ["searchForMissingAlbums"] = false };
            }
            var added = await LidarrAsync(HttpMethod.Post, "album", ct, pick).ConfigureAwait(false);
            albumId = added?["id"]?.GetValue<int>() ?? 0;
        }
        track.LidarrAlbumId = albumId;
        track.State = "requested";
        track.NextUtc = DateTime.MaxValue;
        requestedAlbums.Add(albumId);
        _logger.LogInformation("pending: album « {Album} » demandé à Lidarr pour « {Title} »", pick["title"]?.GetValue<string>(), track.Title);
    }

    private static async Task<(string Path, int Quality, int Metadata)> RootFolderAsync(CancellationToken ct)
    {
        var roots = (await LidarrAsync(HttpMethod.Get, "rootfolder", ct).ConfigureAwait(false) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var root = roots.FirstOrDefault(r => string.Equals(r["path"]?.GetValue<string>(), Config.LidarrRootFolder, StringComparison.Ordinal)) ?? roots.FirstOrDefault()
            ?? throw new InvalidOperationException("Lidarr: aucun dossier racine");
        return (root["path"]?.GetValue<string>() ?? "", root["defaultQualityProfileId"]?.GetValue<int>() ?? 1, root["defaultMetadataProfileId"]?.GetValue<int>() ?? 1);
    }
}
