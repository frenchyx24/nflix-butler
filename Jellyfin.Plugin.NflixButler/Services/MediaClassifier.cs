using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>What a media file looks like once its name has been read.</summary>
public sealed record ParsedMedia(
    string Kind,
    string Title,
    int? Year,
    int Season,
    int Episode,
    bool IsAnimation,
    string FileName);

/// <summary>
/// Reads a file name the way a person would, and builds the canonical path Jellyfin expects.
/// Pure functions only: no IO, so the rules stay testable and predictable.
/// </summary>
public static class MediaClassifier
{
    private static readonly Regex SeasonEpisode = new(
        @"(?i)\bS(?<s>\d{1,2})[\s._-]*E(?<e>\d{1,3})\b|\b(?<s2>\d{1,2})x(?<e2>\d{1,3})\b|(?i)\bseason[\s._-]*(?<s3>\d{1,2})[\s._-]*(episode|ep|e)[\s._-]*(?<e3>\d{1,3})\b",
        RegexOptions.Compiled);

    private static readonly Regex LoneEpisode = new(@"(?i)\bep?[\s._-]*(?<e>\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex SeasonWord = new(@"(?i)\b(season|saison|s)[\s._-]*(?<s>\d{1,2})\b", RegexOptions.Compiled);
    private static readonly Regex YearParen = new(@"\((?<y>19\d{2}|20\d{2})\)", RegexOptions.Compiled);
    private static readonly Regex YearAny = new(@"(?<![\d])(?<y>19\d{2}|20\d{2})(?![\d])", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"\[[^\]]*\]|\{[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex QualityNoise = new(
        @"(?i)\b(2160p|1440p|1080p|1080i|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|hdtv|dvdrip|remux|x264|x265|h264|h265|hevc|avc|10bit|8bit|hdr|dv|dolby|vision|atmos|ddp?5[.\s]?1|aac|ac3|dts|truehd|proper|repack|extended|unrated|multi|french|truefrench|vf|vff|vfq|subfrench|subbed|dubbed|amzn|nf|dsnp|hmax|itunes)\b",
        RegexOptions.Compiled);

    /// <summary>Reads a media file name, using its folders when the name alone is not enough.</summary>
    public static ParsedMedia Parse(string path, IEnumerable<string> animeKeywords, bool inheritedAnimation = false)
    {
        var ext = Path.GetExtension(path);
        var rawName = Path.GetFileNameWithoutExtension(path);
        var parent = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var isAnimation = inheritedAnimation || MatchesAnimation(path, animeKeywords);

        var (season, episode) = ReadSeasonEpisode(rawName, parent);
        var cleaned = Clean(rawName);

        if (season == null && episode > 0)
        {
            var parentSeason = ReadSeasonWord(parent);
            if (parentSeason > 0)
            {
                season = parentSeason;
            }
        }

        if (season != null)
        {
            var seriesFolder = SeriesFolder(path);
            var (seriesTitle, seriesYear) = SplitTitleYear(seriesFolder ?? cleaned);
            var title = string.IsNullOrWhiteSpace(seriesTitle) ? cleaned : seriesTitle;
            return new ParsedMedia(
                Kind: "episode",
                Title: title,
                Year: seriesYear,
                Season: season.Value < 1 ? 1 : season.Value,
                Episode: Math.Max(1, episode),
                IsAnimation: isAnimation,
                FileName: rawName);
        }

        var (movieTitle, year) = SplitTitleYear(cleaned);
        return new ParsedMedia("movie", movieTitle, year, 0, 0, isAnimation, rawName);
    }

    /// <summary>The canonical path Jellyfin indexes without ambiguity.</summary>
    public static string TargetPath(ParsedMedia media, string libraryRoot, string libraryName)
    {
        if (media.Kind == "episode")
        {
            var series = SafeName(media.Title);
            var folder = media.Year is null ? series : $"{series} ({media.Year})";
            var seasonFolder = $"Season {media.Season:00}";
            var fileName = $"{series} S{media.Season:00}E{media.Episode:00}";
            var ext = Path.GetExtension(media.FileName.Length > 0 ? media.FileName : "video.mkv");
            var folderPath = Path.Combine(libraryRoot, libraryName, folder, seasonFolder);
            return Path.Combine(folderPath, fileName + (ext.Length > 0 ? ext : ".mkv"));
        }

        var movieFolder = media.Year is null ? SafeName(media.Title) : $"{SafeName(media.Title)} ({media.Year})";
        var movieExt = Path.GetExtension(media.FileName.Length > 0 ? media.FileName : "video.mkv");
        return Path.Combine(libraryRoot, libraryName, movieFolder, movieFolder + (movieExt.Length > 0 ? movieExt : ".mkv"));
    }

    /// <summary>Which library a parsed file belongs to.</summary>
    public static string PickLibrary(ParsedMedia media, Configuration.PluginConfiguration config) =>
        media.IsAnimation ? config.AnimeLibrary
            : media.Kind == "episode" ? config.SeriesLibrary
            : config.MovieLibrary;

    /// <summary>Strips release noise and keeps a name a metadata provider can search.</summary>
    public static string Clean(string name)
    {
        var text = Brackets.Replace(name, " ");
        text = QualityNoise.Replace(text, " ");
        text = text.Replace('.', ' ').Replace('_', ' ');
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text.Trim(' ', '-', '–', '—');
    }

    /// <summary>True when the file or one of its folders carries an animation keyword.</summary>
    public static bool MatchesAnimation(string path, IEnumerable<string> animeKeywords)
    {
        var haystack = " " + path.ToLowerInvariant().Replace('/', ' ').Replace('\\', ' ') + " ";
        return animeKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Any(k => haystack.Contains(k.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
        }

        var cleaned = Regex.Replace(builder.ToString(), @"\s{2,}", " ").Trim();
        return cleaned.Length == 0 ? "Sans titre" : cleaned;
    }

    private static (string Title, int? Year) SplitTitleYear(string cleaned)
    {
        var paren = YearParen.Match(cleaned);
        if (paren.Success && int.TryParse(paren.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parenYear))
        {
            var title = cleaned[..paren.Index].Trim(' ', '-');
            return (string.IsNullOrWhiteSpace(title) ? cleaned : title, parenYear);
        }

        var any = YearAny.Match(cleaned);
        if (any.Success && int.TryParse(any.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            var title = (cleaned[..any.Index] + " " + cleaned[(any.Index + any.Length)..]).Trim(' ', '-');
            return (string.IsNullOrWhiteSpace(title) ? cleaned : title, year);
        }

        return (cleaned.Trim(), null);
    }

    private static (int? Season, int Episode) ReadSeasonEpisode(string name, string parent)
    {
        var match = SeasonEpisode.Match(name);
        if (match.Success)
        {
            var s = FirstGroup(match, "s", "s2", "s3");
            var e = FirstGroup(match, "e", "e2", "e3");
            if (e > 0)
            {
                return (s > 0 ? s : null, e);
            }
        }

        var lone = LoneEpisode.Match(name);
        if (lone.Success && int.TryParse(lone.Groups["e"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var onlyEpisode))
        {
            return (null, onlyEpisode);
        }

        return (null, 0);
    }

    private static int FirstGroup(Match match, params string[] names)
    {
        foreach (var name in names)
        {
            var group = match.Groups[name];
            if (group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static int ReadSeasonWord(string folder)
    {
        var match = SeasonWord.Match(folder);
        return match.Success && int.TryParse(match.Groups["s"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var season) ? season : 0;
    }

    /// <summary>Walks up until the season folder is behind us, then returns the series folder name.</summary>
    private static string? SeriesFolder(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var looksLikeSeason = Regex.IsMatch(name, @"(?i)^(season|saison|s)[\s._-]*\d{1,2}$|(?i)^disc[\s._-]*\d$");
            if (!looksLikeSeason && !Regex.IsMatch(name, @"^\d{4}$"))
            {
                return name;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
