using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// Finds where the end credits start for one item. Order: Outro media segment, credits chapter,
/// AniSkip (anime), TheIntroDB (films and series), then local ffmpeg detection in the background.
/// Every answer, including "not found", is cached as one JSON file per item in the plugin folder.
/// </summary>
public sealed class CreditsService
{
    // Static fields run in declaration order: timeouts must be initialised before the HttpClient
    // that reads them. HttpTimeout was declared after Http, so CreateClient saw 00:00:00 and the
    // static constructor threw TypeInitializationException on every credits lookup (fixed in 0.8.3).
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NegativeRetry = TimeSpan.FromDays(7);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(3);
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(3, 3);
    private static readonly SemaphoreSlim LocalGate = new(1, 1);

    /// <summary>Cache format version. Bumped from 1 to 2 so stale <c>"Source":"none"</c> files are re-queried (0.8.1).</summary>
    private const int CacheSchema = 2;

    private readonly ILogger<CreditsService> _logger;
    private readonly IServiceProvider _services;
    private readonly ILibraryManager _library;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<Guid, CreditsInfo> _memory = new();
    private readonly ConcurrentDictionary<Guid, byte> _localRunning = new();

    public CreditsService(
        IServiceProvider services,
        ILibraryManager library,
        IApplicationPaths paths,
        ILogger<CreditsService> logger)
    {
        _services = services;
        _library = library;
        _logger = logger;
        _cacheDir = Path.Combine(paths.PluginConfigurationsPath, "NflixButler", "credits");
        Directory.CreateDirectory(_cacheDir);
    }

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private static HttpClient CreateClient()
    {
        // Literal, not the field: a future reorder must not bring the static init crash back.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NflixButler/0.8 (https://github.com/frenchyx24/nflix-butler)");
        return client;
    }

