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
    private readonly LyricsService _lyrics;
    private readonly Channel<CatalogTrack> _queue = Channel.CreateUnbounded<CatalogTrack>();
    private readonly ConcurrentDictionary<string, MusicJob> _jobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resolving = new();
    private int _workerStarted;

    public MusicLibraryService(YtMusicService yt, MusicCatalog catalog, ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, LyricsService lyrics, ILogger<MusicLibraryService> logger)
    {
        _lyrics = lyrics;
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

    /** Downloads [track] now with an audio id already chosen (legacy single-track route). */
    public Task<string> DownloadWithAudioAsync(CatalogTrack track, string videoId, CancellationToken ct)
    {
        if (Store.CatalogVideo(track.Id) == null) { Store.SetCatalogVideo(track.Id, videoId); Store.Save(); }
        return DownloadAsync(track, ct);
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

        var artistDir = Path.GetFullPath(Path.Combine(root, Safe(ArtistNames.Primary(track.Artist), "Artiste inconnu")));
        var albumDir = Path.GetFullPath(Path.Combine(artistDir, Safe(track.Album, "Titres")));
        if (!albumDir.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Chemin refusé.");
        Directory.CreateDirectory(albumDir);

        var prefix = track.TrackNumber > 0 ? track.TrackNumber.ToString("00") + " - " : "";
        var target = Path.Combine(albumDir, prefix + Safe(track.Title, track.Id) + ".m4a");
        if (File.Exists(target))
        {
            Store.SetCatalogFile(track.Id, target);
            Store.Save();
            await WriteLyricsAsync(track, target, ct).ConfigureAwait(false);
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
            if (!await TagAsync(raw, File.Exists(cover) ? cover : null, track, year, tagged, ct).ConfigureAwait(false))
                File.Move(raw, target, true);
            else
                File.Move(tagged, target, true);
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
        await WriteLyricsAsync(track, target, ct).ConfigureAwait(false);
        QueueScan(albumDir, root);
        _logger.LogInformation("music download: {File}", target);
        return target;
    }

    /// <summary>Writes clean tags (and the cover as attached picture) with Jellyfin's ffmpeg; false when it failed.</summary>
    private async Task<bool> TagAsync(string input, string? cover, CatalogTrack track, string year, string output, CancellationToken ct)
    {
        try
        {
            await YtMusicService.RunProcessAsync(Ffmpeg, list =>
            {
                list.Add("-y");
                list.Add("-v");
                list.Add("error");
                list.Add("-i");
                list.Add(input);
                if (cover != null) { list.Add("-i"); list.Add(cover); }
                list.Add("-map");
                list.Add("0:a");
                if (cover != null) { list.Add("-map"); list.Add("1:v"); list.Add("-c:v"); list.Add("mjpeg"); list.Add("-disposition:v:0"); list.Add("attached_pic"); }
                list.Add("-c:a");
                list.Add("copy");
                list.Add("-map_metadata");
                list.Add("-1");
                void Meta(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) { list.Add("-metadata"); list.Add(key + "=" + value); } }
                Meta("title", track.Title);
                Meta("artist", track.Artist);
                Meta("album_artist", ArtistNames.Primary(track.Artist));
                Meta("album", track.Album);
                Meta("date", year);
                if (track.TrackNumber > 0) Meta("track", track.TrackNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (track.DiscNumber > 0) Meta("disc", track.DiscNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
                list.Add("-f");
                list.Add("mp4");
                list.Add(output);
            }, ct, 120).ConfigureAwait(false);
            return File.Exists(output);
        }
        catch (Exception error)
        {
            // Tagging is a bonus: keep the plain audio rather than losing the download.
            _logger.LogWarning("music tagging failed for {Track}: {Message}", track.Id, error.Message);
            TryDelete(output);
            return false;
        }
    }

    /// <summary>Lyrics sidecar next to the audio (.lrc synced, .txt plain) for Jellyfin's lyrics.</summary>
    private async Task WriteLyricsAsync(CatalogTrack track, string audioPath, CancellationToken ct)
    {
        try
        {
            var stem = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
            if (File.Exists(stem + ".lrc") || File.Exists(stem + ".txt")) return;
            var entry = await _lyrics.GetAsync(track.Artist, track.Title, track.Album, track.Duration, ct).ConfigureAwait(false);
            var sidecar = LyricsService.Sidecar(entry);
            if (sidecar == null) return;
            await File.WriteAllTextAsync(stem + sidecar.Value.Extension, sidecar.Value.Content, ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogDebug("lyrics sidecar failed for {Track}: {Message}", track.Id, error.Message);
        }
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

    private static readonly string Ffprobe =
        File.Exists("/usr/lib/jellyfin-ffmpeg/ffprobe") ? "/usr/lib/jellyfin-ffmpeg/ffprobe" : "ffprobe";

    private static readonly SemaphoreSlim MigrateGate = new(1, 1);

    private static async Task<Dictionary<string, string>> ProbeTagsAsync(string path, CancellationToken ct)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = await YtMusicService.RunProcessAsync(Ffprobe, list =>
            {
                list.Add("-v"); list.Add("error");
                list.Add("-show_entries"); list.Add("format_tags");
                list.Add("-of"); list.Add("json");
                list.Add(path);
            }, ct, 30).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var t))
                foreach (var p in t.EnumerateObject())
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String) tags[p.Name] = p.Value.GetString() ?? "";
        }
        catch { }
        return tags;
    }

    /// <summary>One file handled by the library migration.</summary>
    public sealed record MigratedFile(string From, string To, string Artist, string Album, bool Matched, bool Lyrics);

    /**
     * Re-files the online-music folder: every track under its main artist only (featured artists
     * stay in the title and artist tag), Artist/Album/NN - Title.m4a with clean tags, embedded
     * cover, cover.jpg, artist picture and lyrics sidecar; yt-dlp leftovers removed, empty folders
     * deleted, then one library scan. Idempotent: a clean file is only checked for lyrics.
     */
    public async Task<List<MigratedFile>> MigrateAsync(CancellationToken ct)
    {
        var folder = Plugin.Instance?.Configuration.YtMusicFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("Aucun dossier musique configuré.");
        var root = Path.GetFullPath(folder).TrimEnd('/');
        var done = new List<MigratedFile>();
        if (!Directory.Exists(root)) return done;
        if (!await MigrateGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new InvalidOperationException("Migration déjà en cours.");
        try
        {
            var files = Directory.EnumerateFiles(root, "*.m4a", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith(".nflix-", StringComparison.Ordinal))
                .ToList();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, file).Split('/');
                if (rel.Length < 2) continue;
                var folderArtist = rel[0];
                var tags = await ProbeTagsAsync(file, ct).ConfigureAwait(false);
                tags.TryGetValue("title", out var tagTitle);
                tags.TryGetValue("artist", out var tagArtist);
                tags.TryGetValue("album_artist", out var tagAlbumArtist);
                tags.TryGetValue("album", out var tagAlbum);
                var primary = ArtistNames.Primary(string.IsNullOrWhiteSpace(tagAlbumArtist) ? folderArtist : tagAlbumArtist);
                var clean = rel.Length == 3
                    && Safe(primary, "") == folderArtist
                    && !string.IsNullOrWhiteSpace(tagTitle)
                    && string.Equals(tagAlbumArtist, primary, StringComparison.Ordinal);
                var stem = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                if (clean)
                {
                    var hadLyrics = File.Exists(stem + ".lrc") || File.Exists(stem + ".txt");
                    if (!hadLyrics)
                    {
                        var t = new CatalogTrack { Title = tagTitle!, Artist = string.IsNullOrWhiteSpace(tagArtist) ? primary : tagArtist!, Album = tagAlbum ?? "" };
                        await WriteLyricsAsync(t, file, ct).ConfigureAwait(false);
                        if (File.Exists(stem + ".lrc") || File.Exists(stem + ".txt"))
                            done.Add(new MigratedFile(file, file, primary, tagAlbum ?? "", true, true));
                    }
                    continue;
                }

                var fileTitle = Path.GetFileNameWithoutExtension(file);
                if (fileTitle.Length > 5 && char.IsDigit(fileTitle[0]) && char.IsDigit(fileTitle[1]) && fileTitle[2..].StartsWith(" - ", StringComparison.Ordinal)) fileTitle = fileTitle[5..];
                var title = string.IsNullOrWhiteSpace(tagTitle) ? fileTitle : tagTitle!;
                var credit = string.IsNullOrWhiteSpace(tagArtist) ? folderArtist.Replace('，', ',') : tagArtist!;
                CatalogTrack? match = null;
                try { match = await _catalog.FindTrackAsync(credit, title, ct).ConfigureAwait(false); } catch { }
                var track = match ?? new CatalogTrack { Title = title, Artist = ArtistNames.Primary(credit), Album = tagAlbum ?? "" };
                if (match != null && track.AlbumId.Length > 0)
                {
                    try
                    {
                        var album = await _catalog.AlbumAsync(track.AlbumId, ct).ConfigureAwait(false);
                        var at = album.Tracks.FirstOrDefault(x => x.Id == track.Id);
                        if (at != null) { track.TrackNumber = at.TrackNumber; track.DiscNumber = at.DiscNumber; }
                        if (track.Cover.Length == 0) track.Cover = album.Cover;
                    }
                    catch { }
                }
                var artistDir = Path.GetFullPath(Path.Combine(root, Safe(ArtistNames.Primary(track.Artist), "Artiste inconnu")));
                var albumDir = Path.GetFullPath(Path.Combine(artistDir, Safe(track.Album, "Titres")));
                if (!albumDir.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(albumDir);
                var prefix = track.TrackNumber > 0 ? track.TrackNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " - " : "";
                var target = Path.Combine(albumDir, prefix + Safe(track.Title, "Titre") + ".m4a");

                var cover = Path.Combine(albumDir, "cover.jpg");
                if (!File.Exists(cover) && track.Cover.StartsWith("https://", StringComparison.Ordinal))
                    await SaveImageAsync(track.Cover, cover, ct).ConfigureAwait(false);
                await SaveArtistPictureAsync(track, artistDir, ct).ConfigureAwait(false);
                var oldPicture = Path.Combine(root, folderArtist, "folder.jpg");
                var newPicture = Path.Combine(artistDir, "folder.jpg");
                if (!File.Exists(newPicture) && File.Exists(oldPicture)) File.Copy(oldPicture, newPicture);

                var year = "";
                try { if (track.AlbumId.Length > 0) year = (await _catalog.AlbumAsync(track.AlbumId, ct).ConfigureAwait(false)).Year; } catch { }
                var temp = Path.Combine(albumDir, ".nflix-migrate-" + Guid.NewGuid().ToString("N") + ".m4a");
                if (await TagAsync(file, File.Exists(cover) ? cover : null, track, year, temp, ct).ConfigureAwait(false))
                {
                    File.Move(temp, target, true);
                    if (!string.Equals(file, target, StringComparison.Ordinal)) TryDelete(file);
                }
                else if (!string.Equals(file, target, StringComparison.Ordinal))
                {
                    File.Move(file, target, true);
                }
                // yt-dlp leftovers of the old single-track route.
                foreach (var ext in new[] { ".webm", ".webp", ".jpg", ".part", ".ytdl" })
                    if (!string.Equals(stem + ext, cover, StringComparison.Ordinal)) TryDelete(stem + ext);
                foreach (var ext in new[] { ".lrc", ".txt" })
                {
                    var newStem = Path.Combine(albumDir, Path.GetFileNameWithoutExtension(target));
                    if (File.Exists(stem + ext) && !File.Exists(newStem + ext)) File.Move(stem + ext, newStem + ext);
                }

                Store.MovePath(file, target);
                if (match != null) Store.SetCatalogFile(track.Id, target);
                await WriteLyricsAsync(track, target, ct).ConfigureAwait(false);
                var targetStem = Path.Combine(albumDir, Path.GetFileNameWithoutExtension(target));
                done.Add(new MigratedFile(file, target, ArtistNames.Primary(track.Artist), track.Album, match != null, File.Exists(targetStem + ".lrc") || File.Exists(targetStem + ".txt")));
            }
            Store.Save();

            // Empty folders left behind (deepest first); never the root itself.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                    if (entries.All(e => Path.GetFileName(e) is "folder.jpg") && !Directory.EnumerateFiles(dir, "*.m4a", SearchOption.AllDirectories).Any()
                        && Path.GetRelativePath(root, dir).Split('/').Length == 1 && Safe(ArtistNames.Primary(Path.GetFileName(dir)), "") != Path.GetFileName(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                    else if (entries.Count == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch { }
            }
            if (done.Count > 0) QueueScan(root, root);
            _logger.LogInformation("music migration: {Count} file(s) handled", done.Count);
            return done;
        }
        finally
        {
            MigrateGate.Release();
        }
    }
}
