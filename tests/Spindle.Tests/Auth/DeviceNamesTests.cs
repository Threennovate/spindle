using Spindle.Api.Auth;

namespace Spindle.Tests.Auth;

public sealed class DeviceNamesTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36", "Chrome on macOS")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15", "Safari on macOS")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0", "Firefox on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/140.0 Mobile/15E148 Safari/604.1", "Chrome on iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("curl/8.7.1", "A browser")]
    [InlineData("", null)]
    public void Names_browsers_by_what_they_say(string userAgent, string? expected) =>
        Assert.Equal(expected, DeviceNames.FromUserAgent(userAgent));
}
