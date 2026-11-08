using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.NflixButler.Services;

/**
 * Jellyfin 12.4 only accepts the full "Authorization: MediaBrowser … Token=…" header: the legacy
 * "X-Emby-Token" header and the "api_key" query parameter are answered 401. External services
 * (Seerr/Horus, older clients) still send them, so their stored API key stops working and they
 * can no longer resolve Jellyfin users. This middleware rewrites the legacy forms into the
 * MediaBrowser header before the auth middleware runs; requests that already carry an
 * Authorization header are left untouched.
 */
public sealed class LegacyAuthHeaderStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var headers = context.Request.Headers;
            if (!headers.ContainsKey("Authorization"))
            {
                string? token = null;
                if (headers.TryGetValue("X-Emby-Token", out var emby) && !string.IsNullOrWhiteSpace(emby.ToString()))
                    token = emby.ToString();
                else if (headers.TryGetValue("X-MediaBrowser-Token", out var mb) && !string.IsNullOrWhiteSpace(mb.ToString()))
                    token = mb.ToString();
                else if (context.Request.Query.TryGetValue("api_key", out var query) && !string.IsNullOrWhiteSpace(query.ToString()))
                    token = query.ToString();

                if (token != null)
                    headers["Authorization"] = "MediaBrowser Token=\"" + token + "\"";
            }

            await nextMiddleware();
        });
        next(app);
    };
}