    /// <summary>Credits start in ticks, or null when no source knows it.</summary>
    public async Task<long?> GetCreditsStartTicksAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        var info = await ResolveAsync(item, cancellationToken).ConfigureAwait(false);
        return info.StartTicks;
    }

    /// <summary>Cached credits record of an item, looking it up on first use.</summary>
    public async Task<CreditsInfo> ResolveAsync(BaseItem item, CancellationToken cancellationToken = default)
    {
        if (_memory.TryGetValue(item.Id, out var memory))
        {
            return memory;
        }

        var cached = Load(item);
        if (cached is not null && IsFresh(cached, item))
        {
            _memory[item.Id] = cached;
            return cached;
        }

        if (!Config.CreditsEnabled)
        {
            return new CreditsInfo { RuntimeTicks = item.RunTimeTicks ?? 0 };
        }

        CreditsInfo info;
        try
        {
            info = await LookupAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler credits: lookup failed for {Item}", item.Name);
            info = new CreditsInfo();
        }

        info.RuntimeTicks = item.RunTimeTicks ?? 0;
        info.Utc = DateTime.UtcNow;
        info.Schema = CacheSchema;
        if (info.StartTicks is not null)
        {
            await TryStoreSegmentAsync(item, info).ConfigureAwait(false);
        }

        Save(item.Id, info);
        _memory[item.Id] = info;
        _logger.LogInformation(
            "credits: {Item} source={Source} start={Start}",
            item.Name,
            string.IsNullOrEmpty(info.Source) ? "none" : info.Source,
            info.StartTicks is long ticks ? ticks.ToString(CultureInfo.InvariantCulture) : "none");

        if (info.StartTicks is null && !info.LocalTried && Config.CreditsLocalDetection && (item.RunTimeTicks ?? 0) > 0)
        {
            QueueLocalDetection(item);
        }

        return info;
    }

    private static bool IsFresh(CreditsInfo info, BaseItem item)
    {
        if (info.Schema != CacheSchema)
        {
            return false;
        }

        if (info.RuntimeTicks != (item.RunTimeTicks ?? 0))
        {
            return false;
        }

        return info.StartTicks is not null || DateTime.UtcNow - info.Utc < NegativeRetry;
    }

    private async Task<CreditsInfo> LookupAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var runtime = Seconds(item.RunTimeTicks ?? 0);
        var half = (item.RunTimeTicks ?? 0) / 2;
        var ids = ResolveIds(item);
        _logger.LogDebug(
            "Nflix Butler credits: ids for {Item} tmdb={Tmdb} tvdb={Tvdb} mal={Mal} anilist={AniList} season={Season} episode={Episode}",
            item.Name, ids.Tmdb, ids.Tvdb, ids.Mal, ids.AniList, ids.Season, ids.Episode);

        // 1. Outro media segment written by Jellyfin or by a previous run of this service.
        if (await FromSegmentAsync(item, half, cancellationToken).ConfigureAwait(false) is { } segment)
        {
            return segment;
        }

        // 2. A chapter named after the credits.
        if (FromChapter(item, half) is { } chapter)
        {
            return chapter;
        }

        // 3. AniSkip, anime episodes only.
        if (item is Episode episode && runtime > 0
            && await FromAniSkipAsync(episode, ids, runtime, cancellationToken).ConfigureAwait(false) is { } ani)
        {
            return ani;
        }

        // 4. TheIntroDB, films and series with a TMDB id.
        if (runtime > 0 && await FromTheIntroDbAsync(item, ids, runtime, cancellationToken).ConfigureAwait(false) is { } intro)
        {
            return intro;
        }

        return new CreditsInfo();
    }

    private async Task<CreditsInfo?> FromSegmentAsync(BaseItem item, long half, CancellationToken cancellationToken)
    {
        try
        {
            if (_services.GetService<IMediaSegmentManager>() is not { } segments)
            {
                return null;
            }

            var found = await segments.GetSegmentsAsync(
                item, new[] { MediaSegmentType.Outro }, _library.GetLibraryOptions(item), false).ConfigureAwait(false);
            var best = found
                .Where(s => s.Type == MediaSegmentType.Outro && s.StartTicks > 0 && s.StartTicks >= half)
                .OrderBy(s => s.StartTicks)
                .FirstOrDefault();
            if (best is null)
            {
                return null;
            }

            return new CreditsInfo
            {
                StartTicks = best.StartTicks,
                EndTicks = best.EndTicks > best.StartTicks ? best.EndTicks : null,
                Source = "segment",
                Detail = "media segment",
                SegmentStored = true,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: no media segments for {Item}", item.Name);
            return null;
        }
    }

    private CreditsInfo? FromChapter(BaseItem item, long half)
    {
        try
        {
            var chapters = _services.GetService<IChapterManager>()?.GetChapters(item.Id);
            var chapter = chapters?
                .Where(c => WatchedRules.IsCreditsChapter(c.Name) && c.StartPositionTicks >= half && c.StartPositionTicks > 0)
                .OrderBy(c => c.StartPositionTicks)
                .FirstOrDefault();
            if (chapter is null)
            {
                return null;
            }

            return new CreditsInfo
            {
                StartTicks = chapter.StartPositionTicks,
                Source = "chapter",
                Detail = chapter.Name,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: no chapters for {Item}", item.Name);
            return null;
        }
    }

    private async Task<CreditsInfo?> FromAniSkipAsync(Episode episode, ItemIds ids, double lengthSeconds, CancellationToken cancellationToken)
    {
        var malId = ids.Mal;
        if (malId is null && ids.AniList is int aniListId)
        {
            malId = await AniListToMalAsync(aniListId, cancellationToken).ConfigureAwait(false);
        }

        if (malId is null)
        {
            return null;
        }

        var number = ids.Episode ?? episode.IndexNumber ?? 1;
        var exact = (int)Math.Round(lengthSeconds);
        foreach (var length in new[] { exact, exact - 60, exact + 60 })
        {
            if (length <= 0)
            {
                continue;
            }

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.aniskip.com/v2/skip-times/{0}/{1}?types=ed&types=mixed-ed&episodeLength={2}",
                malId, number, length);
            var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            if (json is null || CreditsRules.PickAniSkip(json, lengthSeconds) is not { } times)
            {
                continue;
            }

            return ToInfo(times.Start, times.End, "aniskip", string.Format(CultureInfo.InvariantCulture, "MAL {0} ep {1} len {2}", malId, number, length));
        }

        return null;
    }

    private async Task<int?> AniListToMalAsync(int aniListId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["query"] = "query($id:Int){Media(id:$id,type:ANIME){idMal}}",
            ["variables"] = new JsonObject { ["id"] = aniListId },
        }.ToJsonString();

        var json = await PostStringAsync("https://graphql.anilist.co", body, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("Media", out var media)
                && media.TryGetProperty("idMal", out var mal)
                && mal.ValueKind == JsonValueKind.Number)
            {
                var value = mal.GetInt32();
                return value > 0 ? value : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }

        return null;
    }

    private async Task<CreditsInfo?> FromTheIntroDbAsync(BaseItem item, ItemIds ids, double runtime, CancellationToken cancellationToken)
    {
        if (ids.Tmdb is not int tmdbId || tmdbId <= 0)
        {
            return null;
        }

        var (type, season, episode) = item switch
        {
            Episode => ("tv", ids.Season, ids.Episode),
            Movie => ("movie", null, null),
            _ => (string.Empty, null, null),
        };
        if (type.Length == 0)
        {
            return null;
        }

        var query = new StringBuilder();
        query.Append("?tmdb_id=").Append(tmdbId).Append("&type=").Append(type);
        if (season is int s)
        {
            query.Append("&season=").Append(s);
        }

        if (episode is int e)
        {
            query.Append("&episode=").Append(e);
        }

        foreach (var version in new[] { "v4", "v2" })
        {
            var url = "https://api.theintrodb.org/" + version + "/media" + query;
            var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            if (json is null)
            {
                continue;
            }

            var times = CreditsRules.PickTheIntroDb(json, runtime) ?? CreditsRules.PickIntroDb(json, runtime);
            if (times is { } t)
            {
                return ToInfo(t.Start, t.End, "theintrodb", "tmdb " + tmdbId.ToString(CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    private static CreditsInfo ToInfo(double startSeconds, double? endSeconds, string source, string? detail) => new()
    {
        StartTicks = TimeSpan.FromSeconds(startSeconds).Ticks,
        EndTicks = endSeconds is double end && end > startSeconds ? TimeSpan.FromSeconds(end).Ticks : null,
        Source = source,
        Detail = detail,
    };

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);
            using var response = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Nflix Butler credits: {Url} returned {Status}", url, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug("Nflix Butler credits: {Url} failed ({Message})", url, ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<string?> PostStringAsync(string url, string body, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogDebug("Nflix Butler credits: POST {Url} failed ({Message})", url, ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private void QueueLocalDetection(BaseItem item)
    {
        if (!_localRunning.TryAdd(item.Id, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(LocalTimeout);
                var info = await DetectLocalAsync(item, cts.Token).ConfigureAwait(false);
                info.RuntimeTicks = item.RunTimeTicks ?? 0;
                info.LocalTried = true;
                info.Utc = DateTime.UtcNow;
                Save(item.Id, info);
                _memory[item.Id] = info;
                _logger.LogInformation(
                    "Nflix Butler credits: local detection for {Item} -> {Source} {Start}",
                    item.Name, info.Source, info.StartTicks is long t ? TimeSpan.FromTicks(t).ToString(@"hh\:mm\:ss") : "none");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nflix Butler credits: local detection failed for {Item}", item.Name);
            }
            finally
            {
                _localRunning.TryRemove(item.Id, out _);
            }
        });
    }

    private async Task<CreditsInfo> DetectLocalAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await LocalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = item.Path;
            var runtime = Seconds(item.RunTimeTicks ?? 0);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || runtime < 60)
            {
                return new CreditsInfo();
            }

            var info = item is Episode episode
                ? await DetectSeriesAsync(episode, path, runtime, cancellationToken).ConfigureAwait(false)
                : await DetectFilmAsync(path, runtime, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                await TryStoreSegmentAsync(item, info).ConfigureAwait(false);
                return info;
            }

            return new CreditsInfo();
        }
        finally
        {
            LocalGate.Release();
        }
    }

    private async Task<CreditsInfo?> DetectFilmAsync(string path, double runtime, CancellationToken cancellationToken)
    {
        var window = Math.Min(runtime, Math.Min(900, Math.Max(300, runtime * 0.15)));
        var from = Math.Max(0, runtime - window);
        var (exit, stderr) = await RunFfmpegAsync(
            new[]
            {
                "-hide_banner", "-nostdin", "-ss", from.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-t", window.ToString("0.###", CultureInfo.InvariantCulture),
                "-an", "-threads", "1", "-vf", "blackdetect=d=0.1:pix_th=0.10", "-f", "null", "-",
            },
            cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }

        var intervals = CreditsRules.ParseBlackDetect(stderr);
        if (CreditsRules.PickFilmCreditsFromBlack(intervals, window) is not double offset)
        {
            return null;
        }

        var start = from + offset;
        return CreditsRules.IsPlausibleStart(start, runtime)
            ? ToInfo(start, runtime, "blackdetect", "black frames")
            : null;
    }

    private async Task<CreditsInfo?> DetectSeriesAsync(Episode episode, string path, double runtime, CancellationToken cancellationToken)
    {
        var tail = Math.Min(360, runtime * 0.5);
        if (tail < 90)
        {
            return null;
        }

        var index = episode.IndexNumber ?? 0;
        var sibling = SiblingEpisode(episode, index > 1 ? -1 : 1);
        if (sibling?.Path is not string otherPath || !File.Exists(otherPath) || sibling.RunTimeTicks is not long otherRuntime || otherRuntime <= 0)
        {
            return null;
        }

        var from = runtime - tail;
        var otherFrom = Math.Max(0, Seconds(otherRuntime) - tail);
        var mine = await FingerprintAsync(path, from, tail, cancellationToken).ConfigureAwait(false);
        var other = await FingerprintAsync(otherPath, otherFrom, tail, cancellationToken).ConfigureAwait(false);
        if (mine.Length == 0 || other.Length == 0)
        {
            return null;
        }

        if (CreditsRules.FindCommonSegment(other, mine) is not { } match)
        {
            return null;
        }

        var start = from + (match.StartB * CreditsRules.FingerprintPointSeconds);
        return CreditsRules.IsPlausibleStart(start, runtime)
            ? ToInfo(start, runtime, "fingerprint", match.Length.ToString(CultureInfo.InvariantCulture) + " points")
            : null;
    }

    private BaseItem? SiblingEpisode(Episode episode, int delta)
    {
        var index = episode.IndexNumber ?? 0;
        var target = index + delta;
        if (index <= 0 || target <= 0 || episode.SeasonId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return _library.GetItemList(new InternalItemsQuery
            {
                ParentId = episode.SeasonId,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = false,
            }).FirstOrDefault(x => x.IndexNumber == target);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<uint[]> FingerprintAsync(string path, double from, double length, CancellationToken cancellationToken)
    {
        var (exit, _, stdout) = await RunFfmpegRawAsync(
            new[]
            {
                "-hide_banner", "-nostdin", "-ss", from.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", path, "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
                "-vn", "-threads", "1", "-f", "chromaprint", "-fp_format", "raw", "-",
            },
            cancellationToken).ConfigureAwait(false);
        return exit == 0 && stdout.Length >= 4 ? CreditsRules.ParseRawFingerprint(stdout) : Array.Empty<uint>();
    }

    private async Task TryStoreSegmentAsync(BaseItem item, CreditsInfo info)
    {
        if (!Config.CreditsWriteSegments || info.StartTicks is not long start || info.SegmentStored)
        {
            return;
        }

        try
        {
            if (_services.GetService<IMediaSegmentManager>() is not { } segments || !segments.IsTypeSupported(item))
            {
                return;
            }

            var end = info.EndTicks ?? item.RunTimeTicks ?? start;
            await segments.CreateSegmentAsync(
                new MediaSegmentDto
                {
                    ItemId = item.Id,
                    Type = MediaSegmentType.Outro,
                    StartTicks = start,
                    EndTicks = end > start ? end : start,
                },
                "NflixButler").ConfigureAwait(false);
            info.SegmentStored = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: could not store segment for {Item}", item.Name);
        }
    }

    private string CachePath(Guid itemId) => Path.Combine(_cacheDir, itemId.ToString("N") + ".json");

    private CreditsInfo? Load(BaseItem item)
    {
        try
        {
            var path = CachePath(item.Id);
            return File.Exists(path) ? JsonSerializer.Deserialize<CreditsInfo>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: could not read cache for {Item}", item.Name);
            return null;
        }
    }

    private void Save(Guid itemId, CreditsInfo info)
    {
        try
        {
            var path = CachePath(itemId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(info));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: could not write cache for {ItemId}", itemId);
        }
    }

    private static double Seconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;

    private static string? ProviderId(BaseItem? item, params string[] keys)
    {
        if (item?.ProviderIds is not { } ids)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (ids.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            foreach (var pair in ids)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }
        }

        return null;
    }

    /// <summary>Provider ids and episode coordinates of one item, resolved across its parents.</summary>
    private sealed record ItemIds(int? Tmdb, int? Tvdb, int? Mal, int? AniList, int? Season, int? Episode);

    private static readonly System.Text.RegularExpressions.Regex SeasonEpisodePattern = new(
        @"S(?<s>\d+)E(?<e>\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private ItemIds ResolveIds(BaseItem item)
    {
        var chain = new List<BaseItem?> { item };
        AddParent(chain, item is Episode ep ? ep.SeasonId : Guid.Empty);
        if (item is Episode episode)
        {
            AddParent(chain, episode.SeriesId);
        }

        AddParent(chain, item.ParentId);

        int? Id(params string[] keys)
        {
            foreach (var candidate in chain)
            {
                var value = ProviderId(candidate, keys);
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }

            return null;
        }

        int? season = item.ParentIndexNumber;
        int? number = item.IndexNumber;
        if (season is null || number is null)
        {
            var match = SeasonEpisodePattern.Match((item.Path ?? string.Empty) + " " + (item.Name ?? string.Empty));
            if (match.Success)
            {
                season ??= int.TryParse(match.Groups["s"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
                number ??= int.TryParse(match.Groups["e"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : null;
            }
        }

        return new ItemIds(
            Id("Tmdb", "TMDb"),
            Id("Tvdb"),
            Id("MyAnimeList", "Mal"),
            Id("AniList"),
            season,
            number);
    }

    private void AddParent(List<BaseItem?> chain, Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }

        try
        {
            if (_library.GetItemById(id) is { } parent && chain.All(x => x?.Id != parent.Id))
            {
                chain.Add(parent);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string FfmpegPath() =>
        File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg") ? "/usr/lib/jellyfin-ffmpeg/ffmpeg" : "ffmpeg";

    private async Task<(int Exit, string StdErr)> RunFfmpegAsync(string[] args, CancellationToken cancellationToken)
    {
        var (exit, stderr, _) = await RunFfmpegRawAsync(args, cancellationToken).ConfigureAwait(false);
        return (exit, stderr);
    }

    private async Task<(int Exit, string StdErr, byte[] StdOut)> RunFfmpegRawAsync(string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(FfmpegPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, string.Empty, Array.Empty<byte>());
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var stdout = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdoutTask).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stderr, stdout.ToArray());
        }
        catch (OperationCanceledException)
        {
            return (-1, "timeout", Array.Empty<byte>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nflix Butler credits: ffmpeg failed");
            return (-1, ex.Message, Array.Empty<byte>());
        }
    }
}
