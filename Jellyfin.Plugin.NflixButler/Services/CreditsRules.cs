using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>A credits start found for one item, as cached on disk.</summary>
public sealed class CreditsInfo
{
    /// <summary>Gets or sets the credits start in ticks; null when no source found it.</summary>
    public long? StartTicks { get; set; }

    /// <summary>Gets or sets the credits end in ticks, when the source knows it.</summary>
    public long? EndTicks { get; set; }

    /// <summary>Gets or sets where the start came from (segment, chapter, aniskip, theintrodb, introdb, fingerprint, blackdetect, none).</summary>
    public string Source { get; set; } = "none";

    /// <summary>Gets or sets a short free-form detail (ids, match length...).</summary>
    public string? Detail { get; set; }

    /// <summary>Gets or sets the runtime the answer applies to; a different file invalidates the cache.</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Gets or sets when the lookup ran.</summary>
    public DateTime Utc { get; set; }

    /// <summary>Gets or sets a value indicating whether local detection already ran for this item.</summary>
    public bool LocalTried { get; set; }

    /// <summary>Gets or sets a value indicating whether the start was written as an Outro media segment.</summary>
    public bool SegmentStored { get; set; }

    /// <summary>Gets or sets the cache schema version that produced this record; older records are re-queried.</summary>
    public int Schema { get; set; }
}

/// <summary>One black interval reported by ffmpeg blackdetect, in seconds relative to the analysed window.</summary>
public readonly record struct BlackInterval(double Start, double End);

/// <summary>A common audio segment between two fingerprints, in fingerprint points.</summary>
public readonly record struct FingerprintMatch(int StartA, int EndA, int StartB, int EndB)
{
    /// <summary>Gets the match length in points.</summary>
    public int Length => EndA - StartA;
}

/// <summary>
/// Pure credits logic: parsing of public database answers and ffmpeg output, and the local
/// detection algorithms. No Jellyfin dependency, so it is unit tested.
/// </summary>
public static class CreditsRules
{
    /// <summary>Seconds covered by one chromaprint point (4096 / 3 samples at 11025 Hz).</summary>
    public const double FingerprintPointSeconds = 0.1238;

    /// <summary>Minimum common audio, in seconds, to accept a fingerprint match as credits.</summary>
    public const double MinCommonSeconds = 15;

