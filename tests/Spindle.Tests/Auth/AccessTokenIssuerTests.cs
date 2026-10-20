using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Spindle.Api.Auth;

namespace Spindle.Tests.Auth;

public sealed class AccessTokenIssuerTests
{
    private static readonly AuthOptions TestOptions = new()
    {
        Issuer = "spindle-tests",
        Audience = "spindle-tests",
        SigningKey = new string('k', 32),
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
    };

    [Fact]
    public async Task Issued_token_passes_the_same_validation_as_the_api()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var userId = Guid.NewGuid();

        var token = new AccessTokenIssuer(Options.Create(TestOptions), timeProvider).Issue(userId, Guid.NewGuid());
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, TestOptions.CreateValidationParameters());

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal(userId.ToString(), result.ClaimsIdentity.Name);
    }

    [Fact]
    public void Reported_expiry_matches_the_token()
    {
        var start = new DateTimeOffset(2026, 9, 25, 12, 0, 0, 750, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);

        var token = new AccessTokenIssuer(Options.Create(TestOptions), timeProvider).Issue(Guid.NewGuid(), Guid.NewGuid());
        var jwt = new JsonWebToken(token.Value);

        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 15, 0, TimeSpan.Zero), token.ExpiresAt);
        Assert.Equal(token.ExpiresAt.UtcDateTime, jwt.ValidTo);
    }

    [Fact]
    public void Short_signing_key_fails_options_validation()
    {
        var options = new AuthOptions { Issuer = "a", Audience = "a", SigningKey = "too-short" };

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

        Assert.False(isValid);
    }
}
