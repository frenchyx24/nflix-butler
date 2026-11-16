using System;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>Thresholds that decide when an item counts as watched.</summary>
/// <param name="FilmMinRemainingSeconds">Films: remaining time that always counts as the credits zone.</param>
/// <param name="FilmRemainingPercent">Films: remaining share of the runtime that counts as the credits zone.</param>
/// <param name="EpisodeMinRemainingSeconds">Episodes: remaining time that always counts as the credits zone.</param>
/// <param name="EpisodeRemainingPercent">Episodes: remaining share of the runtime that counts as the credits zone.</param>
public sealed record WatchedThresholds(
    int FilmMinRemainingSeconds = 480,
    double FilmRemainingPercent = 10,
    int EpisodeMinRemainingSeconds = 150,
    double EpisodeRemainingPercent = 8);

/// <summary>Outcome of a watched decision.</summary>
/// <param name="Watched">True when the item must be marked as played.</param>
/// <param name="Reason">Short machine-readable reason, shown in the status endpoint.</param>
/// <param name="ZoneStartTicks">Start of the credits zone used for the decision, 0 when unknown.</param>
public readonly record struct WatchedDecision(bool Watched, string Reason, long ZoneStartTicks);

/// <summary>
/// Pure decision logic for "watched" marking. No Jellyfin dependency, so it can be unit tested.
/// Order: a known credits start (media segment or chapter) wins; else an adaptive threshold by
/// runtime. Never watched below 50 % of the runtime, unless the next episode started after the
/// viewer had reached the credits zone.
/// </summary>
public static class WatchedRules
{
    /// <summary>Minimum share of the runtime that must be reached by the last position.</summary>
    public const double MinimumProgress = 0.5;

    /// <summary>Computes where the credits zone starts, in ticks.</summary>
    public static long ZoneStart(long runtimeTicks, long? creditsStartTicks, bool isEpisode, WatchedThresholds t)
    {
        if (runtimeTicks <= 0)
        {
            return 0;
        }

        // A credits segment/chapter only counts when it sits inside the runtime and past the middle.
        if (creditsStartTicks is long credits && credits > 0 && credits < runtimeTicks
            && credits >= (long)(runtimeTicks * MinimumProgress))
        {
            return credits;
        }

        var minSeconds = isEpisode ? t.EpisodeMinRemainingSeconds : t.FilmMinRemainingSeconds;
        var percent = isEpisode ? t.EpisodeRemainingPercent : t.FilmRemainingPercent;
        var remaining = Math.Max(
            TimeSpan.FromSeconds(Math.Max(0, minSeconds)).Ticks,
            (long)(runtimeTicks * Math.Clamp(percent, 0, 50) / 100.0));
        return Math.Max((long)(runtimeTicks * MinimumProgress), runtimeTicks - remaining);
    }

    /// <summary>Decides whether an item is watched.</summary>
    /// <param name="runtimeTicks">Item runtime.</param>
    /// <param name="positionTicks">Last known playback position.</param>
    /// <param name="creditsStartTicks">Start of the credits (Outro segment or credits chapter), if known.</param>
    /// <param name="isEpisode">True for episodes, false for films.</param>
    /// <param name="nextEpisodeStarted">True when the next episode of the same series started.</param>
    /// <param name="maxPositionTicks">Furthest position reached in the session (handles seek-back before autoplay).</param>
    /// <param name="t">Thresholds.</param>
    public static WatchedDecision Decide(
        long runtimeTicks,
        long positionTicks,
        long? creditsStartTicks,
        bool isEpisode,
        bool nextEpisodeStarted,
        long maxPositionTicks,
        WatchedThresholds t)
    {
        if (runtimeTicks <= 0)
        {
            return new WatchedDecision(false, "no-runtime", 0);
        }

        var zone = ZoneStart(runtimeTicks, creditsStartTicks, isEpisode, t);
        var source = creditsStartTicks is long c && c == zone ? "credits" : "threshold";
        var half = (long)(runtimeTicks * MinimumProgress);

        if (positionTicks >= zone && positionTicks >= half)
        {
            return new WatchedDecision(true, source, zone);
        }

        var furthest = Math.Max(positionTicks, maxPositionTicks);
        if (isEpisode && nextEpisodeStarted && furthest >= zone)
        {
            return new WatchedDecision(true, "next-episode", zone);
        }

        return new WatchedDecision(false, positionTicks < half ? "below-half" : "before-credits", zone);
    }

    /// <summary>True when a chapter name designates end credits.</summary>
    public static bool IsCreditsChapter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var n = name.Trim().ToLowerInvariant();
        return n.Contains("credits", StringComparison.Ordinal)
            || n.Contains("générique", StringComparison.Ordinal)
            || n.Contains("generique", StringComparison.Ordinal)
            || n.Contains("end credit", StringComparison.Ordinal)
            || n == "end"
            || n == "fin";
    }
}
