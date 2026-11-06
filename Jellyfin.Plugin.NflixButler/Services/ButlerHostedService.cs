using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/// <summary>
/// Runs the butler forever: one pass, a pause, another pass. The pause is the configured polling
/// interval, so a file dropped into a folder is picked up without any manual action and without a
/// library-wide scan.
/// </summary>
public sealed class ButlerHostedService : BackgroundService
{
    private readonly ButlerEngine _engine;
    private readonly StateStore _store;
    private readonly ILogger<ButlerHostedService> _logger;

    public ButlerHostedService(ButlerEngine engine, StateStore store, ILogger<ButlerHostedService> logger)
    {
        _engine = engine;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.State.StartedUtc = DateTime.UtcNow;
        _store.Save();
        _logger.LogInformation("Nflix Butler: started");

        // Let the server finish its own startup before touching the media folders.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var seconds = Math.Clamp(Plugin.Instance?.Configuration.PollSeconds ?? 30, 5, 3600);
            try
            {
                await _engine.RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nflix Butler: unexpected failure");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Nflix Butler: stopped");
    }
}
