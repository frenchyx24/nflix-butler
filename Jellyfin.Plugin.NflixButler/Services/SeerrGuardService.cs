using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NflixButler.Services;

/**
 * Keeps Seerr/Horus user permissions parseable by the JellyfinEnhanced web plugin.
 *
 * That plugin deserialises /api/v1/user with an int-based permission enum: any account whose
 * permission bitmask exceeds int.MaxValue (2147483647) makes the whole user list fail to parse,
 * so the web UI reports "no Seerr account found" for everyone. Seerr v3 grants such high bits
 * (e.g. 40079483008) both to existing users and as defaultPermissions for new ones.
 *
 * Every few minutes this guard lists Seerr users and clamps any over-int32 value (the classic
 * permission bits all live below 2^31, only the new high bits are dropped), and does the same
 * for defaultPermissions in the main settings. It reads the Seerr URL and API key from the
 * JellyfinEnhanced plugin configuration; if that plugin is absent or unconfigured it does
 * nothing.
 */
public sealed class SeerrGuardService : BackgroundService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const long Int32Max = int.MaxValue;

    private readonly ILogger<SeerrGuardService> _logger;
    private readonly MediaBrowser.Common.Configuration.IApplicationPaths _paths;

    public SeerrGuardService(ILogger<SeerrGuardService> logger, MediaBrowser.Common.Configuration.IApplicationPaths paths)
    {
        _logger = logger;
        _paths = paths;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First pass after startup, then every 10 minutes.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await GuardAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogDebug(ex, "Seerr guard: pass failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task GuardAsync(CancellationToken ct)
    {
        var (url, apiKey) = ReadEnhancedSeerrConfig();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey)) return;
        url = url!.TrimEnd('/');

        var changed = 0;

        // Users: clamp any permission bitmask above int.MaxValue.
        using (var listDoc = await GetJsonAsync(url + "/api/v1/user?take=100&skip=0", apiKey!, ct).ConfigureAwait(false))
        {
            if (listDoc == null) return;
            if (!listDoc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
            foreach (var user in results.EnumerateArray())
            {
                if (!user.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                if (!user.TryGetProperty("permissions", out var permEl) || permEl.ValueKind != JsonValueKind.Number) continue;
                if (!permEl.TryGetInt64(out var perms) || perms <= Int32Max) continue;
                var id = idEl.GetInt32();
                var masked = perms & Int32Max;
                var body = JsonSerializer.Serialize(new { permissions = masked });
                using var req = new HttpRequestMessage(HttpMethod.Put, url + "/api/v1/user/" + id)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Api-Key", apiKey);
                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) { changed++; _logger.LogInformation("Seerr guard: clamped permissions of user {Id} from {Old} to {New}", id, perms, masked); }
            }
        }

        // Settings: same clamp for defaultPermissions so new accounts never get high bits.
        using (var settingsDoc = await GetJsonAsync(url + "/api/v1/settings/main", apiKey!, ct).ConfigureAwait(false))
        {
            if (settingsDoc == null) return;
            if (!settingsDoc.RootElement.TryGetProperty("defaultPermissions", out var defEl)
                || defEl.ValueKind != JsonValueKind.Number
                || !defEl.TryGetInt64(out var defPerms)
                || defPerms <= Int32Max) return;

            // The settings body is read-only on "apiKey": strip it before posting back.
            using var ms = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in settingsDoc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("apiKey")) continue;
                    if (prop.NameEquals("defaultPermissions")) writer.WriteNumber("defaultPermissions", defPerms & Int32Max);
                    else prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            ms.Position = 0;
            using var req = new HttpRequestMessage(HttpMethod.Post, url + "/api/v1/settings/main")
            { Content = new StreamContent(ms) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) { changed++; _logger.LogInformation("Seerr guard: clamped defaultPermissions from {Old} to {New}", defPerms, defPerms & Int32Max); }
        }

        if (changed > 0) _logger.LogInformation("Seerr guard: {Count} value(s) clamped", changed);
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    /// <summary>Seerr URL + API key from the JellyfinEnhanced plugin configuration (its own XML).</summary>
    private (string? Url, string? ApiKey) ReadEnhancedSeerrConfig()
    {
        try
        {
            var path = System.IO.Path.Combine(_paths.PluginConfigurationsPath, "Jellyfin.Plugin.JellyfinEnhanced.xml");
            if (!System.IO.File.Exists(path)) return (null, null);
            var doc = System.Xml.Linq.XDocument.Load(path);
            var urls = doc.Root?.Element("JellyseerrUrls")?.Value;
            var apiKey = doc.Root?.Element("JellyseerrApiKey")?.Value;
            var url = urls?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return (string.IsNullOrWhiteSpace(url) ? null : url, string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
        }
        catch
        {
            return (null, null);
        }
    }
}
