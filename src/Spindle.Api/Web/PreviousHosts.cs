using Microsoft.Extensions.Options;
using Spindle.Api.Common;

namespace Spindle.Api.Web;

/// <summary>
/// After the app moves to another domain, browsers that open the old one go to the same page on the new one
/// (<c>App:BaseUrl</c>). Apps that haven't updated yet keep talking to the old domain: its API, live sync, downloads
/// and Apple's association file answer as before, since apps don't follow a redirect for a POST or a WebSocket.
/// </summary>
internal static class PreviousHosts
{
    private static readonly string[] StillServed = ["/api", "/downloads", "/health", "/.well-known"];

    public static IApplicationBuilder UsePreviousHostRedirect(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<AppOptions>>().Value;
        if (options.PreviousHosts.Length == 0)
        {
            return app;
        }

        var previous = options.PreviousHosts.Select(host => host.Trim()).Where(host => host.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = options.BaseUrl.TrimEnd('/');
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (previous.Contains(request.Host.Host)
                && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && !StillServed.Any(path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.Redirect(target + request.PathBase + request.Path + request.QueryString, permanent: true);
                return;
            }

            await next(context);
        });
    }
}
