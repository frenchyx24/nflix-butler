using System;
using Jellyfin.Plugin.NflixButler.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NflixButler;

/// <summary>Registers the butler engine, its state store and the background service.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            return new StateStore(System.IO.Path.Combine(paths.PluginConfigurationsPath, "NflixButler"));
        });
        serviceCollection.AddSingleton<ButlerEngine>();
        serviceCollection.AddHostedService<ButlerHostedService>();
        serviceCollection.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            return new YtStore(System.IO.Path.Combine(paths.PluginConfigurationsPath, "NflixButler"));
        });
        serviceCollection.AddSingleton<YtMusicService>();
        serviceCollection.AddSingleton(provider =>
        {
            var paths = provider.GetRequiredService<IApplicationPaths>();
            return new SyncStore(System.IO.Path.Combine(paths.PluginConfigurationsPath, "NflixButler"));
        });
        // Re-accept the legacy X-Emby-Token header / api_key query param as a MediaBrowser token.
        serviceCollection.AddSingleton<IStartupFilter, LegacyAuthHeaderStartupFilter>();
        // Keep Seerr permission bitmasks inside int32 so the web Seerr plugin can parse its user list.
        serviceCollection.AddHostedService<SeerrGuardService>();
    }
}
