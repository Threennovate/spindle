namespace Spindle.Api.Web;

internal static class AppleAssociation
{
    public static IEndpointRouteBuilder MapAppleAssociation(this IEndpointRouteBuilder routes)
    {
        // Team ID and bundle IDs must match the signed apps' associated-domains entitlements.
        routes.MapGet("/.well-known/apple-app-site-association", () => Results.Json(new
        {
            webcredentials = new
            {
                apps = new[] { "9ARDA688G5.com.spindletrack.mac", "9ARDA688G5.com.spindletrack.iphone" },
            },
        })).ExcludeFromDescription();
        return routes;
    }
}
