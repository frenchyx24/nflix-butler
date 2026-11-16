using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>Playback state of one (user, item) pair, persisted across restarts.</summary>
public sealed class WatchedEntry
{
    public Guid UserId { get; set; }

    public Guid ItemId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsEpisode { get; set; }

    public Guid SeriesId { get; set; }

    public int? Season { get; set; }

    public int? Index { get; set; }

    public long RuntimeTicks { get; set; }

    public long PositionTicks { get; set; }

    public long MaxPositionTicks { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public bool Done { get; set; }
}

/// <summary>One watched decision, kept in memory for the status endpoint.</summary>
public sealed record WatchedLog(
    DateTime Utc,
    string User,
    Guid ItemId,
    string Item,
    bool Marked,
    string Reason,
    string Source,
    double PositionPercent);

/// <summary>
/// Marks films and episodes as watched when the viewer reached the end credits, even when they
/// skipped them. Hooks playback events of every client, survives lost stop events (periodic sweep)
/// and restarts (state persisted as JSON). Never unmarks; marking is idempotent.
/// </summary>
public sealed class WatchedTracker : IHostedService, IDisposable
{
    private const int MaxLog = 200;
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(10);

    private readonly ISessionManager _sessions;
    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly CreditsService _creditsService;
    private readonly ILogger<WatchedTracker> _logger;
    private readonly string _stateFile;
    private readonly ConcurrentDictionary<string, WatchedEntry> _entries = new();
    private readonly ConcurrentQueue<WatchedLog> _log = new();
    private readonly object _saveLock = new();
    private readonly object _markLock = new();
    private Timer? _timer;
    private DateTime _lastSweepUtc = DateTime.MinValue;
    private int _dirty;
    private int _sweeping;

    public WatchedTracker(
        ISessionManager sessions,
        IUserDataManager userData,
        IUserManager users,
        ILibraryManager library,
        CreditsService creditsService,
        IApplicationPaths paths,
        ILogger<WatchedTracker> logger)
    {
        _sessions = sessions;
        _userData = userData;
        _users = users;
        _library = library;
        _creditsService = creditsService;
        _logger = logger;
        var folder = Path.Combine(paths.PluginConfigurationsPath, "NflixButler");
        Directory.CreateDirectory(folder);
        _stateFile = Path.Combine(folder, "watched-state.json");
    }

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Gets the most recent decisions, newest first.</summary>
    public IReadOnlyList<WatchedLog> Recent => _log.Reverse().ToList();

    /// <summary>Gets the number of tracked (user, item) pairs.</summary>
    public int TrackedCount => _entries.Count;

    /// <summary>Gets the time of the last sweep.</summary>
    public DateTime LastSweepUtc => _lastSweepUtc;

    public static WatchedThresholds Thresholds()
    {
        var c = Config;
        return new WatchedThresholds(
            Math.Max(0, c.WatchedFilmMinRemainingMinutes) * 60,
            c.WatchedFilmRemainingPercent,
            Math.Max(0, c.WatchedEpisodeMinRemainingSeconds),
            c.WatchedEpisodeRemainingPercent);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logger.LogInformation("Nflix Butler watched: tracking {Count} entries", _entries.Count);

        if (Config.WatchedBackfillOnStart)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                var dry = Config.BackfillDryRun;
                await BackfillAsync(dry).ConfigureAwait(false);
                if (!dry && Plugin.Instance is { } plugin)
                {
                    plugin.Configuration.WatchedBackfillOnStart = false;
                    plugin.SaveConfiguration();
                }
            });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Save();
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Handle(e, started: true, stopped: false);

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Handle(e, started: false, stopped: false);

    private void OnStopped(object? sender, PlaybackStopEventArgs e) => Handle(e, started: false, stopped: true);

