using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>State of one catalogue track in the download queue.</summary>
public sealed class MusicJob
{
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string State { get; set; } = "queued"; // queued | running | done | failed
    public string Message { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/**
 * Turns catalogue tracks into real library files: resolves the matching audio, downloads it in
 * the background (one at a time, the HTTP request returns immediately), writes clean tags and the
 * album cover with Jellyfin's own ffmpeg, files it as Artist/Album/NN - Title.m4a next to
 * cover.jpg (and the artist picture as folder.jpg), then asks Jellyfin to scan that folder.
 */
public sealed class MusicLibraryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly string Ffmpeg =
        File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg") ? "/usr/lib/jellyfin-ffmpeg/ffmpeg" : "ffmpeg";

    private static readonly string[] Variants = { "remix", "live", "acoustic", "instrumental", "karaoke", "cover", "slowed", "sped up", "nightcore", "8d", "reverb", "edit" };

    private readonly YtMusicService _yt;
    private readonly MusicCatalog _catalog;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MusicLibraryService> _logger;
    private readonly Channel<CatalogTrack> _queue = Channel.CreateUnbounded<CatalogTrack>();
    private readonly ConcurrentDictionary<string, MusicJob> _jobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resolving = new();
    private int _workerStarted;

    public MusicLibraryService(YtMusicService yt, MusicCatalog catalog, ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<MusicLibraryService> logger)
    {
        _yt = yt;
        _catalog = catalog;
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public YtStore Store => _yt.Store;

    /** Audio id for a catalogue track: artist + title search, best title match, cached forever. */
    public async Task<string> ResolveAsync(CatalogTrack track, CancellationToken ct)
    {
        var known = Store.CatalogVideo(track.Id);
        if (known != null) return known;
        var gate = _resolving.GetOrAdd(track.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            known = Store.CatalogVideo(track.Id);
            if (known != null) return known;
            var hits = (await _yt.SearchAsync(track.Artist + " " + track.Title, 10, ct).ConfigureAwait(false))
                .Where(h => h.Kind == "track" && CallerIdentity.IsVideoId(h.VideoId)).ToList();
            if (hits.Count == 0) throw new InvalidOperationException("Aucune source audio pour ce titre.");
            var wanted = MusicCatalog.Norm(StripBrackets(track.Title));
            var wantedFull = MusicCatalog.Norm(track.Title);
            var artist = MusicCatalog.Norm(track.Artist);
            YtHit best = hits[0];
            var bestScore = int.MinValue;
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                var title = MusicCatalog.Norm(hit.Title);
                var score = 20 - (i * 2);
                if (title == wantedFull || title == wanted) score += 100;
                else if (title.Contains(wanted) || wanted.Contains(title)) score += 50;
                if (artist.Length > 0 && MusicCatalog.Norm(hit.Subtitle).Contains(artist)) score += 30;
                foreach (var v in Variants)
                    if (title.Contains(v) && !wantedFull.Contains(v)) score -= 60;
                if (score > bestScore) { bestScore = score; best = hit; }
            }
            Store.SetCatalogVideo(track.Id, best.VideoId);
            Store.Save();
            return best.VideoId;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string StripBrackets(string value)
    {
        var cut = value.IndexOfAny(new[] { '(', '[' });
        var head = cut > 0 ? value[..cut] : value;
        var dash = head.IndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? head[..dash] : head).Trim();
    }

    /// <summary>File of a catalogue track when it is (still) in the library.</summary>
    public string? FileOf(string trackId)
    {
        var path = Store.CatalogFile(trackId);
        return path != null && File.Exists(path) ? path : null;
    }

    /// <summary>Jellyfin item id of a downloaded catalogue track, when the scan already picked it up.</summary>
    public Guid? ItemOf(string trackId)
    {
        var path = FileOf(trackId);
        if (path == null) return null;
        var item = _library.FindByPath(path, false);
        return item?.Id;
    }

    public MusicJob? Job(string trackId) => _jobs.TryGetValue(trackId, out var job) ? job : null;

    /** Queues [tracks]; already present or already queued tracks are skipped. Returns how many were queued. */
    public int Enqueue(IEnumerable<CatalogTrack> tracks)
    {
        EnsureWorker();
        var count = 0;
        foreach (var track in tracks)
        {
            if (track.Id.Length == 0 || FileOf(track.Id) != null) continue;
            if (_jobs.TryGetValue(track.Id, out var existing) && existing.State is "queued" or "running") continue;
            _jobs[track.Id] = new MusicJob { TrackId = track.Id, Title = track.Title, Artist = track.Artist };
            _queue.Writer.TryWrite(track);
            count++;
        }
        return count;
    }

    private void EnsureWorker()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 1) return;
        _ = Task.Run(WorkerAsync);
    }

