using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NflixButler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.NflixButler.Api;

/**
 * YouTube Music search + audio proxy for the Nflix app. Any authenticated Jellyfin user may
 * search; streaming is proxied server-side because Google signs stream URLs per server IP.
 * The app never sees the signed URL, it just plays this route.
 */
[ApiController]
[Authorize]
[Route("Plugins/NflixButler/yt")]
public class YtMusicController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, DateTime> LastSearch = new();
    private static readonly SemaphoreSlim StreamGate = new(4, 4);
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    })
    { Timeout = TimeSpan.FromMinutes(10) };

    private readonly YtMusicService _yt;

    public YtMusicController(YtMusicService yt) => _yt = yt;

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        // One search per 2 s per client: yt-dlp is expensive.
        var now = DateTime.UtcNow;
        if (LastSearch.TryGetValue(ip, out var last) && now - last < TimeSpan.FromSeconds(2))
            return StatusCode(429, new { message = "Trop de recherches, attends 2 s." });
        LastSearch[ip] = now;

        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { results = Array.Empty<object>() });

        try
        {
            var items = await _yt.SearchAsync(q, limit, ct);
            return Ok(new
            {
                results = items.Select(r => new
                {
                    videoId = r.VideoId,
                    title = r.Title,
                    artist = r.Artist,
                    duration = r.Duration,
                    thumbnail = r.Thumbnail,
                }),
            });
        }
        catch (Exception error)
        {
            return StatusCode(502, new { message = error.Message });
        }
    }

    /// <summary>Proxies the audio stream of one video. Range requests are forwarded so the player can seek.</summary>
    [HttpGet("stream")]
    public async Task<IActionResult> Stream([FromQuery] string videoId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoId) || videoId.Length > 32)
            return BadRequest(new { message = "videoId invalide" });

        await StreamGate.WaitAsync(ct);
        try
        {
            var url = await _yt.StreamUrlAsync(videoId, ct);
            var upstream = new HttpRequestMessage(HttpMethod.Get, url);
            upstream.Headers.TryAddWithoutValidation("User-Agent",
                "com.google.android.youtube/20.10.38 (Linux; U; Android 14) gzip");
            if (Request.Headers.TryGetValue("Range", out var range) && System.Net.Http.Headers.RangeHeaderValue.TryParse(range.ToString(), out var parsed))
                upstream.Headers.Range = parsed;
            var response = await Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, new { message = "YouTube a répondu " + (int)response.StatusCode });

            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = "audio/mp4";
            foreach (var header in response.Headers)
            {
                if (header.Key is "Content-Length" or "Content-Range" or "Accept-Ranges")
                    Response.Headers[header.Key] = string.Join(",", header.Value);
            }
            Response.Headers["Accept-Ranges"] = "bytes";
            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            _ = response; // disposed by the caller of the stream
            return new FileStreamResult(upstreamStream, "audio/mp4");
        }
        catch (Exception error)
        {
            return StatusCode(502, new { message = error.Message });
        }
        finally
        {
            StreamGate.Release();
        }
    }
}
