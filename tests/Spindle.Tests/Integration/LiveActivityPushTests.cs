using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Spindle.Api.Auth;
using Spindle.Api.Projects;
using Spindle.Api.Push;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>A timer changed or stopped elsewhere shows in the iPhone's Live Activity, through APNs.</summary>
[Collection(PostgresCollection.Name)]
public sealed class LiveActivityPushTests(PostgresApiFactory factory)
{
    private const string ActivityToken = "bb22bb22";

    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_phones_activity_changes_with_the_entry_and_ends_when_the_timer_stops_elsewhere()
    {
        var apns = new FakeApns();
        var server = Hosted(apns);
        var mac = await TestUser.RegisterAsync(server);
        var phone = await SignInAgainAsync(server, mac);
        var project = await mac.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9" });
        var start = new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero);
        var entry = await mac.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Landing page", projectId = project.Id, start });
        // Opened, the app shows the timer and sends its activity's token.
        Assert.True(await RegisterAsync(phone, new { sandbox = true, liveActivityToken = ActivityToken, liveActivityEntryId = entry.Id }));

        (await mac.PutAsync($"/time-entries/{entry.Id}", new { description = "Landing page copy", projectId = project.Id, start })).EnsureSuccessStatusCode();
        var update = await apns.NextAsync(ActivityToken);
        (await mac.PostAsync($"/time-entries/{entry.Id}/stop", new { })).EnsureSuccessStatusCode();
        var end = await apns.NextAsync(ActivityToken);

