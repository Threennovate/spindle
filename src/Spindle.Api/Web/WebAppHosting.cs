using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Spindle.Api.Web;

/// <summary>
/// Serves the React app from wwwroot, where <c>dotnet publish</c> puts the Vite build. In development the Vite
/// dev server serves the app instead and proxies <c>/api</c> to this server.
/// </summary>
internal static class WebAppHosting
{
    // Radix UI positions popovers with inline styles, hence 'unsafe-inline' for styles only. Paying opens Paddle's
    // checkout: its script and styles from its CDN, the checkout itself in a frame, and Retain's script it loads.
    private const string Paddle = "https://cdn.paddle.com https://sandbox-cdn.paddle.com";

    private const string ContentSecurityPolicy =
        $"default-src 'self'; script-src 'self' {Paddle} https://public.profitwell.com; style-src 'self' 'unsafe-inline' {Paddle}; "
        + $"img-src 'self' data: {Paddle}; font-src 'self' data:; "
        + "connect-src 'self' https://checkout-service.paddle.com https://sandbox-checkout-service.paddle.com; "
        + "frame-src https://buy.paddle.com https://sandbox-buy.paddle.com; "
        + "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        // The app is behind a sign-in, so search engines would only find its sign-in page, competing with the website
        // and showing every self-hosted server. Crawlers may read it (no robots.txt), so they see this and leave it out.
        headers["X-Robots-Tag"] = "noindex, nofollow";
        await next(context);
    });

    public static IApplicationBuilder UseWebAppStaticFiles(this IApplicationBuilder app) =>
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                // Vite puts a content hash in every file name under /assets, so these never change.
                if (context.Context.Request.Path.StartsWithSegments("/assets"))
                {
                    context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
            },
        });

    /// <summary>
    /// Serves the downloads folder (<c>App:DownloadsPath</c>) at <c>/downloads</c>: the Mac app's disk image and the
    /// Sparkle feed that tells the installed app about new versions, the Windows installers with their Velopack feed and
    /// packages, and the iPhone app with its install page.
    /// </summary>
    public static IApplicationBuilder UseDownloads(this IApplicationBuilder app, string? path, string contentRoot, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return app;
        }

        // A relative path starts at the site's folder, e.g. ..\downloads beside it: under IIS the working directory is
        // IIS's own.
        var folder = Path.GetFullPath(path, contentRoot);
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("The downloads folder {Folder} doesn't exist; nothing is served at /downloads.", folder);
            return app;
        }

        logger.LogInformation("Serving the downloads folder {Folder} at /downloads", folder);

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".dmg"] = "application/x-apple-diskimage";
        types.Mappings[".delta"] = "application/octet-stream";
        // The Windows app: its installer, and the update packages its feed (releases.win-*.json) names.
        types.Mappings[".exe"] = "application/vnd.microsoft.portable-executable";
        types.Mappings[".nupkg"] = "application/octet-stream";
        // The iPhone app, installed from a link: its package and the manifest that describes it.
        types.Mappings[".ipa"] = "application/octet-stream";
        types.Mappings[".plist"] = "application/xml";
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(folder),
            RequestPath = "/downloads",
            ContentTypeProvider = types,
            // The feed and the latest version's disk image change with every release; asking again costs a 304.
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
        });
    }

    /// <summary>
    /// Short links for the website: /download/mac is the latest Mac app, /download/windows the Windows installer (x64,
    /// which Windows on Arm also runs) and /download/windows-arm64 the native Arm one. Temporary redirects, so the files
    /// behind them can be renamed without changing the links.
    /// </summary>
    public static IEndpointRouteBuilder MapDownloadLinks(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/download/mac", () => TypedResults.Redirect("/downloads/Spindle.dmg")).ExcludeFromDescription();
        routes.MapGet("/download/windows", () => TypedResults.Redirect("/downloads/Spindle-Setup.exe")).ExcludeFromDescription();
        routes.MapGet("/download/windows-arm64", () => TypedResults.Redirect("/downloads/Spindle-Setup-arm64.exe")).ExcludeFromDescription();
        return routes;
    }

    public static IEndpointRouteBuilder MapWebAppFallback(this IEndpointRouteBuilder routes)
    {
        // Unknown API routes and downloads stay real 404s instead of returning the app's HTML. A download that looks
        // like a file matches no endpoint, so the downloads folder serves it or the request ends in a 404; routing
        // runs first, and an endpoint here would keep the file from being served.
        routes.MapFallback("/api/{**path}", () => TypedResults.NotFound()).ExcludeFromDescription();
        routes.MapFallback("/downloads/{**path:nonfile}", () => TypedResults.NotFound()).ExcludeFromDescription();

        // Any other path is a client-side route. index.html is revalidated so a new deploy shows up at once.
        routes.MapFallbackToFile("index.html", new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
        }).ExcludeFromDescription();

        return routes;
    }
}