    private static readonly Regex BlackLine = new(
        @"black_start:\s*(?<s>-?[0-9.]+)\s+black_end:\s*(?<e>-?[0-9.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Picks the ending start from an AniSkip v2 answer. Results whose episode length is closest to
    /// <paramref name="runtimeSeconds"/> win; a small length gap is absorbed by anchoring on the end.
    /// Returns (start, end) in seconds, or null.
    /// </summary>
    public static (double Start, double End)? PickAniSkip(string json, double runtimeSeconds)
    {
        if (string.IsNullOrWhiteSpace(json) || runtimeSeconds <= 0)
        {
            return null;
        }

        var candidates = new List<(double Start, double End, double Length)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True
                || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var r in results.EnumerateArray())
            {
                var type = r.TryGetProperty("skipType", out var t) ? t.GetString() : null;
                if (type is not ("ed" or "mixed-ed") || !r.TryGetProperty("interval", out var iv))
                {
                    continue;
                }

                var start = iv.GetProperty("startTime").GetDouble();
                var end = iv.GetProperty("endTime").GetDouble();
                var length = r.TryGetProperty("episodeLength", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : 0;
                candidates.Add((start, end, length));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }

        foreach (var c in candidates.OrderBy(c => c.Length > 0 ? Math.Abs(c.Length - runtimeSeconds) : double.MaxValue))
        {
            var shift = 0.0;
            if (c.Length > 0)
            {
                var gap = runtimeSeconds - c.Length;
                if (Math.Abs(gap) > 90)
                {
                    continue; // Another cut of the episode: the times do not apply.
                }

                if (Math.Abs(gap) > 5)
                {
                    shift = gap; // Credits are anchored on the end of the episode.
                }
            }

            var start = c.Start + shift;
            var end = Math.Min(runtimeSeconds, c.End + shift);
            if (IsPlausibleStart(start, runtimeSeconds, 0.3))
            {
                return (start, end > start ? end : runtimeSeconds);
            }
        }

        return null;
    }

    /// <summary>Reads the credits start of a TheIntroDB v2 /media answer, in seconds.</summary>
    public static (double Start, double? End)? PickTheIntroDb(string json, double runtimeSeconds)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("credits", out var credits) || credits.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var c in credits.EnumerateArray())
            {
                if (!c.TryGetProperty("start_ms", out var s) || s.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var start = s.GetDouble() / 1000.0;
                double? end = c.TryGetProperty("end_ms", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() / 1000.0 : null;
                if (IsPlausibleStart(start, runtimeSeconds, 0.3))
                {
                    return (start, end);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }

        return null;
    }

    /// <summary>Reads the outro start of an IntroDB /segments answer, in seconds. Low-confidence entries are ignored.</summary>
    public static (double Start, double? End)? PickIntroDb(string json, double runtimeSeconds, double minConfidence = 0.5)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("outro", out var outro) || outro.ValueKind != JsonValueKind.Object
                || !outro.TryGetProperty("start_ms", out var s) || s.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (outro.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number && conf.GetDouble() < minConfidence)
            {
                return null;
            }

            var start = s.GetDouble() / 1000.0;
            double? end = outro.TryGetProperty("end_ms", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() / 1000.0 : null;
            return IsPlausibleStart(start, runtimeSeconds, 0.3) ? (start, end) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A credits start is plausible past <paramref name="minRatio"/> of the runtime and at least 5 s before the end.</summary>
    /// <remarks>
    /// Curated databases use 0.3: Jellyfin runtimes are sometimes nearly double the real duration (file duration
    /// from ffprobe can disagree), and a 0.5 floor then rejects a correct 1356 s answer for a 1532 s episode.
    /// Local detection keeps 0.5 because a fingerprint match can be wrong.
    /// </remarks>
    public static bool IsPlausibleStart(double startSeconds, double runtimeSeconds, double minRatio = 0.5)
        => runtimeSeconds > 0 && startSeconds >= runtimeSeconds * minRatio && startSeconds < runtimeSeconds - 5;

    /// <summary>Parses blackdetect lines from ffmpeg stderr.</summary>
    public static List<BlackInterval> ParseBlackDetect(string stderr)
    {
        var list = new List<BlackInterval>();
        foreach (Match m in BlackLine.Matches(stderr ?? string.Empty))
        {
            if (double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                && double.TryParse(m.Groups["e"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) && e > s)
            {
                list.Add(new BlackInterval(s, e));
            }
        }

        return list;
    }

    /// <summary>
    /// Film credits from black frames in the analysed tail window: the earliest black start from
    /// which the rest of the window is mostly black (credits on black, sparse text) for at least
    /// <paramref name="minCreditsSeconds"/>. Returns seconds relative to the window, or null.
    /// </summary>
    public static double? PickFilmCreditsFromBlack(
        IReadOnlyList<BlackInterval> intervals, double windowSeconds, double minCoverage = 0.6, double minCreditsSeconds = 60)
    {
        if (intervals.Count == 0 || windowSeconds <= 0)
        {
            return null;
        }

        var sorted = intervals.OrderBy(i => i.Start).ToList();
        foreach (var candidate in sorted)
        {
            var from = candidate.Start;
            var rest = windowSeconds - from;
            if (rest < minCreditsSeconds)
            {
                break;
            }

            var black = sorted.Where(i => i.End > from).Sum(i => Math.Min(i.End, windowSeconds) - Math.Max(i.Start, from));
            if (black / rest >= minCoverage)
            {
                return from;
            }
        }

        return null;
    }

    /// <summary>Converts raw chromaprint output (little-endian uint32) into points.</summary>
    public static uint[] ParseRawFingerprint(byte[] raw)
    {
        var points = new uint[raw.Length / 4];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = BitConverter.ToUInt32(raw, i * 4);
        }

        return points;
    }

    /// <summary>
    /// Longest audio segment shared by two fingerprints (credits song of two episodes). Candidate
    /// alignments come from exact point matches; points differing by at most
    /// <paramref name="maxBitDiff"/> bits are similar; gaps up to <paramref name="maxGapPoints"/> are bridged.
    /// </summary>
    public static FingerprintMatch? FindCommonSegment(
        uint[] a, uint[] b, int maxBitDiff = 6, int maxGapPoints = 28, double minSeconds = MinCommonSeconds)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return null;
        }

        var index = new Dictionary<uint, List<int>>();
        for (var j = 0; j < b.Length; j++)
        {
            if (!index.TryGetValue(b[j], out var l))
            {
                index[b[j]] = l = new List<int>();
            }

            l.Add(j);
        }

        // shift = j - i; vote for alignments that share exact points.
        var votes = new Dictionary<int, int>();
        for (var i = 0; i < a.Length; i++)
        {
            if (index.TryGetValue(a[i], out var js))
            {
                foreach (var j in js)
                {
                    votes[j - i] = votes.GetValueOrDefault(j - i) + 1;
                }
            }
        }

        var shifts = votes.Where(v => v.Value >= 2).OrderByDescending(v => v.Value).Take(40)
            .SelectMany(v => new[] { v.Key - 1, v.Key, v.Key + 1 }).Distinct();
        var minPoints = (int)Math.Ceiling(minSeconds / FingerprintPointSeconds);
        FingerprintMatch? best = null;
        foreach (var shift in shifts)
        {
            var i0 = Math.Max(0, -shift);
            var i1 = Math.Min(a.Length, b.Length - shift);
            int runStart = -1, lastGood = -1;
            for (var i = i0; i <= i1; i++)
            {
                var good = i < i1 && BitOperations.PopCount(a[i] ^ b[i + shift]) <= maxBitDiff;
                if (good)
                {
                    if (runStart < 0 || i - lastGood > maxGapPoints)
                    {
                        Close();
                        runStart = i;
                    }

                    lastGood = i;
                }
                else if (i == i1)
                {
                    Close();
                }
            }

            void Close()
            {
                if (runStart >= 0 && lastGood - runStart + 1 >= minPoints
                    && (best is null || lastGood + 1 - runStart > best.Value.Length))
                {
                    best = new FingerprintMatch(runStart, lastGood + 1, runStart + shift, lastGood + 1 + shift);
                }

                runStart = -1;
            }
        }

        return best;
    }

    /// <summary>
    /// Credits start of every episode of a season from tail fingerprints: each episode is matched
    /// with its neighbours; the longest match wins. Keys are episode positions in the list; values
    /// are seconds relative to each tail window.
    /// </summary>
    public static Dictionary<int, double> SeasonCreditsFromFingerprints(IReadOnlyList<uint[]> tails)
    {
        var best = new Dictionary<int, (double Start, int Length)>();
        for (var k = 0; k + 1 < tails.Count; k++)
        {
            if (FindCommonSegment(tails[k], tails[k + 1]) is not { } m)
            {
                continue;
            }

            Offer(k, m.StartA, m.Length);
            Offer(k + 1, m.StartB, m.Length);
        }

        return best.ToDictionary(x => x.Key, x => x.Value.Start);

        void Offer(int episode, int startPoint, int length)
        {
            if (!best.TryGetValue(episode, out var cur) || length > cur.Length)
            {
                best[episode] = (startPoint * FingerprintPointSeconds, length);
            }
        }
    }
}