        Assert.Equal("https://api.sandbox.push.apple.com/3/device/bb22bb22", update.Url);
        Assert.Equal(HttpVersion.Version20, update.Version);
        Assert.Equal("liveactivity", update.Headers["apns-push-type"]);
        Assert.Equal("com.spindletrack.iphone.push-type.liveactivity", update.Headers["apns-topic"]);
        await AssertSignedAsync(update.Headers["authorization"]);
        var aps = update.Body["aps"]!;
        Assert.Equal("update", aps["event"]!.GetValue<string>());
        Assert.Null(aps["alert"]);
        Assert.Equal(entry.Id.ToString(), aps["content-state"]!["entryId"]!.GetValue<string>());
        Assert.Equal("Landing page copy", aps["content-state"]!["description"]!.GetValue<string>());
        Assert.Equal("Website", aps["content-state"]!["projectName"]!.GetValue<string>());
        Assert.Equal("#0B83D9", aps["content-state"]!["projectColor"]!.GetValue<string>());
        // Seconds since 2001, as Swift decodes a date.
        Assert.Equal(813_403_800, aps["content-state"]!["start"]!.GetValue<double>());
        Assert.Equal("end", end.Body["aps"]!["event"]!.GetValue<string>());
        Assert.NotNull(end.Body["aps"]!["dismissal-date"]);
    }

    [Fact]
    public async Task A_timer_started_elsewhere_starts_no_activity()
    {
        var apns = new FakeApns();
        var server = Hosted(apns);
        var mac = await TestUser.RegisterAsync(server);
        var phone = await SignInAgainAsync(server, mac);
        // As the first TestFlight build sends it; the token isn't kept.
        await RegisterAsync(phone, new { sandbox = false, liveActivityStartToken = "aa11aa11" });

        await mac.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "From the Mac", start = DateTimeOffset.UtcNow });
        await Task.Delay(LiveActivityPushService.Settle * 4, Ct);

        Assert.Empty(apns.Sent);
    }

    [Fact]
    public async Task The_phone_that_stopped_the_timer_is_not_sent_it()
    {
        var apns = new FakeApns();
        var server = Hosted(apns);
        var mac = await TestUser.RegisterAsync(server);
        var phone = await SignInAgainAsync(server, mac);
        var entry = await phone.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "From the phone", start = DateTimeOffset.UtcNow });
        await RegisterAsync(phone, new { sandbox = true, liveActivityToken = ActivityToken, liveActivityEntryId = entry.Id });

        (await phone.PostAsync($"/time-entries/{entry.Id}/stop", new { })).EnsureSuccessStatusCode();
        await Task.Delay(LiveActivityPushService.Settle * 4, Ct);

        Assert.Empty(apns.Sent);
    }

    [Fact]
    public async Task A_signed_out_phone_and_a_token_apns_no_longer_knows_hear_nothing_more()
    {
        var apns = new FakeApns { Gone = { ActivityToken } };
        var server = Hosted(apns);
        var mac = await TestUser.RegisterAsync(server);
        var phone = await SignInAgainAsync(server, mac);
        var tablet = await SignInAsync(server, mac);
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        var entry = await mac.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "First", start });
        await RegisterAsync(phone, new { sandbox = true, liveActivityToken = ActivityToken, liveActivityEntryId = entry.Id });
        await RegisterAsync(tablet.User, new { sandbox = true, liveActivityToken = "cc33cc33", liveActivityEntryId = entry.Id });
        (await server.CreateClient().PostAsJsonAsync("/api/auth/logout", new { refreshToken = tablet.Tokens.RefreshToken }, Ct)).EnsureSuccessStatusCode();

        (await mac.PutAsync($"/time-entries/{entry.Id}", new { description = "Renamed", start })).EnsureSuccessStatusCode();
        await apns.NextAsync(ActivityToken);
        (await mac.PostAsync($"/time-entries/{entry.Id}/stop", new { })).EnsureSuccessStatusCode();
        await Task.Delay(LiveActivityPushService.Settle * 4, Ct);

        Assert.Single(apns.Sent);
    }

    [Fact]
    public async Task A_server_without_apns_settings_keeps_no_devices()
    {
        var user = await TestUser.RegisterAsync(factory);

        Assert.False(await RegisterAsync(user, new { sandbox = true, liveActivityToken = ActivityToken, liveActivityEntryId = Guid.NewGuid() }));
    }

    private WebApplicationFactory<Program> Hosted(FakeApns apns) => factory.WithWebHostBuilder(builder => builder
        .UseSetting("Push:Apns:KeyId", "KEY1234567")
        .UseSetting("Push:Apns:TeamId", "TEAM123456")
        // As an environment variable would carry it, on one line.
        .UseSetting("Push:Apns:PrivateKey", Key.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n", StringComparison.Ordinal))
        .ConfigureTestServices(services => services.AddHttpClient<ApnsClient>().ConfigurePrimaryHttpMessageHandler(() => apns)));

    private static async Task<bool> RegisterAsync(TestUser user, object device)
    {
        var response = await user.Client.PutAsJsonAsync("/api/me/push-device", device, Ct);
        return await TestUser.ReadAsync<bool>(response);
    }

    private static async Task<TestUser> SignInAgainAsync(WebApplicationFactory<Program> server, TestUser user) =>
        (await SignInAsync(server, user)).User;

    /// <summary>The same person on another device: a session of their own.</summary>
    private static async Task<(TestUser User, AuthTokensResponse Tokens)> SignInAsync(WebApplicationFactory<Program> server, TestUser user)
    {
        var client = server.CreateClient();
        var tokens = await TestUser.ReadAsync<AuthTokensResponse>(
            await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "correct horse battery" }, Ct));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (new TestUser(client, user.UserId, user.WorkspaceId, user.Email), tokens);
    }

    /// <summary>The provider token is signed with the APNs key, names it, and is issued by the team.</summary>
    private static async Task AssertSignedAsync(string authorization)
    {
        Assert.StartsWith("bearer ", authorization);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(authorization["bearer ".Length..], new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(ECDsa.Create(Key.ExportParameters(false))),
            ValidIssuer = "TEAM123456",
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("KEY1234567", ((JsonWebToken)result.SecurityToken).Kid);
    }

    private sealed record Push(string Token, string Url, Version Version, IReadOnlyDictionary<string, string> Headers, JsonObject Body);

    /// <summary>APNs, which answers 410 for the tokens in <see cref="Gone"/>.</summary>
    private sealed class FakeApns : HttpMessageHandler
    {
        public HashSet<string> Gone { get; } = [];

        public ConcurrentQueue<Push> Sent { get; } = [];

        private readonly Dictionary<string, int> seenBefore = [];

        /// <summary>The next push to the token, after those already taken.</summary>
        public async Task<Push> NextAsync(string token)
        {
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(10); waited += TimeSpan.FromMilliseconds(100))
            {
                if (Sent.Where(p => p.Token == token).Skip(seenBefore.GetValueOrDefault(token)).FirstOrDefault() is { } push)
                {
                    seenBefore[token] = seenBefore.GetValueOrDefault(token) + 1;
                    return push;
                }

                await Task.Delay(100, Ct);
            }

            throw new TimeoutException($"No further push reached {token}.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.RequestUri!.Segments[^1];
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Sent.Enqueue(new Push(token, request.RequestUri.ToString(), request.Version, headers, body));
            return Gone.Contains(token)
                ? new HttpResponseMessage(HttpStatusCode.Gone) { Content = JsonContent.Create(new { reason = "Unregistered" }) }
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
