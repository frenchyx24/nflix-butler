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
    public DateTime RequestedUtc { get; set; }

    /// <summary>Direct download by Nflix when Lidarr finds nothing: "", running, done, failed.</summary>
    public string DlState { get; set; } = "";
    public int DlTries { get; set; }
    public bool SourceRetried { get; set; }
    public bool FreshSource { get; set; }
    public bool NeedOtherSource { get; set; }
    public string DlFile { get; set; } = "";
    public DateTime DlNextUtc { get; set; } = DateTime.MinValue;
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
    private readonly MusicLibraryService _music;
    private readonly ILogger<PendingMusicService> _logger;

    public PendingMusicService(IApplicationPaths paths, ILibraryManager library, IPlaylistManager playlists, MusicCatalog catalog, MusicLibraryService music, ILogger<PendingMusicService> logger)
    {
        var dir = Path.Combine(paths.PluginConfigurationsPath, "NflixButler");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "pending.json");
        _library = library;
        _playlists = playlists;
        _catalog = catalog;
        _music = music;
        _logger = logger;
        try { _items = File.Exists(_path) ? JsonSerializer.Deserialize<List<PendingTrack>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch { _items = new(); }
        // Discographies whose downloads a restart cut short resume (already fetched tracks are skipped).
        foreach (var recent in ArtistsDone.Where(a => DateTime.UtcNow - a.Value < TimeSpan.FromDays(2)).Select(a => a.Key).ToList())
        {
            ArtistsDone.Remove(recent);
            if (ArtistNamesSeen.TryGetValue(recent, out var name)) _artistQueue.Enqueue(name);
        }
        // A download cut by a restart starts over.
        foreach (var t in _items.Where(t => t.DlState == "running")) t.DlState = "";
        // Tracks that failed every try on their first audio id get another round with a fresh source lookup.
        foreach (var t in _items.Where(t => t.DlState == "failed" && !t.FreshSource)) { t.DlTries = 1; t.DlNextUtc = DateTime.MinValue; t.FreshSource = true; }
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
        QueueArtists(tracks.Select(t => t.Artist));
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

    // ---- Discographies -------------------------------------------------------------------------

    /// <summary>Alternate takes nobody asked for: whole records of these are skipped.</summary>
    private static readonly System.Text.RegularExpressions.Regex VariantOnly = new(
        @"drumless|instrumental|karaok|a ?cappella|acapella|sped ?up|slowed|8-?bit|lullaby|berceuse|piano version|commentary",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Edition = new(
        @"deluxe|edition|édition|remaster|anniversary|expanded|bonus|live|version|reissue|complete",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private string ArtistsPath => Path.Combine(Path.GetDirectoryName(_path)!, "artists.json");
    private Dictionary<string, DateTime>? _artistsDone;
    private Dictionary<string, string>? _artistNames;

    private string ArtistNamesPath => Path.Combine(Path.GetDirectoryName(_path)!, "artist-names.json");

    /// <summary>Display name of each queued artist key, kept so an interrupted discography can resume.</summary>
    private Dictionary<string, string> ArtistNamesSeen
    {
        get
        {
            if (_artistNames != null) return _artistNames;
            try { _artistNames = File.Exists(ArtistNamesPath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ArtistNamesPath)) : null; }
            catch { }
            return _artistNames ??= new() { ["daftpunk"] = "Daft Punk" };
        }
    }
    private readonly Queue<string> _artistQueue = new();

    private Dictionary<string, DateTime> ArtistsDone
    {
        get
        {
            if (_artistsDone != null) return _artistsDone;
            try { _artistsDone = File.Exists(ArtistsPath) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ArtistsPath)) : null; }
            catch { }
            return _artistsDone ??= new();
        }
    }

    /// <summary>Queues the whole discography of these artists (main credit only, once every 14 days).</summary>
    public void QueueArtists(IEnumerable<string> credits)
    {
        if (!Config.DownloadDiscographies) return;
        lock (_gate)
        {
            foreach (var credit in credits)
            {
                var name = ArtistNames.Primary(credit).Trim();
                var key = ArtistNames.Key(name);
                if (key.Length < 2 || _artistQueue.Any(q => ArtistNames.Key(q) == key)) continue;
                if (ArtistsDone.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromDays(14)) continue;
                _artistQueue.Enqueue(name);
                ArtistNamesSeen[key] = name;
            }
            try { File.WriteAllText(ArtistNamesPath, JsonSerializer.Serialize(ArtistNamesSeen, Options)); } catch { }
        }
        _wake.Release();
    }

    /// <summary>One artist per pass: every album, EP and single the library does not have yet goes to the download queue.</summary>
    private async Task DiscographyStepAsync(CancellationToken ct)
    {
        string? name;
        lock (_gate) name = _artistQueue.Count > 0 ? _artistQueue.Dequeue() : null;
        if (name == null) return;
        var key = ArtistNames.Key(name);
        try
        {
            var artist = await _catalog.FindArtistAsync(name, ct).ConfigureAwait(false);
            // Exact artist only: a near match would download someone else's discography.
            if (artist == null || ArtistNames.Key(artist.Name) != key)
            {
                _logger.LogInformation("discographie : artiste « {Name} » introuvable dans le catalogue", name);
            }
            else
            {
                var albums = (await _catalog.ArtistAlbumsAsync(artist.Id, ct).ConfigureAwait(false))
                    .Where(a => a.Type != "compile" && !VariantOnly.IsMatch(a.Title))
                    // Originals first, so a later "Deluxe" or "Remastered" edition is recognised as the same record.
                    .OrderBy(a => a.Type switch { "album" => 0, "ep" => 1, _ => 2 })
                    .ThenBy(a => Edition.IsMatch(a.Title) ? 1 : 0)
                    .ThenBy(a => a.Year)
                    .ToList();
                var seenTitles = new HashSet<string>();
                var queued = 0;
                foreach (var summary in albums)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seenTitles.Add(SyncController.Normalize(CleanTitle(summary.Title)))) continue;
                    CatalogAlbum album;
                    try { album = await _catalog.AlbumAsync(summary.Id, ct).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { continue; }
                    // Singles already on an album, or tracks already in the library, are skipped.
                    var wanted = album.Tracks.Where(t => !seenTitles.Contains("t:" + SyncController.Normalize(CleanTitle(t.Title)))
                            && SyncController.MatchLocal(_library, t.Title, t.Artist.Length > 0 ? t.Artist : artist.Name) == null)
                        .ToList();
                    foreach (var t in album.Tracks) seenTitles.Add("t:" + SyncController.Normalize(CleanTitle(t.Title)));
                    foreach (var t in wanted)
                    {
                        if (t.Album.Length == 0) t.Album = album.Title;
                        if (t.Cover.Length == 0) t.Cover = album.Cover;
                        if (t.AlbumId.Length == 0) t.AlbumId = album.Id;
                    }
                    queued += _music.Enqueue(wanted);
                }
                _logger.LogInformation("discographie : « {Name} », {Albums} sorties, {Tracks} titres mis en téléchargement", artist.Name, albums.Count, queued);
            }
            lock (_gate)
            {
                ArtistsDone[key] = DateTime.UtcNow;
                File.WriteAllText(ArtistsPath, JsonSerializer.Serialize(ArtistsDone, Options));
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogWarning("discographie : échec pour « {Name} » : {Message}", name, error.Message);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await UpdateDownloaderAsync(stoppingToken).ConfigureAwait(false);
            try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception error) { _logger.LogWarning("pending: pass failed: {Message}", error.Message); }
            bool busy;
            lock (_gate) busy = _items.Count > 0 || _artistQueue.Count > 0;
            try { await _wake.WaitAsync(busy ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false); }
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
            // Playlist deleted by its owner: forget its pending tracks.
            if (!Guid.TryParse(track.PlaylistId, out var owning) || _library.GetItemById(owning) == null)
            {
                lock (_gate) _items.RemoveAll(x => x.Id == track.Id);
                changed = true;
                continue;
            }
            // A track Nflix downloaded itself is found by its file; others by artist + title.
            var local = track.DlFile.Length > 0 ? _library.FindByPath(track.DlFile, false) : null;
            local ??= SyncController.MatchLocal(_library, track.Title, track.Artist);
            if (local == null && CleanTitle(track.Title) is var clean && clean != track.Title)
                local = SyncController.MatchLocal(_library, clean, track.Artist);
            if (local != null && Guid.TryParse(track.PlaylistId, out var pid) && Guid.TryParse(track.UserId, out var uid))
            {
                try
                {
                    int before;
                    lock (_gate) before = _items.Count(x => x.PlaylistId == track.PlaylistId && x.Position < track.Position);
                    // Original order: the slot after every earlier track already in the playlist.
                    // Never twice: a positioned add can throw after it has already written the entry.
                    if (!InPlaylist(pid, local.Id))
                    {
                        try { await _playlists.AddItemToPlaylistAsync(pid, new[] { local.Id }, Math.Max(0, track.Position - before), uid).ConfigureAwait(false); }
                        catch (ArgumentException) when (!InPlaylist(pid, local.Id)) { await _playlists.AddItemToPlaylistAsync(pid, new[] { local.Id }, null, uid).ConfigureAwait(false); }
                    }
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
            // Lidarr is the last resort: only once Nflix could not fetch the track itself.
            if (!(track.DlState == "failed" && track.DlTries >= MaxDirectTries)) continue;
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
        await DirectDownloadsAsync(ct).ConfigureAwait(false);
        await DiscographyStepAsync(ct).ConfigureAwait(false);
    }

    private bool InPlaylist(Guid playlistId, Guid itemId) =>
        _library.GetItemById(playlistId) is MediaBrowser.Controller.Playlists.Playlist playlist
        && playlist.LinkedChildren.Any(c => c.ItemId == itemId);

    private const int MaxDirectTries = 4;

    /// <summary>Title without "(2014 Remaster)", "[Official Video]", "feat." credits or "Artist | … | Music Video" noise.</summary>
    internal static string CleanTitle(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+(feat\.?|ft\.?|featuring)\s.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (t.Contains('|')) t = t.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).OrderByDescending(x => x.Length).First();
        return t.Trim().Length > 1 ? t.Trim() : title;
    }

    /// <summary>
    /// Lidarr only finds whole albums on its indexers; a track it cannot fetch within 20 minutes
    /// (no source, failed or blocked import) is downloaded by Nflix itself from its exact audio id.
    /// </summary>
    private DateTime _updatedUtc = DateTime.MinValue;

    /// <summary>YouTube changes often: the downloader updates itself once a day (standalone binary only).</summary>
    private async Task UpdateDownloaderAsync(CancellationToken ct)
    {
        const string bin = "/config/bin/yt-dlp";
        if (DateTime.UtcNow - _updatedUtc < TimeSpan.FromHours(24) || !File.Exists(bin)) return;
        _updatedUtc = DateTime.UtcNow;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bin)
            {
                ArgumentList = { "-U" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process == null) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            _logger.LogInformation("pending: mise à jour du téléchargeur : {Line}", line.Trim());
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("pending: downloader update failed: {Message}", error.Message);
        }
    }

    private const int Workers = 3;

    private async Task DirectDownloadsAsync(CancellationToken ct)
    {
        List<PendingTrack> due;
        lock (_gate)
            due = _items.Where(t => t.DlState is "" or "failed"
                    && !(t.DlState == "failed" && (t.DlTries >= MaxDirectTries || t.DlNextUtc > DateTime.UtcNow)))
                .OrderBy(t => t.Position).ToList();
        if (due.Count == 0) return;
        // Several tracks at once: each one is a separate small download.
        using var gate = new SemaphoreSlim(Workers);
        var tasks = due.Select(async track =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await DirectOneAsync(track, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _wake.Release();
    }

    private static bool Gone(string message) =>
        message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || message.Contains("not available", StringComparison.OrdinalIgnoreCase)
        || message.Contains("private", StringComparison.OrdinalIgnoreCase) || message.Contains("removed", StringComparison.OrdinalIgnoreCase)
        || message.Contains("autre source", StringComparison.OrdinalIgnoreCase);

    private async Task DirectOneAsync(PendingTrack track, CancellationToken ct)
    {
        track.DlState = "running";
        Save();
        try
        {
            var hit = await _catalog.FindTrackAsync(track.Artist, track.Title, ct).ConfigureAwait(false)
                ?? await _catalog.FindTrackAsync(track.Artist, CleanTitle(track.Title), ct).ConfigureAwait(false);
            var catalogTrack = hit ?? new CatalogTrack { Id = "v" + (track.VideoId.Length > 0 ? track.VideoId : track.Id), Title = CleanTitle(track.Title), Artist = track.Artist, Album = track.Album, Cover = track.Cover, Duration = track.Duration };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(8));
            // Links from Deezer or Spotify carry no audio id: find the track first.
            if (track.VideoId.Length == 0)
            {
                track.VideoId = await _music.ResolveAsync(catalogTrack, cts.Token).ConfigureAwait(false);
                Save();
            }
            var audioId = track.VideoId;
            if (track.NeedOtherSource)
            {
                // The playlist's audio is gone: another source of the same track.
                _music.Store.ForgetCatalogVideo(catalogTrack.Id);
                string? other = null;
                try { other = await _music.ResolveAsync(catalogTrack, cts.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { _logger.LogDebug("pending: no other source for {Title}: {Message}", track.Title, error.Message); }
                _music.Store.ForgetCatalogVideo(catalogTrack.Id);
                if (other == null || other == track.VideoId) throw new InvalidOperationException("aucune autre source");
                audioId = other;
            }
            var file = await _music.DownloadWithAudioAsync(catalogTrack, audioId, cts.Token).ConfigureAwait(false);
            track.DlState = "done";
            track.DlFile = file;
            _logger.LogInformation("pending: « {Title} » téléchargé directement ({File})", track.Title, file);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            track.DlTries++;
            track.DlState = "failed";
            var gone = Gone(error.Message);
            if (gone && !track.NeedOtherSource) { track.NeedOtherSource = true; track.DlNextUtc = DateTime.UtcNow; }
            else track.DlNextUtc = DateTime.UtcNow.AddMinutes(track.DlTries switch { 1 => 1, 2 => 5, _ => 20 });
            _logger.LogWarning("pending: direct download {Try}/{Max} failed for {Title}: {Message}", track.DlTries, MaxDirectTries, track.Title, error.Message);
        }
        Save();
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
        track.RequestedUtc = DateTime.UtcNow;
        track.NextUtc = DateTime.MaxValue;
        requestedAlbums.Add(albumId);
        _logger.LogInformation("pending: album « {Album} » demandé à Lidarr pour « {Title} »", pick["title"]?.GetValue<string>(), track.Title);
    }

    public sealed record Progress(string Label, int Percent, string Kind = "");

    private Dictionary<int, Progress> _queueCache = new();
    private DateTime _queueAt = DateTime.MinValue;
    private readonly Dictionary<int, (DateTime At, int Files, int Total)> _albumStats = new();

    /// <summary>Human status of each pending track, from the Lidarr download queue (cached 10 s).</summary>
    public async Task<Dictionary<string, Progress>> StatusAsync(List<PendingTrack> tracks, CancellationToken ct)
    {
        var result = new Dictionary<string, Progress>();
        if (LidarrConfigured && tracks.Any(t => t.State == "requested") && DateTime.UtcNow - _queueAt > TimeSpan.FromSeconds(10))
        {
            var map = new Dictionary<int, Progress>();
            try
            {
                var queue = await LidarrAsync(HttpMethod.Get, "queue?pageSize=200", ct).ConfigureAwait(false);
                foreach (var r in (queue?["records"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var albumId = r["albumId"]?.GetValue<int>() ?? 0;
                    if (albumId == 0) continue;
                    double size = r["size"]?.GetValue<double>() ?? 0, left = r["sizeleft"]?.GetValue<double>() ?? 0;
                    var pct = size > 0 ? (int)Math.Round(100 * (size - left) / size) : 0;
                    var tracked = r["trackedDownloadState"]?.GetValue<string>() ?? "";
                    var status = (r["status"]?.GetValue<string>() ?? "").ToLowerInvariant();
                    var warn = (r["trackedDownloadStatus"]?.GetValue<string>() ?? "").ToLowerInvariant();
                    var eta = r["timeleft"]?.GetValue<string>() ?? "";
                    Progress p = tracked switch
                    {
                        "importPending" or "importing" => new Progress("Import sur le serveur", 100, "importing"),
                        "failedPending" or "failed" => new Progress("Échec du téléchargement, nouvel essai", -1, "failed"),
                        _ when warn == "warning" && status != "downloading" => new Progress(pct >= 100 ? "Import bloqué, vérification en cours" : "Téléchargement bloqué", pct, "blocked"),
                        _ when status is "queued" or "delay" or "paused" => new Progress("En file de téléchargement", pct, "queued"),
                        _ => new Progress("Téléchargement " + pct + " %" + (eta.Length > 0 && TimeSpan.TryParse(eta, out var t) ? " · " + (t.TotalMinutes >= 1 ? (int)t.TotalMinutes + " min" : "< 1 min") : ""), pct, "downloading"),
                    };
                    if (!map.TryGetValue(albumId, out var prev) || p.Percent > prev.Percent) map[albumId] = p;
                }
                _queueCache = map;
                _queueAt = DateTime.UtcNow;
            }
            catch (Exception error) when (error is not OperationCanceledException) { _logger.LogDebug("pending: queue read failed: {Message}", error.Message); }
        }
        foreach (var t in tracks)
        {
            if (t.DlState == "running") { result[t.Id] = new Progress("Téléchargement en cours", -1, "direct"); continue; }
            if (t.DlState == "done") { result[t.Id] = new Progress("Ajout à la bibliothèque", 100, "files"); continue; }
            if (t.State == "requested" && _queueCache.TryGetValue(t.LidarrAlbumId, out var q)) { result[t.Id] = q; continue; }
            if (t.State == "requested" && t.LidarrAlbumId > 0)
            {
                var files = await AlbumFilesAsync(t.LidarrAlbumId, ct).ConfigureAwait(false);
                if (files > 0) { result[t.Id] = new Progress("Ajout à la bibliothèque", 100, "files"); continue; }
            }
            result[t.Id] = t.State switch
            {
                "requested" => DateTime.UtcNow - t.RequestedUtc < TimeSpan.FromMinutes(30) || t.RequestedUtc == default
                    ? new Progress("Recherche d'une source", -1, "searching")
                    : new Progress("Aucune source pour l'instant", -1, "nosource"),
                "notfound" or "disabled" => new Progress("Lecture en ligne uniquement", -1, "online"),
                _ => new Progress(t.Tries > 0 ? "Nouvel essai bientôt" : "En file de téléchargement", -1, "prep"),
            };
            // A failed direct download overrides the Lidarr wording with its own retry state.
            if (t.DlState == "failed" && !(t.DlTries >= MaxDirectTries && t.State == "requested"))
                result[t.Id] = t.DlTries >= MaxDirectTries
                    ? new Progress("Lecture en ligne uniquement", -1, "online")
                    : new Progress("Nouvel essai du téléchargement (" + t.DlTries + "/" + MaxDirectTries + ")", -1, result[t.Id].Kind);
        }
        return result;
    }

    private async Task<int> AlbumFilesAsync(int albumId, CancellationToken ct)
    {
        lock (_albumStats) { if (_albumStats.TryGetValue(albumId, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Files; }
        try
        {
            var album = await LidarrAsync(HttpMethod.Get, "album/" + albumId, ct).ConfigureAwait(false);
            var files = album?["statistics"]?["trackFileCount"]?.GetValue<int>() ?? 0;
            lock (_albumStats) _albumStats[albumId] = (DateTime.UtcNow, files, 0);
            return files;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return 0; }
    }

    private static async Task<(string Path, int Quality, int Metadata)> RootFolderAsync(CancellationToken ct)
    {
        var roots = (await LidarrAsync(HttpMethod.Get, "rootfolder", ct).ConfigureAwait(false) as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var root = roots.FirstOrDefault(r => string.Equals(r["path"]?.GetValue<string>(), Config.LidarrRootFolder, StringComparison.Ordinal)) ?? roots.FirstOrDefault()
            ?? throw new InvalidOperationException("Lidarr: aucun dossier racine");
        return (root["path"]?.GetValue<string>() ?? "", root["defaultQualityProfileId"]?.GetValue<int>() ?? 1, root["defaultMetadataProfileId"]?.GetValue<int>() ?? 1);
    }
}