    private async Task WorkerAsync()
    {
        await foreach (var track in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var job = _jobs.GetOrAdd(track.Id, _ => new MusicJob { TrackId = track.Id, Title = track.Title, Artist = track.Artist });
            job.State = "running";
            job.UpdatedUtc = DateTime.UtcNow;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                job.Path = await DownloadAsync(track, cts.Token).ConfigureAwait(false);
                job.State = "done";
                job.Message = "";
            }
            catch (Exception error)
            {
                job.State = "failed";
                job.Message = error is TimeoutException ? "Source trop lente." : "Téléchargement impossible.";
                _logger.LogWarning("music download failed for {Track}: {Message}", track.Id, error.Message);
            }
            job.UpdatedUtc = DateTime.UtcNow;
            // Finished jobs older than an hour are forgotten (the file itself is the state).
            foreach (var old in _jobs.Values.Where(j => j.State is "done" or "failed" && DateTime.UtcNow - j.UpdatedUtc > TimeSpan.FromHours(1)).ToList())
                _jobs.TryRemove(old.TrackId, out _);
        }
    }

    internal static string Safe(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\\' ? '_' : c).ToArray())
            .Trim().Trim('.').Trim();
        if (clean.Length > 120) clean = clean[..120].Trim();
        return clean is "" or "." or ".." ? fallback : clean;
    }

    private async Task<string> DownloadAsync(CatalogTrack track, CancellationToken ct)
    {
        var folder = Plugin.Instance?.Configuration.YtMusicFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Aucun dossier musique configuré.");
        var root = Path.GetFullPath(folder).TrimEnd('/');

        // Album tracks carry the cover and position; a bare search hit may not.
        if (track.AlbumId.Length > 0 && (track.TrackNumber == 0 || track.Cover.Length == 0))
        {
            try
            {
                var album = await _catalog.AlbumAsync(track.AlbumId, ct).ConfigureAwait(false);
                var match = album.Tracks.FirstOrDefault(t => t.Id == track.Id);
                if (match != null) { track.TrackNumber = match.TrackNumber; track.DiscNumber = match.DiscNumber; }
                if (track.Cover.Length == 0) track.Cover = album.Cover;
                if (track.Album.Length == 0) track.Album = album.Title;
            }
            catch { }
        }

        var artistDir = Path.GetFullPath(Path.Combine(root, Safe(track.Artist, "Artiste inconnu")));
        var albumDir = Path.GetFullPath(Path.Combine(artistDir, Safe(track.Album, "Titres")));
        if (!albumDir.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Chemin refusé.");
        Directory.CreateDirectory(albumDir);

        var prefix = track.TrackNumber > 0 ? track.TrackNumber.ToString("00") + " - " : "";
        var target = Path.Combine(albumDir, prefix + Safe(track.Title, track.Id) + ".m4a");
        if (File.Exists(target))
        {
            Store.SetCatalogFile(track.Id, target);
            Store.Save();
            return target;
        }

        var videoId = await ResolveAsync(track, ct).ConfigureAwait(false);
        var raw = Path.Combine(albumDir, ".nflix-" + track.Id + ".raw.m4a");
        var tagged = Path.Combine(albumDir, ".nflix-" + track.Id + ".tag.m4a");
        try
        {
            await YtMusicService.RunProcessAsync(YtMusicService.Binary, list =>
            {
                list.Add("-q");
                list.Add("--no-warnings");
                list.Add("--no-playlist");
                list.Add("--force-overwrites");
                list.Add("-f");
                list.Add("bestaudio[ext=m4a]/bestaudio");
                list.Add("-o");
                list.Add(raw);
                list.Add("https://www.youtube.com/watch?v=" + videoId);
            }, ct, 600).ConfigureAwait(false);
            if (!File.Exists(raw)) throw new InvalidOperationException("Fichier audio absent après téléchargement.");

            // Cover next to the album (Jellyfin picks cover.jpg) and embedded in the file.
            var cover = Path.Combine(albumDir, "cover.jpg");
            if (!File.Exists(cover) && track.Cover.StartsWith("https://", StringComparison.Ordinal))
                await SaveImageAsync(track.Cover, cover, ct).ConfigureAwait(false);
            await SaveArtistPictureAsync(track, artistDir, ct).ConfigureAwait(false);

            var year = "";
            try { if (track.AlbumId.Length > 0) year = (await _catalog.AlbumAsync(track.AlbumId, ct).ConfigureAwait(false)).Year; } catch { }
            var hasCover = File.Exists(cover);
            try
            {
                await YtMusicService.RunProcessAsync(Ffmpeg, list =>
                {
                    list.Add("-y");
                    list.Add("-v");
                    list.Add("error");
                    list.Add("-i");
                    list.Add(raw);
                    if (hasCover) { list.Add("-i"); list.Add(cover); }
                    list.Add("-map");
                    list.Add("0:a");
                    if (hasCover) { list.Add("-map"); list.Add("1:v"); list.Add("-c:v"); list.Add("mjpeg"); list.Add("-disposition:v:0"); list.Add("attached_pic"); }
                    list.Add("-c:a");
                    list.Add("copy");
                    void Meta(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) { list.Add("-metadata"); list.Add(key + "=" + value); } }
                    Meta("title", track.Title);
                    Meta("artist", track.Artist);
                    Meta("album_artist", track.Artist);
                    Meta("album", track.Album);
                    Meta("date", year);
                    if (track.TrackNumber > 0) Meta("track", track.TrackNumber.ToString());
                    if (track.DiscNumber > 0) Meta("disc", track.DiscNumber.ToString());
                    list.Add("-f");
                    list.Add("mp4");
                    list.Add(tagged);
                }, ct, 120).ConfigureAwait(false);
                File.Move(tagged, target, true);
            }
            catch (Exception error)
            {
                // Tagging is a bonus: keep the plain audio rather than losing the download.
                _logger.LogWarning("music tagging failed for {Track}: {Message}", track.Id, error.Message);
                File.Move(raw, target, true);
            }
        }
        finally
        {
            TryDelete(raw);
            TryDelete(tagged);
        }

        Store.SetCatalogFile(track.Id, target);
        Store.SetCatalogVideo(track.Id, videoId);
        var item = Store.Item(videoId) ?? new YtItem { VideoId = videoId };
        item.DownloadedPath = target;
        if (item.Title.Length == 0) item.Title = track.Title;
        if (item.Artist.Length == 0) item.Artist = track.Artist;
        Store.PutItem(item);
        Store.Save();
        QueueScan(albumDir, root);
        _logger.LogInformation("music download: {File}", target);
        return target;
    }

    private async Task SaveArtistPictureAsync(CatalogTrack track, string artistDir, CancellationToken ct)
    {
        var picture = Path.Combine(artistDir, "folder.jpg");
        if (File.Exists(picture) || track.ArtistId.Length == 0) return;
        try
        {
            var artist = await _catalog.ArtistAsync(track.ArtistId, ct).ConfigureAwait(false);
            if (artist.Picture.StartsWith("https://", StringComparison.Ordinal))
                await SaveImageAsync(artist.Picture, picture, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task SaveImageAsync(string url, string path, CancellationToken ct)
    {
        var uri = new Uri(url);
        // Covers only come from the catalogue CDN.
        if (!(uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".deezer.com", StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            var bytes = await Http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
            if (bytes.Length < 512 || bytes.Length > 15_000_000) return;
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Refreshes the closest folder Jellyfin already knows (new artist folders appear on the parent scan).</summary>
    private void QueueScan(string dir, string root)
    {
        var current = dir;
        while (current.Length >= root.Length)
        {
            var folderItem = _library.FindByPath(current, true);
            if (folderItem != null)
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                    ImageRefreshMode = MetadataRefreshMode.Default,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false,
                };
                _providers.QueueRefresh(folderItem.Id, options, RefreshPriority.High);
                return;
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        var top = _library.FindByPath(Path.GetDirectoryName(root) ?? root, true);
        if (top != null)
            _providers.QueueRefresh(top.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
    }
}
