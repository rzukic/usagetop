using System.Reflection;

namespace AiUsage.Server;

/// <summary>
/// The server's own pages: a handful of files compiled into the binary, so a
/// single-file publish still has a UI. The pages are public and hold nothing;
/// everything they show comes from /api, which wants the token.
/// </summary>
public static class WebUi
{
    /// <summary>
    /// Nothing from anywhere else, and no inline script: a provider's message
    /// that somehow reached the page as markup would still have nothing to run.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; "
        + "img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly (string Route, string Resource, string ContentType)[] Files =
    [
        ("/", "web/index.html", "text/html; charset=utf-8"),
        ("/plans", "web/plans.html", "text/html; charset=utf-8"),
        ("/app.css", "web/app.css", "text/css; charset=utf-8"),
        ("/app.js", "web/app.js", "text/javascript; charset=utf-8"),
        ("/marks.js", "web/marks.js", "text/javascript; charset=utf-8"),
        ("/plans.js", "web/plans.js", "text/javascript; charset=utf-8"),
        ("/favicon.svg", "web/favicon.svg", "image/svg+xml"),
    ];

    public static void Map(WebApplication app)
    {
        foreach (var (route, resource, contentType) in Files)
        {
            var bytes = Read(resource);
            app.MapGet(route, (HttpContext context) =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";

                // Small, and it must match the binary that serves the API.
                headers.CacheControl = "no-cache";
                return Results.Bytes(bytes, contentType);
            });
        }
    }

    internal static byte[] Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The page file '{resource}' was not built in.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
