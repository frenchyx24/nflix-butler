using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// Caps shared by the controllers and the pending music queue: long input fields are clipped
/// before they reach a store or an external service, and queues stay bounded.
/// </summary>
public static class InputLimits
{
    /// <summary>Longest search query / artist name forwarded to a catalogue or kept in history.</summary>
    public const int Query = 200;

    /// <summary>Longest artist or title sent to Deezer on export.</summary>
    public const int Field = 200;

    /// <summary>Most pending tracks one user may keep across every playlist.</summary>
    public const int PendingPerUser = 5000;

    /// <summary>Most artists kept in the discography queue.</summary>
    public const int ArtistQueue = 500;

    public static string Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max].Trim();
    }

    /// <summary>Clip a search query before it is sent upstream or recorded in a history.</summary>
    public static string ClipQuery(string? value) => Clip(value, Query);

    /// <summary>
    /// Adds [incoming] to the pending list, skipping duplicates and stopping at the per-user cap.
    /// Items already queued keep their place: a repeated import never evicts them.
    /// </summary>
    public static int Admit(IList<PendingTrack> items, IEnumerable<PendingTrack> incoming, int perUser)
    {
        var added = 0;
        foreach (var track in incoming)
        {
            if (items.Count(x => x.UserId == track.UserId) >= perUser) break;
            if (items.Any(x => x.PlaylistId == track.PlaylistId && x.Title == track.Title && x.Artist == track.Artist)) continue;
            items.Add(track);
            added++;
        }
        return added;
    }

    /// <summary>Enqueues [name] once; false when it is already queued or the queue is full.</summary>
    public static bool TryEnqueue(Queue<string> queue, string name, int cap)
    {
        if (queue.Count >= cap) return false;
        if (queue.Any(q => ArtistNames.Key(q) == ArtistNames.Key(name))) return false;
        queue.Enqueue(name);
        return true;
    }
}
