using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NflixButler.Api;

/// <summary>Admin view of the watched marking: recent decisions and a manual backfill.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/NflixButler/Watched")]
public class WatchedController : ControllerBase
{
    private readonly WatchedTracker _tracker;

    public WatchedController(WatchedTracker tracker)
    {
        _tracker = tracker;
    }

    /// <summary>Settings in effect, tracked entries and the last 200 decisions.</summary>
    [HttpGet("Status")]
    public IActionResult Status()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new
        {
            enabled = config?.WatchedEnabled ?? true,
            thresholds = WatchedTracker.Thresholds(),
            tracked = _tracker.TrackedCount,
            lastSweepUtc = _tracker.LastSweepUtc,
            recent = _tracker.Recent,
        });
    }

    /// <summary>Marks resume positions already in the credits zone. dryRun=true (default) only reports.</summary>
    [HttpPost("Backfill")]
    public async Task<IActionResult> Backfill([FromQuery] bool dryRun = true)
    {
        var results = await _tracker.BackfillAsync(dryRun).ConfigureAwait(false);
        return Ok(new { dryRun, count = results.Count, items = results });
    }
}
