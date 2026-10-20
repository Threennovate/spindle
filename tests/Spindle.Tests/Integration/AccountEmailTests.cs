using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Spindle.Api.Auth;

namespace Spindle.Tests.Integration;

/// <summary>Confirming an email address, and resetting a forgotten password, through links sent by email.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class AccountEmailTests(PostgresApiFactory factory)
{
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_accounts_confirm_their_address_from_an_email()
    {
        var user = await TestUser.RegisterAsync(factory);
        var email = factory.Emails.LastTo(user.Email);
        var link = ConfirmLink().Match(email.TextBody);

        var mangled = await Anonymous().PostAsJsonAsync("/api/auth/confirm-email", new { userId = user.UserId, token = "not-a-token" }, Ct);
        var confirmed = await Anonymous().PostAsJsonAsync("/api/auth/confirm-email", new { userId = Guid.Parse(link.Groups["user"].Value), token = link.Groups["token"].Value }, Ct);
        var again = await Anonymous().PostAsJsonAsync("/api/auth/confirm-email", new { userId = user.UserId, token = link.Groups["token"].Value }, Ct);

        Assert.Equal("Confirm your email for Spindle", email.Subject);
        Assert.StartsWith("https://track.example.com/confirm-email?", link.Value);
        Assert.Equal(["token"], await TestUser.ErrorFieldsAsync(mangled));
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.True((await user.MeAsync()).EmailConfirmed);
    }

    [Fact]
    public async Task People_who_joined_by_invitation_have_confirmed_their_address_already()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);

        Assert.True((await member.MeAsync()).EmailConfirmed);
        Assert.Equal("Test User invited you to Test User on Spindle", factory.Emails.LastTo(member.Email).Subject);
    }

    [Fact]
    public async Task A_new_confirmation_email_can_be_asked_for()
    {
        var user = await TestUser.RegisterAsync(factory);
        var first = factory.Emails.LastTo(user.Email);

        var resent = await user.Client.PostAsync("/api/me/confirmation-email", null, Ct);
        var link = ConfirmLink().Match(factory.Emails.LastTo(user.Email).TextBody);
        (await Anonymous().PostAsJsonAsync("/api/auth/confirm-email", new { userId = user.UserId, token = link.Groups["token"].Value }, Ct)).EnsureSuccessStatusCode();
        var afterConfirming = await user.Client.PostAsync("/api/me/confirmation-email", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resent.StatusCode);
        Assert.NotSame(first, factory.Emails.LastTo(user.Email));
        Assert.Equal(HttpStatusCode.NoContent, afterConfirming.StatusCode);
    }

    [Fact]
    public async Task Asking_for_a_confirmation_email_says_when_it_could_not_be_sent()
    {
        var user = await TestUser.RegisterAsync(factory);
        factory.Emails.Fail = true;
        try
        {
            var resent = await user.Client.PostAsync("/api/me/confirmation-email", null, Ct);
            var problem = await resent.Content.ReadFromJsonAsync<ProblemDetails>(Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, resent.StatusCode);
            Assert.Equal("The email couldn't be sent. Try again in a few minutes.", problem?.Title);
        }
        finally
        {
            factory.Emails.Fail = false;
        }
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_from_an_email()
    {
        var user = await TestUser.RegisterAsync(factory);
        var login = await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }, Ct);
        var session = await TestUser.ReadAsync<AuthTokensResponse>(login);

        var unknown = await Anonymous().PostAsJsonAsync("/api/auth/forgot-password", new { email = $"nobody-{Guid.NewGuid():N}@example.com" }, Ct);
        var asked = await Anonymous().PostAsJsonAsync("/api/auth/forgot-password", new { email = user.Email.ToUpperInvariant() }, Ct);
        var email = factory.Emails.LastTo(user.Email);
        var token = ResetLink().Match(email.TextBody).Groups["token"].Value;

        var weak = await Anonymous().PostAsJsonAsync("/api/auth/reset-password", new { email = user.Email, token, newPassword = "short" }, Ct);
        var reset = await Anonymous().PostAsJsonAsync("/api/auth/reset-password", new { email = user.Email, token, newPassword = "a fresh start here" }, Ct);
        var reused = await Anonymous().PostAsJsonAsync("/api/auth/reset-password", new { email = user.Email, token, newPassword = "yet another one" }, Ct);

        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("Reset your Spindle password", email.Subject);
        Assert.Contains($"https://track.example.com/reset-password?email={Uri.EscapeDataString(user.Email)}&token=", email.TextBody);
        Assert.Equal(["newPassword"], await TestUser.ErrorFieldsAsync(weak));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(["token"], await TestUser.ErrorFieldsAsync(reused));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = session.RefreshToken }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "a fresh start here" }, Ct)).StatusCode);
        Assert.True((await user.MeAsync()).EmailConfirmed);
    }

    [Fact]
    public async Task Resetting_the_password_opens_a_locked_account()
    {
        var user = await TestUser.RegisterAsync(factory);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong password" }, Ct);
        }

        var locked = await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }, Ct);
        await Anonymous().PostAsJsonAsync("/api/auth/forgot-password", new { email = user.Email }, Ct);
        var token = ResetLink().Match(factory.Emails.LastTo(user.Email).TextBody).Groups["token"].Value;
        (await Anonymous().PostAsJsonAsync("/api/auth/reset-password", new { email = user.Email, token, newPassword = "a fresh start here" }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "a fresh start here" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Account_emails_are_limited_per_network_address()
    {
        await using var strict = factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:AccountEmailsPerQuarterHour", "2"));
        var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "someone@example.com" }, Ct)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
    }

    private HttpClient Anonymous() => factory.CreateClient();

    [GeneratedRegex(@"https://track\.example\.com/confirm-email\?user=(?<user>[0-9a-f-]+)&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex ConfirmLink();

    [GeneratedRegex(@"/reset-password\?email=[^&\s]+&token=(?<token>[A-Za-z0-9_-]+)")]
    private static partial Regex ResetLink();
}
