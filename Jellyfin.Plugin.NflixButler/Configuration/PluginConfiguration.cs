using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NflixButler.Configuration;

/// <summary>
/// Settings of the Nflix butler: what it watches, how it files new media, and how hard it tries
/// to repair items Jellyfin could not identify.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Master switch. When false the watcher still runs but does nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between two directory snapshots. Local timers only, never a global scan.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Extra roots to watch. Library roots from Jellyfin are always watched.</summary>
    public string[] ExtraRoots { get; set; } = Array.Empty<string>();

    /// <summary>Library name used for films.</summary>
    public string MovieLibrary { get; set; } = "Films";

    /// <summary>Library name used for series and episodes.</summary>
    public string SeriesLibrary { get; set; } = "Series";

    /// <summary>Library name used for animation.</summary>
    public string AnimeLibrary { get; set; } = "Anime";

    /// <summary>Words in a name or in any parent folder that mean animation.</summary>
    public string[] AnimeKeywords { get; set; } =
    {
        "anime", "animé", "animation", "manga", "shonen", "shounen", "seinen", "isekai",
        "ghibli", "toei", "madhouse", "ufotable", "one piece", "naruto", "boruto", "dragon ball",
        "demon slayer", "kimetsu", "jujutsu", "attack on titan", "shingeki", "solo leveling",
        "my hero academia", "boku no hero", "spy x family", "chainsaw man", "dandadan",
        "frieren", "slime", "re:zero", "sword art online", "bleach", "hunter x hunter",
    };

    /// <summary>Ignore files smaller than this, in megabytes. Kills samples and leftover stubs.</summary>
    public int MinFileSizeMb { get; set; } = 40;

    /// <summary>Let the butler ask Jellyfin's metadata providers to identify unnamed items.</summary>
    public bool FixUnmatched { get; set; } = true;

    /// <summary>How many repairs one pass may attempt, so a cold library never floods the server.</summary>
    public int MaxRepairsPerRun { get; set; } = 8;

    /// <summary>How many new files one pass may hand to Jellyfin, so a bulk copy never floods it.</summary>
    public int MaxRefreshesPerRun { get; set; } = 40;

    /// <summary>Keep this many intake entries in memory and on disk.</summary>
    public int KeepEntries { get; set; } = 500;

    /// <summary>Add a dashboard link to the Jellyfin web client at startup.</summary>
    public bool InjectWebDashboard { get; set; } = true;

    /// <summary>
    /// When true, inject the Nflix web theme (black/red, app-like bottom navigation) into the
    /// Jellyfin web client's index.html. Off by default; enabled on the nflix-clone test app.
    /// </summary>
    public bool NflixWebTheme { get; set; } = false;

    /// <summary>OAuth redirect URI for streaming-account linking (must match the Deezer/Spotify app settings).</summary>
    public string SyncRedirectUri { get; set; } = "https://nflix.miracloud.fr/sync-callback";

    /// <summary>Deezer application id (create one at deezer.com/developers).</summary>
    public string DeezerAppId { get; set; } = "";

    /// <summary>Deezer application secret.</summary>
    public string DeezerAppSecret { get; set; } = "";

    /// <summary>Spotify application client id (create one at developer.spotify.com).</summary>
    public string SpotifyClientId { get; set; } = "";

    /// <summary>Spotify application client secret.</summary>
    public string SpotifyClientSecret { get; set; } = "";

    /// <summary>Folder where YouTube Music downloads land (inside the music library, refreshed after each download).</summary>
    public string YtMusicFolder { get; set; } = "/media/plex/Musiques/YouTube";

    /// <summary>Video containers the butler reacts to.</summary>
    public string[] VideoExtensions { get; set; } =
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".wmv", ".webm", ".mpg", ".mpeg",
    };

    /// <summary>Folders that never contain media worth filing.</summary>
    public string[] SkipFolders { get; set; } =
    {
        "@eaDir", "#recycle", ".recycle", "$RECYCLE.BIN", "System Volume Information",
        "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "trailers",
        "sample", "samples", "subs", "subtitles", "artwork", "metadata",
    };
}
