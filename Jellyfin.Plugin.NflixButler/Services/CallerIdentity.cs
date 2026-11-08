using System;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// Caller identity and input validation shared by the plugin controllers.
/// Jellyfin 12.4 exposes the user id as the "Jellyfin-UserId" claim (not NameIdentifier).
/// </summary>
public static class CallerIdentity
{
    private static readonly Regex VideoIdRx = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex BrowseIdRx = new("^[A-Za-z0-9_-]{2,64}$", RegexOptions.Compiled);
    private static readonly Regex PlaylistIdRx = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static bool IsVideoId(string? value) => value != null && VideoIdRx.IsMatch(value);

    public static bool IsBrowseId(string? value) => value != null && BrowseIdRx.IsMatch(value);

    public static bool IsPlaylistId(string? value) => value != null && PlaylistIdRx.IsMatch(value);

    /// <summary>Jellyfin user id of the caller, or Guid.Empty (API key without user).</summary>
    public static Guid UserId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst("Jellyfin-UserId")?.Value
            ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }

    public static bool IsAdmin(ClaimsPrincipal? user) =>
        user != null && (user.IsInRole("Administrator") || user.IsInRole("administrator"));

    /// <summary>
    /// The user id a request may act on. A signed-in user always acts on their own id; only an
    /// administrator (or an admin API key) may name another user. Returns null when forbidden.
    /// The returned string keeps the caller's spelling of the id (store keys predate this check).
    /// </summary>
    public static string? EffectiveUser(ClaimsPrincipal? user, string? requested)
    {
        var caller = UserId(user);
        var hasRequested = Guid.TryParse(requested, out var requestedId);
        if (caller != Guid.Empty)
        {
            if (!hasRequested || requestedId == caller) return hasRequested ? requested : caller.ToString("N");
            return IsAdmin(user) ? requested : null;
        }
        return IsAdmin(user) && hasRequested ? requested : null;
    }

    /// <summary>Per-user key for history/recents ("" for callers without a user).</summary>
    public static string HistoryKey(ClaimsPrincipal? user)
    {
        var id = UserId(user);
        return id == Guid.Empty ? "" : id.ToString("N");
    }
}
