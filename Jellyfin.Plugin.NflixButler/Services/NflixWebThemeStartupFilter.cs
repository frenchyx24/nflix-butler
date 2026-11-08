using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.NflixButler.Services;

/**
 * Injects the Nflix web theme into the Jellyfin web client's index.html when the
 * [PluginConfiguration.NflixWebTheme] flag is on (used on the nflix-clone app to make the web UI
 * look like the Nflix mobile app: black/red theme + app-like bottom navigation).
 * The theme itself is served as a plugin web page at /Plugins/NflixButler/web/theme.js.
 */
public sealed class NflixWebThemeStartupFilter : IStartupFilter
{
    private const string ScriptTag = "<script src=\"/Plugins/NflixButler/web/theme.js\" defer></script>";

    private static bool Enabled() => Plugin.Instance?.Configuration.NflixWebTheme == true;

    private static bool IsWebIndex(string path) =>
        path.Equals("/web", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!Enabled()
                || context.Request.Method != "GET"
                || !IsWebIndex(path))
            {
                await nextMiddleware();
                return;
            }

            // Buffer the HTML so the script tag can be inserted before </head>.
            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await nextMiddleware();
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            var contentType = context.Response.ContentType ?? string.Empty;
            if (context.Response.StatusCode != 200 || !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(originalBody);
                return;
            }

            buffer.Position = 0;
            string html;
            using (var reader = new StreamReader(buffer, Encoding.UTF8, leaveOpen: true))
                html = await reader.ReadToEndAsync();

            if (html.Contains("/Plugins/NflixButler/web/theme.js", StringComparison.Ordinal))
            {
                var unchanged = Encoding.UTF8.GetBytes(html);
                context.Response.ContentLength = unchanged.Length;
                await originalBody.WriteAsync(unchanged);
                return;
            }

            var injected = html.Replace("</head>", ScriptTag + "</head>", StringComparison.OrdinalIgnoreCase);
            if (ReferenceEquals(injected, html) || injected == html)
                injected = html.Replace("</body>", ScriptTag + "</body>", StringComparison.OrdinalIgnoreCase);
            var bytes = Encoding.UTF8.GetBytes(injected);
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes);
        });
        next(app);
    };
}
