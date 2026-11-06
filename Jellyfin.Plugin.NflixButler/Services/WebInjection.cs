using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// Adds the dashboard script tag to the web client on disk. The 10.11 web client is a compiled
/// React bundle that the Android app caches, so patching index.html is the only injection that
/// reaches every client.
/// </summary>
public sealed class WebInjection : IHostedService
{
    private const string Marker = "nflix-butler-nav";
    private readonly ILogger<WebInjection> _logger;

    public WebInjection(ILogger<WebInjection> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Plugin.Instance?.Configuration.InjectWebDashboard == false)
            {
                return Task.CompletedTask;
            }

            var index = Path.Combine(AppContext.BaseDirectory, "jellyfin-web", "index.html");
            if (!File.Exists(index))
            {
                _logger.LogInformation("Nflix Butler: no web client at {Path}, dashboard stays API-only", index);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(index);
            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var tag = $"<script id=\"{Marker}\" src=\"/Plugins/NflixButler/web/nav.js\" defer></script>";
            var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            var patched = bodyEnd >= 0 ? html.Insert(bodyEnd, tag) : html + tag;

            var temp = index + ".nflix-butler.tmp";
            File.WriteAllText(temp, patched);
            File.Move(temp, index, true);
            _logger.LogInformation("Nflix Butler: dashboard link injected into the web client");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only web directory is fine: the dashboard stays reachable by URL.
            _logger.LogWarning(ex, "Nflix Butler: cannot patch the web client");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
