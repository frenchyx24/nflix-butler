using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.NflixButler.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NflixButler;

/// <summary>
/// Nflix Butler: keeps the library tidy, files what arrives, and repairs items Jellyfin could not
/// identify. It never triggers a library-wide scan, only targeted refreshes of the folders it
/// actually touched.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin GUID. Keep it stable: the server keys settings and pages on it.</summary>
    public static readonly Guid PluginId = new("7f3c1a52-9b64-4d0e-8f21-6c1d2ba94e77");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataFolder = Path.Combine(applicationPaths.PluginConfigurationsPath, "NflixButler");
        Directory.CreateDirectory(DataFolder);
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the folder holding the butler state.</summary>
    public string DataFolder { get; }

    public override string Name => "Nflix Butler";

    public override Guid Id => PluginId;

    public override string Description =>
        "Rangement automatique des nouveaux médias, bibliothèques mieux associées, et réparation ciblée des fiches que Jellyfin ne sait pas identifier.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "nflixbutler",
            EmbeddedResourcePath = "Jellyfin.Plugin.NflixButler.Web.page.html",
        },
    };
}
