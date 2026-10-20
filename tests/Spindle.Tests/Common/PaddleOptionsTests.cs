using Spindle.Api.Subscriptions;

namespace Spindle.Tests.Common;

public sealed class PaddleOptionsTests
{
    [Theory]
    [InlineData("sandbox", "pdl_sdbx_apikey_abc", "test_abc")]
    [InlineData("production", "pdl_live_apikey_abc", "live_abc")]
    [InlineData("production", null, null)]
    public void Accepts_keys_of_its_environment(string environment, string? apiKey, string? clientToken)
    {
        var paddle = new PaddleOptions { Environment = environment, ApiKey = apiKey, ClientToken = clientToken };

        Assert.Empty(paddle.Validate());
    }

    [Theory]
    [InlineData("sandbox", "pdl_live_apikey_abc", "live_abc")]
    [InlineData("production", "pdl_sdbx_apikey_abc", "test_abc")]
    public void Rejects_keys_of_the_other_environment(string environment, string apiKey, string clientToken)
    {
        var paddle = new PaddleOptions { Environment = environment, ApiKey = apiKey, ClientToken = clientToken };

        Assert.Equal(["ApiKey", "ClientToken"], paddle.Validate().SelectMany(r => r.MemberNames));
    }
}
