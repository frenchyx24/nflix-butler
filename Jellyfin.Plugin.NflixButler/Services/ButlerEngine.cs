using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.NflixButler.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// The butler itself: snapshot the watched folders, file what is new or misplaced, refresh only the
/// folders it touched, and repair items whose metadata lookup came back empty.
/// </summary>
public sealed class ButlerEngine
{
    private readonly ILibraryManager _library;
    private readonly ILibraryMonitor _monitor;
    private readonly IProviderManager _providers;
    private readonly ILogger<ButlerEngine> _logger;
    private readonly StateStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ButlerEngine(
        ILibraryManager library,
        ILibraryMonitor monitor,
        IProviderManager providers,
        StateStore store,
        ILogger<ButlerEngine> logger)
    {
        _library = library;
        _monitor = monitor;
        _providers = providers;
        _store = store;
        _logger = logger;
    }

    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets a value indicating whether a pass is running.</summary>
    public bool Running { get; private set; }

    /// <summary>Gets the folders currently watched: Jellyfin libraries plus the extra roots.</summary>
    public IReadOnlyList<string> Roots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var folder in _library.GetVirtualFolders())
            {
                foreach (var location in folder.Locations ?? Array.Empty<string>())
                {
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        roots.Add(location);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler: cannot read the library folders");
        }

        foreach (var extra in Configuration.ExtraRoots ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                roots.Add(extra);
            }
        }

        return roots.Where(Directory.Exists).ToList();
    }

    /// <summary>One full pass: detect, file, refresh, repair.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Running = true;
        try
        {
            var config = Configuration;
            if (!config.Enabled)
            {
                return;
            }

            var roots = Roots();
            var seenNow = 0;
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var file in EnumerateVideos(root, config))
                {
                    if (cancellationToken.IsCancellationRequested || seenNow >= 200)
                    {
                        break;
                    }

                    seenNow += await HandleFileAsync(root, file, config, cancellationToken).ConfigureAwait(false);
                }
            }

            _store.State.LastRunUtc = DateTime.UtcNow;
            _store.State.Runs++;
            _store.State.LastError = null;
            _store.Save();

            if (config.FixUnmatched)
            {
                await RepairAsync(config.MaxRepairsPerRun, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _store.State.LastError = ex.Message;
            _store.Save();
            _logger.LogError(ex, "Nflix Butler: pass failed");
        }
        finally
        {
            Running = false;
            _gate.Release();
        }
    }

    /// <summary>Asks the metadata providers to identify items that have no external id.</summary>
    public async Task<int> RepairAsync(int max, CancellationToken cancellationToken)
    {
        var repaired = 0;
        foreach (var item in UnmatchedItems(max * 4).Take(Math.Max(1, max)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryIdentifyAsync(item, cancellationToken).ConfigureAwait(false))
            {
                repaired++;
                _store.State.Repaired++;
            }
        }

        if (repaired > 0)
        {
            _store.Save();
        }

        return repaired;
    }

    /// <summary>Repairs one item, from the dashboard.</summary>
    public async Task<bool> FixItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        return item != null && await TryIdentifyAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-reads one item's folder without touching the rest of the library.</summary>
    public void RefreshItem(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        var path = item?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            _monitor.ReportFileSystemChanged(folder);
        }
    }

    /// <summary>Items without a single external id, newest first.</summary>
    public IReadOnlyList<BaseItem> UnmatchedItems(int limit)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new BaseItemKind[] { BaseItemKind.Movie, BaseItemKind.Series },
            Limit = Math.Clamp(limit, 1, 1000),
        };

        try
        {
            return _library.GetItemList(query)
                .Where(item => item.ProviderIds is null || item.ProviderIds.Count == 0)
                .Where(item => !string.IsNullOrEmpty(item.Path))
                .OrderByDescending(item => item.DateCreated)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler: cannot list unmatched items");
            return Array.Empty<BaseItem>();
        }
    }

    private async Task<bool> TryIdentifyAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item.ProviderIds is { Count: > 0 })
        {
            return false;
        }

        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var parsed = MediaClassifier.Parse(path, Configuration.AnimeKeywords);
        var year = parsed.Year;

        try
        {
            if (item is Movie)
            {
                var search = new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = parsed.Title, Year = year },
                    IncludeDisabledProviders = false,
                };
                var results = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(search, cancellationToken).ConfigureAwait(false);
                var match = results.FirstOrDefault(result => result?.ProviderIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(result?.Name));
                if (match is null)
                {
                    return false;
                }

                Apply(item, match.ProviderIds, match.Name, match.ProductionYear);
            }
            else
            {
                var search = new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = parsed.Title, Year = year },
                    IncludeDisabledProviders = false,
                };
                var results = await _providers.GetRemoteSearchResults<Series, SeriesInfo>(search, cancellationToken).ConfigureAwait(false);
                var match = results.FirstOrDefault(result => result?.ProviderIds is { Count: > 0 } || !string.IsNullOrWhiteSpace(result?.Name));
                if (match is null)
                {
                    return false;
                }

                Apply(item, match.ProviderIds, match.Name, match.ProductionYear);
            }

            await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                _monitor.ReportFileSystemChanged(folder);
            }

            _logger.LogInformation("Nflix Butler: identified {Path} as {Name}", path, item.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler: cannot identify {Path}", path);
            return false;
        }
    }

    private static void Apply(BaseItem target, Dictionary<string, string>? providerIds, string? name, int? year)
    {
        if (providerIds is { Count: > 0 })
        {
            target.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in providerIds)
            {
                target.ProviderIds[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            target.Name = name;
        }

        if (year is > 1800)
        {
            target.ProductionYear = year;
        }
    }

    private async Task<int> HandleFileAsync(string root, string path, PluginConfiguration config, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length < Math.Max(1, config.MinFileSizeMb) * 1024L * 1024L)
            {
                return 0;
            }
        }
        catch (IOException)
        {
            return 0;
        }

        if (_store.AlreadySeen(path, info.Length, info.LastWriteTimeUtc))
        {
            return 0;
        }

        var inheritedAnimation = MediaClassifier.MatchesAnimation(root, config.AnimeKeywords);
        var parsed = MediaClassifier.Parse(path, config.AnimeKeywords, inheritedAnimation);
        var library = MediaClassifier.PickLibrary(parsed, config);
        var entry = new IntakeEntry
        {
            Path = path,
            Kind = parsed.Kind,
            Title = parsed.Title,
            Year = parsed.Year,
            Season = parsed.Season,
            Episode = parsed.Episode,
            Library = library,
            SizeBytes = info.Length,
        };

        var target = MediaClassifier.TargetPath(parsed, root, library);
        var action = "detected";
        var status = "pending";

        if (config.AutoOrganize && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                if (File.Exists(target))
                {
                    action = "duplicate";
                    status = "needs-review";
                    entry.Message = "Destination déjà présente, fichier laissé en place.";
                }
                else
                {
                    File.Move(path, target);
                    action = "filed";
                    status = "ok";
                    entry.Destination = target;
                    _store.State.Filed++;
                    _monitor.ReportFileSystemChanged(Path.GetDirectoryName(target)!);
                    var previous = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
                    {
                        _monitor.ReportFileSystemChanged(previous);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                action = "failed";
                status = "failed";
                entry.Message = ex.Message;
                _logger.LogWarning(ex, "Nflix Butler: cannot file {Path}", path);
            }
        }
        else
        {
            _monitor.ReportFileSystemChanged(Path.GetDirectoryName(path) ?? root);
            status = "ok";
            entry.Message = "Déjà au bon endroit, rafraîchi.";
        }

        entry.Action = action;
        entry.Status = status;
        _store.MarkSeen(path, info.Length, info.LastWriteTimeUtc);
        _store.Add(entry, config.KeepEntries);
        _store.Save();
        await Task.CompletedTask.ConfigureAwait(false);
        return 1;
    }

    private IEnumerable<string> EnumerateVideos(string root, PluginConfiguration config)
    {
        var extensions = new HashSet<string>(config.VideoExtensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(config.SkipFolders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] directories;
            string[] files;
            try
            {
                directories = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (extensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (!skip.Contains(name) && !name.StartsWith(".", StringComparison.Ordinal))
                {
                    pending.Push(directory);
                }
            }
        }
    }
}
