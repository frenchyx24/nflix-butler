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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// The butler never moves or renames a file. It watches the folders for new arrivals, asks Jellyfin
/// to re-read only the folders that changed, and repairs the items whose identification came back
/// empty so the metadata providers can attach the right IMDb/TMDb entry.
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

    /// <summary>Gets the folders watched: Jellyfin library roots plus the extra roots.</summary>
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

    /// <summary>One pass: detect new files, refresh their folders, then repair unnamed items.</summary>
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

            var budget = Math.Clamp(config.MaxRefreshesPerRun, 1, 200);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in Roots())
            {
                if (cancellationToken.IsCancellationRequested || budget <= 0)
                {
                    break;
                }

                foreach (var file in EnumerateVideos(root, config))
                {
                    if (cancellationToken.IsCancellationRequested || budget <= 0)
                    {
                        break;
                    }

                    if (RecordNewFile(root, file, config))
                    {
                        budget--;
                        var folder = Path.GetDirectoryName(file);
                        if (!string.IsNullOrEmpty(folder) && touched.Add(folder))
                        {
                            // Targeted only: Jellyfin re-reads this folder, never the whole library.
                            _monitor.ReportFileSystemChanged(folder);
                        }
                    }
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

    /// <summary>Asks the metadata providers to identify items that have no external id yet.</summary>
    public async Task<int> RepairAsync(int max, CancellationToken cancellationToken)
    {
        var repaired = 0;
        var candidates = UnmatchedItems(max * 4);
        _logger.LogInformation("Nflix Butler: {Count} item(s) sans identifiant externe", candidates.Count);
        foreach (var item in candidates.Take(Math.Clamp(max, 1, 100)))
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
    public async Task<(bool Fixed, string? Name)> FixItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return (false, null);
        }

        var fixedItem = await TryIdentifyAsync(item, cancellationToken).ConfigureAwait(false);
        return (fixedItem, item.Name);
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
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
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

        try
        {
            if (item is Movie)
            {
                var search = new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = parsed.Title, Year = parsed.Year },
                    IncludeDisabledProviders = false,
                };
                var results = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(search, cancellationToken).ConfigureAwait(false);
                var match = results.FirstOrDefault(result => result?.ProviderIds is { Count: > 0 });
                if (match?.ProviderIds is null)
                {
                    return false;
                }

                Apply(item, match.ProviderIds, match.Name, match.ProductionYear);
            }
            else if (item is Series)
            {
                var search = new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = parsed.Title, Year = parsed.Year },
                    IncludeDisabledProviders = false,
                };
                var results = await _providers.GetRemoteSearchResults<Series, SeriesInfo>(search, cancellationToken).ConfigureAwait(false);
                var match = results.FirstOrDefault(result => result?.ProviderIds is { Count: > 0 });
                if (match?.ProviderIds is null)
                {
                    return false;
                }

                Apply(item, match.ProviderIds, match.Name, match.ProductionYear);
            }
            else
            {
                return false;
            }

            await _library.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            // Jellyfin now knows the external id: let it re-read this folder only.
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

    private static void Apply(BaseItem target, Dictionary<string, string> providerIds, string? name, int? year)
    {
        target.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in providerIds)
        {
            target.ProviderIds[pair.Key] = pair.Value;
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

    /// <summary>Records a file the first time it is seen. Returns true when it is new.</summary>
    private bool RecordNewFile(string root, string path, PluginConfiguration config)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length < Math.Max(1, config.MinFileSizeMb) * 1024L * 1024L)
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }

        if (_store.AlreadySeen(path, info.Length, info.LastWriteTimeUtc))
        {
            return false;
        }

        var parsed = MediaClassifier.Parse(path, config.AnimeKeywords);
        var suggested = MediaClassifier.PickLibrary(parsed, config);
        var current = Path.GetFileName(root);
        var entry = new IntakeEntry
        {
            Path = path,
            Kind = parsed.Kind,
            Title = parsed.Title,
            Year = parsed.Year,
            Season = parsed.Season,
            Episode = parsed.Episode,
            Library = suggested,
            SizeBytes = info.Length,
            Action = "refreshed",
            Status = "ok",
            Message = string.Equals(current, suggested, StringComparison.OrdinalIgnoreCase)
                ? "Dossier rafraîchi."
                : $"Détecté dans « {current} », semble appartenir à « {suggested} ».",
        };

        _store.MarkSeen(path, info.Length, info.LastWriteTimeUtc);
        _store.Add(entry, config.KeepEntries);
        _store.Save();
        _logger.LogInformation("Nflix Butler: new file {Path}", path);
        return true;
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
