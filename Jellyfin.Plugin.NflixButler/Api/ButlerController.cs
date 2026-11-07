using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Configuration;
using Jellyfin.Plugin.NflixButler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NflixButler.Api;

/// <summary>Everything the dashboard and the Nflix app need.</summary>
[ApiController]
[Authorize]
[Route("Plugins/NflixButler")]
public class ButlerController : ControllerBase
{
    private readonly ButlerEngine _engine;
    private readonly StateStore _store;

    public ButlerController(ButlerEngine engine, StateStore store)
    {
        _engine = engine;
        _store = store;
    }

    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>The dashboard page itself, served without going through the web client.</summary>
    [AllowAnonymous]
    [HttpGet("web/dashboard")]
    public IActionResult Dashboard() => Content(Resource("dashboard.html"), "text/html; charset=utf-8");

    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("api/status")]
    public IActionResult Status()
    {
        var config = Configuration;
        var state = _store.State;
        return Ok(new
        {
            enabled = config.Enabled,
            running = _engine.Running,
            lastRunUtc = state.LastRunUtc,
            startedUtc = state.StartedUtc,
            runs = state.Runs,
            filed = state.Filed,
            repaired = state.Repaired,
            pending = state.Entries.Count(entry => entry.Status is "pending" or "needs-review"),
            pollSeconds = config.PollSeconds,
            fixUnmatched = config.FixUnmatched,
            roots = _engine.Roots(),
            libraries = new
            {
                movie = config.MovieLibrary,
                series = config.SeriesLibrary,
                anime = config.AnimeLibrary,
            },
            lastError = state.LastError,
        });
    }

    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("api/entries")]
    public IActionResult Entries([FromQuery] int limit = 100) => Ok(new { entries = _store.Recent(limit) });

    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("api/unmatched")]
    public IActionResult Unmatched([FromQuery] int limit = 50) => Ok(new
    {
        items = _engine.UnmatchedItems(limit).Select(item => new
        {
            itemId = item.Id,
            name = item.Name,
            path = item.Path,
            type = item.GetType().Name,
            year = item.ProductionYear,
        }),
    });

    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("api/run-now")]
    public IActionResult RunNow()
    {
        _ = Task.Run(() => _engine.RunAsync(CancellationToken.None));
        return Ok(new { started = true });
    }

    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("api/fix/{itemId:guid}")]
    public async Task<IActionResult> Fix([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var (fixedItem, name) = await _engine.FixItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        return Ok(new { @fixed = fixedItem, name });
    }

    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("api/refresh/{itemId:guid}")]
    public IActionResult Refresh([FromRoute] Guid itemId)
    {
        _engine.RefreshItem(itemId);
        return Ok(new { refreshed = true });
    }

    [Authorize(Policy = "RequiresElevation")]
    [HttpGet("api/settings")]
    public IActionResult Settings() => Ok(Configuration);

    [Authorize(Policy = "RequiresElevation")]
    [HttpPost("api/settings")]
    public IActionResult SaveSettings([FromBody] PluginConfiguration configuration)
    {
        if (configuration is null)
        {
            return BadRequest();
        }

        Plugin.Instance?.UpdateConfiguration(configuration);
        return Ok(new { saved = true });
    }

    private static string Resource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            return "Fichier introuvable.";
        }

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return "Fichier illisible.";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
