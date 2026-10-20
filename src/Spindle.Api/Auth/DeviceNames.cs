namespace Spindle.Api.Auth;

/// <summary>A readable name for a browser session, such as "Chrome on macOS", for the list of signed-in devices.</summary>
internal static class DeviceNames
{
    public static string? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        // Order matters: Edge and Opera also say Chrome, Chrome also says Safari, and iOS browsers say Safari.
        var browser = userAgent switch
        {
            _ when Has(userAgent, "Edg/") => "Edge",
            _ when Has(userAgent, "OPR/") => "Opera",
            _ when Has(userAgent, "Firefox/") || Has(userAgent, "FxiOS/") => "Firefox",
            _ when Has(userAgent, "Chrome/") || Has(userAgent, "CriOS/") => "Chrome",
            _ when Has(userAgent, "Safari/") => "Safari",
            _ => "A browser",
        };

        var system = userAgent switch
        {
            _ when Has(userAgent, "iPhone") => "iPhone",
            _ when Has(userAgent, "iPad") => "iPad",
            _ when Has(userAgent, "Android") => "Android",
            _ when Has(userAgent, "Mac OS X") || Has(userAgent, "Macintosh") => "macOS",
            _ when Has(userAgent, "Windows") => "Windows",
            _ when Has(userAgent, "CrOS") => "ChromeOS",
            _ when Has(userAgent, "Linux") => "Linux",
            _ => null,
        };

        return system is null ? browser : $"{browser} on {system}";
    }

    private static bool Has(string userAgent, string part) => userAgent.Contains(part, StringComparison.Ordinal);
}