    private void Handle(PlaybackProgressEventArgs e, bool started, bool stopped)
    {
        try
        {
            if (!Config.WatchedEnabled || e.Item is not (Movie or Episode) || e.Users is null)
            {
                return;
            }

            var item = e.Item;
            foreach (var user in e.Users)
            {
                var entry = Track(user, item, e.PlaybackPositionTicks, started);
                if (started && item is Episode episode)
                {
                    _ = Task.Run(() => CheckPreviousEpisodeAsync(user, episode));
                }

                if (stopped)
                {
                    _ = Task.Run(() => EvaluateAsync(entry, user, item, nextEpisodeStarted: false, "stop"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: playback event failed");
        }
    }

    private static string Key(Guid userId, Guid itemId) => userId.ToString("N") + ":" + itemId.ToString("N");

    private WatchedEntry Track(User user, BaseItem item, long? position, bool started)
    {
        var entry = _entries.GetOrAdd(Key(user.Id, item.Id), _ => new WatchedEntry { UserId = user.Id, ItemId = item.Id });
        lock (entry)
        {
            entry.Name = item.Name ?? string.Empty;
            entry.IsEpisode = item is Episode;
            if (item is Episode ep)
            {
                entry.SeriesId = ep.SeriesId;
                entry.Season = ep.ParentIndexNumber;
                entry.Index = ep.IndexNumber;
            }

            entry.RuntimeTicks = item.RunTimeTicks ?? entry.RuntimeTicks;
            if (started)
            {
                entry.Done = false;
                entry.MaxPositionTicks = 0;
            }

            if (position is long p && p >= 0)
            {
                entry.PositionTicks = p;
                entry.MaxPositionTicks = Math.Max(entry.MaxPositionTicks, p);
            }

            entry.UpdatedUtc = DateTime.UtcNow;
        }

        Interlocked.Exchange(ref _dirty, 1);
        return entry;
    }

    private async Task CheckPreviousEpisodeAsync(User user, Episode current)
    {
        try
        {
            if (current.ParentIndexNumber is not int season || current.IndexNumber is not int index)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-12);
            var previous = _entries.Values
                .Where(x => x.UserId == user.Id && x.IsEpisode && !x.Done && x.SeriesId == current.SeriesId
                    && x.ItemId != current.Id && x.UpdatedUtc >= cutoff
                    && x.Season is int s && x.Index is int i && (s < season || (s == season && i < index)))
                .OrderByDescending(x => x.Season)
                .ThenByDescending(x => x.Index)
                .FirstOrDefault();
            if (previous is null || _library.GetItemById(previous.ItemId) is not { } item)
            {
                return;
            }

            await EvaluateAsync(previous, user, item, nextEpisodeStarted: true, "next-episode").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: next-episode check failed");
        }
    }

    private async Task EvaluateAsync(WatchedEntry entry, User user, BaseItem item, bool nextEpisodeStarted, string source)
    {
        try
        {
            if (entry.Done)
            {
                return;
            }

            var credits = await GetCreditsStartAsync(item).ConfigureAwait(false);
            long runtime, position, max;
            lock (entry)
            {
                runtime = entry.RuntimeTicks > 0 ? entry.RuntimeTicks : item.RunTimeTicks ?? 0;
                position = entry.PositionTicks;
                max = entry.MaxPositionTicks;
            }

            var decision = WatchedRules.Decide(runtime, position, credits, entry.IsEpisode, nextEpisodeStarted, max, Thresholds());
            var marked = decision.Watched && Mark(user, item);
            if (decision.Watched)
            {
                entry.Done = true;
                Interlocked.Exchange(ref _dirty, 1);
            }

            Record(user, item, marked, decision.Watched && !marked ? "already-played" : decision.Reason, source, position, runtime);
            if (source != "sweep")
            {
                Save();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: evaluation failed for {Item}", item.Name);
        }
    }

    /// <summary>Marks an item as played for a user. Returns false when it already was. Never unmarks.</summary>
    private bool Mark(User user, BaseItem item)
    {
        lock (_markLock)
        {
            var data = _userData.GetUserData(user, item);
            if (data is null || data.Played)
            {
                return false;
            }

            data.Played = true;
            data.PlayCount++;
            data.LastPlayedDate = DateTime.UtcNow;
            data.PlaybackPositionTicks = 0;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
            _logger.LogInformation("Nflix Butler watched: marked {Item} as played for {User}", item.Name, user.Username);
            return true;
        }
    }

    private void Record(User user, BaseItem item, bool marked, string reason, string source, long position, long runtime)
    {
        _log.Enqueue(new WatchedLog(
            DateTime.UtcNow,
            user.Username,
            item.Id,
            item.Name ?? string.Empty,
            marked,
            reason,
            source,
            runtime > 0 ? Math.Round(position * 100.0 / runtime, 1) : 0));
        while (_log.Count > MaxLog && _log.TryDequeue(out _))
        {
        }
    }

    /// <summary>Start of the end credits. All sources and caching live in CreditsService.</summary>
    private async Task<long?> GetCreditsStartAsync(BaseItem item)
    {
        return await _creditsService.GetCreditsStartTicksAsync(item).ConfigureAwait(false);
    }

    private void Tick()
    {
        if (DateTime.UtcNow - _lastSweepUtc >= SweepEvery)
        {
            _ = Task.Run(SweepAsync);
        }
        else if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            Save();
        }
    }

    /// <summary>Evaluates idle entries whose stop event may have been lost, then prunes old state.</summary>
    public async Task SweepAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
        {
            return;
        }

        try
        {
            _lastSweepUtc = DateTime.UtcNow;
            var now = DateTime.UtcNow;
            foreach (var (key, entry) in _entries.ToArray())
            {
                var age = now - entry.UpdatedUtc;
                if ((entry.Done && age > TimeSpan.FromDays(1)) || age > TimeSpan.FromDays(30))
                {
                    _entries.TryRemove(key, out _);
                    continue;
                }

                if (entry.Done || age < Idle || !Config.WatchedEnabled)
                {
                    continue;
                }

                var user = _users.GetUserById(entry.UserId);
                var item = _library.GetItemById(entry.ItemId);
                if (user is null || item is null)
                {
                    _entries.TryRemove(key, out _);
                    continue;
                }

                var data = _userData.GetUserData(user, item);
                if (data is null || data.Played)
                {
                    entry.Done = true;
                    continue;
                }

                if (data.PlaybackPositionTicks > 0)
                {
                    lock (entry)
                    {
                        entry.PositionTicks = data.PlaybackPositionTicks;
                        entry.MaxPositionTicks = Math.Max(entry.MaxPositionTicks, data.PlaybackPositionTicks);
                    }
                }

                await EvaluateAsync(entry, user, item, nextEpisodeStarted: false, "sweep").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: sweep failed");
        }
        finally
        {
            Interlocked.Exchange(ref _sweeping, 0);
            Save();
        }
    }

    /// <summary>Marks resume positions already in the credits zone. Dry run only logs.</summary>
    public async Task<IReadOnlyList<WatchedLog>> BackfillAsync(bool dryRun)
    {
        var results = new List<WatchedLog>();
        var thresholds = Thresholds();
        foreach (var user in _users.GetUsers())
        {
            IReadOnlyList<BaseItem> items;
            try
            {
                items = _library.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    IsResumable = true,
                    Recursive = true,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nflix Butler watched: backfill query failed for {User}", user.Username);
                continue;
            }

            foreach (var item in items)
            {
                var data = _userData.GetUserData(user, item);
                if (data is null || data.Played || data.PlaybackPositionTicks <= 0)
                {
                    continue;
                }

                var runtime = item.RunTimeTicks ?? 0;
                var credits = await GetCreditsStartAsync(item).ConfigureAwait(false);
                var decision = WatchedRules.Decide(
                    runtime, data.PlaybackPositionTicks, credits, item is Episode, false, data.PlaybackPositionTicks, thresholds);
                if (!decision.Watched)
                {
                    continue;
                }

                var marked = !dryRun && Mark(user, item);
                var source = dryRun ? "backfill-dry-run" : "backfill";
                _logger.LogInformation(
                    "Nflix Butler watched: {Source} {Item} for {User} at {Pct:F1}% ({Reason})",
                    source, item.Name, user.Username, data.PlaybackPositionTicks * 100.0 / runtime, decision.Reason);
                Record(user, item, marked, decision.Reason, source, data.PlaybackPositionTicks, runtime);
                results.Add(new WatchedLog(
                    DateTime.UtcNow, user.Username, item.Id, item.Name ?? string.Empty, marked, decision.Reason, source,
                    Math.Round(data.PlaybackPositionTicks * 100.0 / runtime, 1)));
            }
        }

        return results;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<WatchedEntry>>(File.ReadAllText(_stateFile)) ?? new List<WatchedEntry>();
            foreach (var e in list)
            {
                _entries[Key(e.UserId, e.ItemId)] = e;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: could not read {File}", _stateFile);
        }
    }

    private void Save()
    {
        try
        {
            lock (_saveLock)
            {
                var json = JsonSerializer.Serialize(_entries.Values.ToList());
                var tmp = _stateFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _stateFile, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nflix Butler watched: could not save {File}", _stateFile);
        }
    }
}
