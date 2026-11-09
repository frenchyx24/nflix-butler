using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NflixButler.Services;

/**
 * Artist credit helpers. Online sources often credit "Main, Guest, Guest" or "Main feat. Guest";
 * the library files a track under its main artist only. Names that really contain a comma or an
 * ampersand ("Tyler, The Creator", "Simon & Garfunkel") are kept whole.
 */
public static class ArtistNames
{
    private static readonly Regex Featuring = new(@"\s+(feat\.?|ft\.?|featuring|with|x|avec)\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] CommaNames =
    {
        "tyler, the creator", "earth, wind & fire", "crosby, stills, nash & young", "crosby, stills & nash",
        "emerson, lake & palmer", "peter, paul and mary", "blood, sweat & tears", "now, now", "black country, new road",
    };

    /// <summary>Main artist of a credit ("Daft Punk, Pharrell Williams" → "Daft Punk").</summary>
    public static string Primary(string? credit)
    {
        var value = (credit ?? "").Trim();
        if (value.Length == 0) return "";
        // Full-width comma is the source's way to keep "Tyler， The Creator" in one name.
        value = value.Replace('，', ',').Replace(" - Topic", "", StringComparison.Ordinal).Trim();
        var lower = value.ToLowerInvariant();
        foreach (var name in CommaNames)
            if (lower.StartsWith(name, StringComparison.Ordinal))
                return value[..name.Length].Trim();
        var cut = Featuring.Match(value);
        if (cut.Success && cut.Index > 0) value = value[..cut.Index];
        foreach (var separator in new[] { ", ", " / ", "; ", " · " })
        {
            var i = value.IndexOf(separator, StringComparison.Ordinal);
            if (i > 0) value = value[..i];
        }
        return value.Trim();
    }

    /// <summary>All artists of a credit, main first.</summary>
    public static List<string> All(string? credit)
    {
        var main = Primary(credit);
        var rest = (credit ?? "").Replace('，', ',').Trim();
        if (rest.StartsWith(main, StringComparison.OrdinalIgnoreCase)) rest = rest[main.Length..];
        var parts = Regex.Split(rest, @",\s*|\s+(?:feat\.?|ft\.?|featuring|with|x|avec|&|and|et)\s+|\s*/\s*|;\s*", RegexOptions.IgnoreCase)
            .Select(p => p.Trim()).Where(p => p.Length > 0);
        return new[] { main }.Concat(parts).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Comparison key: case, accents, punctuation and full-width forms ignored.</summary>
    public static string Key(string? name)
    {
        var folded = (name ?? "").Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
