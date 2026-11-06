using System;
using Jellyfin.Plugin.NflixButler.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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
    }
}
