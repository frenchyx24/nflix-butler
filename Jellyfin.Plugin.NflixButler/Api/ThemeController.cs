using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NflixButler.Api;

/// <summary>Serves the Nflix web theme script with a JavaScript content type.</summary>
[ApiController]
[Route("Plugins/NflixButler")]
public class ThemeController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("theme.js")]
    public IActionResult Theme()
    {
        var stream = typeof(ThemeController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.NflixButler.Web.theme.js");
        if (stream == null) return NotFound();
        return File(stream, "application/javascript", "theme.js");
    }
}
