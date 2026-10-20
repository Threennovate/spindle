using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Operator;
using Spindle.Api.Subscriptions;
using Spindle.Api.Team;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;
using Spindle.Tests.Api;

namespace Spindle.Tests.Integration;

/// <summary>
/// Paying through Paddle, against a stand-in for Paddle's API: the real provider code, signatures and all, with
/// Paddle's answers made up here.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PaddleTests(PostgresApiFactory factory)
{
    private const string WebhookSecret = "pdl_ntfset_test";
    private const string MonthlyPrice = "pri_monthly";
    private const string YearlyPrice = "pri_yearly";
    private const string StandardMonthlyPrice = "pri_standard_monthly";
    private const string StandardYearlyPrice = "pri_standard_yearly";

    /// <summary>The one address the fake Paddle sends webhooks from, and where every test request comes from unless it says otherwise.</summary>
    private const string PaddleAddress = "34.194.127.46";

    /// <summary>What each price in the fake catalog costs, in cents.</summary>
    private static readonly Dictionary<string, string> Amounts = new()
    {
        [MonthlyPrice] = "500",
        [YearlyPrice] = "5000",
        [StandardMonthlyPrice] = "700",
        [StandardYearlyPrice] = "7000",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_checkout_carries_the_rest_of_the_trial_and_the_seats_and_can_be_paid_by_owners_and_admins_only()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var member = await factory.AddMemberAsync(owner);

        var subscription = await SubscriptionAsync(owner, organizationId);
        var checkout = await owner.Client.PostAsJsonAsync(CheckoutPath(organizationId), new { interval = "month" }, Ct);
        var start = await TestUser.ReadAsync<CheckoutStart>(checkout);

        Assert.True(subscription.CheckoutAvailable);
        Assert.Contains(subscription.Prices, p => p is { Interval: BillingInterval.Month, UnitAmount: 5m, Currency: "EUR" });
        Assert.Equal(("paddle", "sandbox", "test_client_token", "txn_test", "ctm_test"), (start.Provider, start.Environment, start.ClientToken, start.TransactionId, start.CustomerId));
        var item = paddle.Transaction!["items"]![0]!;
        Assert.Equal(2, item["quantity"]!.GetValue<int>());
        Assert.Equal(90, item["price"]!["trial_period"]!["frequency"]!.GetValue<int>());
        Assert.Equal((2, 2), (item["price"]!["quantity"]!["minimum"]!.GetValue<int>(), item["price"]!["quantity"]!["maximum"]!.GetValue<int>()));
        Assert.Equal(organizationId.ToString(), paddle.Transaction["custom_data"]!["organizationId"]!.GetValue<string>());
        Assert.Equal("ctm_test", paddle.Transaction["customer_id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Client.PostAsJsonAsync(CheckoutPath(organizationId), new { interval = "month" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_webhook_without_a_valid_signature_is_refused()
    {
        await using var server = Hosted(new FakePaddle());
        var body = Event("evt_forged", "subscription.created", Subscription("sub_forged", "trialing", Guid.NewGuid(), DateTimeOffset.UtcNow));

        var response = await server.CreateClient().PostAsync(WebhookPath, new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_plan_chosen_in_the_trial_starts_charging_exactly_when_the_trial_ends()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organization = Assert.Single((await owner.MeAsync()).Organizations);
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        // Paddle's trial is in whole days, so its first charge is a little after ours ends; the price is the checkout's.
        paddle.Subscriptions[subscriptionId] = Subscription(subscriptionId, "trialing", organization.Id, DateTimeOffset.UtcNow,
            nextBilledAt: organization.TrialEndsAt!.Value.AddHours(7), priceId: "pri_checkout", seats: 1);

        var delivered = await DeliverAsync(server, Event("evt_created_" + subscriptionId, "subscription.created", paddle.Subscriptions[subscriptionId]));
        var again = await DeliverAsync(server, Event("evt_created_" + subscriptionId, "subscription.created", paddle.Subscriptions[subscriptionId]));
        var subscription = await SubscriptionAsync(owner, organization.Id);

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (delivered.StatusCode, again.StatusCode));
        Assert.Equal((SubscriptionStatus.Trialing, true, BillingInterval.Month), (subscription.Status, subscription.HasPlan, subscription.Interval!.Value));
        // Moved once, to the catalog price and then, separately as Paddle requires, to the exact end of the trial,
        // without charging; the repeat changed nothing.
        var changes = paddle.Changes.Where(c => c.SubscriptionId == subscriptionId).Select(c => c.Body).ToList();
        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal("do_not_bill", change["proration_billing_mode"]!.GetValue<string>()));
        var items = Assert.Single(changes, c => c.ContainsKey("items"));
        var date = Assert.Single(changes, c => c.ContainsKey("next_billed_at"));
        Assert.False(items.ContainsKey("next_billed_at"));
        Assert.Equal(MonthlyPrice, items["items"]![0]!["price_id"]!.GetValue<string>());
        Assert.Equal(
            organization.TrialEndsAt!.Value.ToUnixTimeSeconds(),
            DateTimeOffset.Parse(date["next_billed_at"]!.GetValue<string>(), CultureInfo.InvariantCulture).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Paying_activates_the_plan_and_older_news_arriving_late_changes_nothing()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var periodEnd = now.AddDays(30);

        await DeliverAsync(server, Event($"evt_a_{subscriptionId}", "subscription.activated",
            Subscription(subscriptionId, "active", organizationId, now, periodEndsAt: periodEnd)));
        await DeliverAsync(server, Event($"evt_b_{subscriptionId}", "subscription.updated",
            Subscription(subscriptionId, "trialing", organizationId, now.AddMinutes(-5))));
        var subscription = await SubscriptionAsync(owner, organizationId);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(periodEnd.ToUnixTimeSeconds(), subscription.PaidUntil!.Value.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.PostAsJsonAsync(CheckoutPath(organizationId), new { interval = "year" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Seats_follow_people_joining_and_leaving_within_seconds()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        paddle.Subscriptions[subscriptionId] = Subscription(subscriptionId, "active", organizationId, DateTimeOffset.UtcNow, periodEndsAt: DateTimeOffset.UtcNow.AddDays(30));
        await DeliverAsync(server, Event($"evt_{subscriptionId}", "subscription.activated", paddle.Subscriptions[subscriptionId]));

        var email = $"member-{Guid.NewGuid():N}@example.com";
        await owner.CreateAsync<SentInvitationResponse>("/invitations", new { email });
        var member = await TestUser.RegisterAsync(server, email, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email)));
        var joined = await ChangeAsync(paddle, subscriptionId, seats: 2);
        (await owner.DeleteAsync($"/members/{member.UserId}")).EnsureSuccessStatusCode();
        var left = await ChangeAsync(paddle, subscriptionId, seats: 1);

        // A paid plan settles seats with the next bill instead of charging for each change.
        Assert.All([joined, left], change => Assert.Equal("prorated_next_billing_period", change["proration_billing_mode"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Managing_the_plan_opens_paddles_customer_portal()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.PostAsync(ManagePath(organizationId), null, Ct)).StatusCode);
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        await DeliverAsync(server, Event($"evt_{subscriptionId}", "subscription.activated",
            Subscription(subscriptionId, "active", organizationId, DateTimeOffset.UtcNow, periodEndsAt: DateTimeOffset.UtcNow.AddDays(30))));

        var manage = await TestUser.ReadAsync<ManageSubscriptionResponse>(await owner.Client.PostAsync(ManagePath(organizationId), null, Ct));

        Assert.Equal("https://sandbox-customer-portal.paddle.com/cpl_test", manage.Url.ToString());
    }

    [Fact]
    public async Task Deleting_the_last_account_of_an_organization_ends_its_plan()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        await DeliverAsync(server, Event($"evt_{subscriptionId}", "subscription.activated",
            Subscription(subscriptionId, "active", organizationId, DateTimeOffset.UtcNow, periodEndsAt: DateTimeOffset.UtcNow.AddDays(30))));

        var deleted = await owner.Client.PostAsJsonAsync("/api/me/delete", new { password = "correct horse battery" }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains(subscriptionId, paddle.Canceled);
    }

    [Fact]
    public async Task An_organization_keeps_its_price_cohort_when_new_organizations_get_other_prices()
    {
        var paddle = new FakePaddle();
        await using var before = Hosted(paddle);
        var early = await TestUser.RegisterAsync(before);
        var earlyOrganization = Assert.Single((await early.MeAsync()).Organizations);
        // The prices for new organizations go up; the early one's don't.
        await using var after = Hosted(paddle, currentCohort: "standard");
        var later = await TestUser.RegisterAsync(after);
        var laterOrganizationId = Assert.Single((await later.MeAsync()).Organizations).Id;
        var monthly = $"sub_{Guid.NewGuid():N}";
        var yearly = $"sub_{Guid.NewGuid():N}";
        paddle.Subscriptions[monthly] = Subscription(monthly, "active", earlyOrganization.Id, DateTimeOffset.UtcNow,
            periodEndsAt: DateTimeOffset.UtcNow.AddDays(30), priceId: "pri_checkout");
        paddle.Subscriptions[yearly] = Subscription(yearly, "active", laterOrganizationId, DateTimeOffset.UtcNow,
            periodEndsAt: DateTimeOffset.UtcNow.AddDays(365), priceId: "pri_checkout", interval: "year");

        var earlyPlan = await SubscriptionAsync(As(after, early), earlyOrganization.Id);
        var laterPlan = await SubscriptionAsync(later, laterOrganizationId);
        (await later.Client.PostAsJsonAsync(CheckoutPath(laterOrganizationId), new { interval = "month" }, Ct)).EnsureSuccessStatusCode();
        var laterCheckout = paddle.Transaction!["items"]![0]!["price"]!;
        await DeliverAsync(after, Event($"evt_{monthly}", "subscription.activated", paddle.Subscriptions[monthly]));
        await DeliverAsync(after, Event($"evt_{yearly}", "subscription.activated", paddle.Subscriptions[yearly]));

        Assert.Equal([5m, 50m], earlyPlan.Prices.OrderBy(p => p.Interval).Select(p => p.UnitAmount));
        Assert.Equal([7m, 70m], laterPlan.Prices.OrderBy(p => p.Interval).Select(p => p.UnitAmount));
        Assert.Equal("700", laterCheckout["unit_price"]!["amount"]!.GetValue<string>());
        Assert.Equal(MonthlyPrice, PriceSetFor(paddle, monthly));
        Assert.Equal(StandardYearlyPrice, PriceSetFor(paddle, yearly));
    }

    [Fact]
    public async Task Operators_see_the_price_cohorts_and_one_missing_from_the_settings()
    {
        var paddle = new FakePaddle();
        await using var before = Hosted(paddle);
        await TestUser.RegisterAsync(before);
        var operatorEmail = $"operator-{Guid.NewGuid():N}@example.com";
        await using var after = Hosted(paddle, currentCohort: "standard").WithWebHostBuilder(b => b.UseSetting("Operators:Emails:0", operatorEmail));
        await using var broken = Hosted(paddle, currentCohort: "standard", withFounding: false)
            .WithWebHostBuilder(b => b.UseSetting("Operators:Emails:0", operatorEmail));
        var operatorUser = await TestUser.RegisterAsync(after, operatorEmail);
        await using (var scope = factory.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users
                .Where(u => u.Id == operatorUser.UserId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.EmailConfirmed, true), Ct);
        }

        var cohorts = await operatorUser.Client.GetFromJsonAsync<List<OperatorPriceCohortResponse>>("/api/operator/price-cohorts", TestJson.Options, Ct);
        var missing = await As(broken, operatorUser).Client.GetFromJsonAsync<List<OperatorPriceCohortResponse>>("/api/operator/price-cohorts", TestJson.Options, Ct);

        var founding = Assert.Single(cohorts!, c => c.Name == "founding");
        var standard = Assert.Single(cohorts!, c => c.Name == "standard");
        Assert.Equal((false, true), (founding.Current, founding.HasPrices));
        Assert.Equal([5m, 50m], founding.Prices.OrderBy(p => p.Interval).Select(p => p.UnitAmount));
        Assert.True(founding.Organizations >= 1);
        Assert.Equal((true, true), (standard.Current, standard.HasPrices));
        Assert.Equal([7m, 70m], standard.Prices.OrderBy(p => p.Interval).Select(p => p.UnitAmount));
        Assert.True(standard.Organizations >= 1);
        Assert.Equal((false, 0), (Assert.Single(missing!, c => c.Name == "founding").HasPrices, Assert.Single(missing!, c => c.Name == "founding").Prices.Count));
    }

    [Fact]
    public async Task A_subscription_whose_price_cohort_has_no_prices_is_left_alone()
    {
        var paddle = new FakePaddle();
        await using var before = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(before);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        // A mistake in the settings: the founding prices are gone.
        await using var broken = Hosted(paddle, currentCohort: "standard", withFounding: false);
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        paddle.Subscriptions[subscriptionId] = Subscription(subscriptionId, "active", organizationId, DateTimeOffset.UtcNow,
            periodEndsAt: DateTimeOffset.UtcNow.AddDays(30));

        var delivered = await DeliverAsync(broken, Event($"evt_{subscriptionId}", "subscription.activated", paddle.Subscriptions[subscriptionId]));
        var plan = await SubscriptionAsync(As(broken, owner), organizationId);

        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        Assert.Equal(SubscriptionStatus.Active, plan.Status);
        Assert.Empty(plan.Prices);
        Assert.DoesNotContain(paddle.Changes, c => c.SubscriptionId == subscriptionId);
    }

    [Fact]
    public async Task A_webhook_from_an_address_that_is_not_Paddles_is_refused_even_when_signed()
    {
        var paddle = new FakePaddle();
        await using var server = Hosted(paddle);
        var owner = await TestUser.RegisterAsync(server);
        var organizationId = Assert.Single((await owner.MeAsync()).Organizations).Id;
        var body = Event("evt_elsewhere", "subscription.created", Subscription("sub_elsewhere", "active", organizationId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1)));

        var refused = await DeliverAsync(server, body, from: "203.0.113.7");
        var plan = await SubscriptionAsync(owner, organizationId);
        var delivered = await DeliverAsync(server, body, from: $"::ffff:{PaddleAddress}");

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.False(plan.HasPlan);
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
    }

    /// <summary>
    /// The hosted service, where new organizations join <paramref name="currentCohort"/>: <c>founding</c> at 5 €/50 €
    /// or <c>standard</c> at 7 €/70 €. Without <paramref name="withFounding"/>, the founding prices are missing from
    /// the settings.
    /// </summary>
    private WebApplicationFactory<Program> Hosted(FakePaddle paddle, string currentCohort = "founding", bool withFounding = true) => factory.WithWebHostBuilder(builder => builder
        .UseSetting("Subscriptions:CurrentPriceCohort", currentCohort)
        .UseSetting("Subscriptions:Paddle:ApiKey", "pdl_sdbx_apikey_test")
        .UseSetting("Subscriptions:Paddle:ClientToken", "test_client_token")
        .UseSetting("Subscriptions:Paddle:WebhookSecret", WebhookSecret)
        .UseSetting("Subscriptions:Paddle:PriceCohorts:founding:MonthlyPriceId", withFounding ? MonthlyPrice : null)
        .UseSetting("Subscriptions:Paddle:PriceCohorts:founding:YearlyPriceId", withFounding ? YearlyPrice : null)
        .UseSetting("Subscriptions:Paddle:PriceCohorts:standard:MonthlyPriceId", StandardMonthlyPrice)
        .UseSetting("Subscriptions:Paddle:PriceCohorts:standard:YearlyPriceId", StandardYearlyPrice)
        .ConfigureTestServices(services =>
        {
            services.AddHttpClient<ISubscriptionProvider, PaddleProvider>().ConfigurePrimaryHttpMessageHandler(() => paddle);
            services.AddSingleton<IStartupFilter, FromAddress>();
            // Tests reconcile through the webhooks, not on the hourly timer.
            services.Remove(services.Single(s => s.ImplementationType == typeof(SubscriptionSyncService)));
        }));

    /// <summary>The same person, signed in on another configuration of the same server.</summary>
    private static TestUser As(WebApplicationFactory<Program> server, TestUser user)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = user.Client.DefaultRequestHeaders.Authorization;
        return new TestUser(client, user.UserId, user.WorkspaceId, user.Email);
    }

    /// <summary>The catalog price the subscription was moved to.</summary>
    private static string PriceSetFor(FakePaddle paddle, string subscriptionId) =>
        Assert.Single(paddle.Changes, c => c.SubscriptionId == subscriptionId && c.Body.ContainsKey("items"))
            .Body["items"]![0]!["price_id"]!.GetValue<string>();

    /// <summary>Waits for the change to the subscription's seats, which happens in the background a few seconds after.</summary>
    private static async Task<JsonObject> ChangeAsync(FakePaddle paddle, string subscriptionId, int seats)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(20); waited += TimeSpan.FromMilliseconds(250))
        {
            var change = paddle.Changes.FirstOrDefault(c => c.SubscriptionId == subscriptionId
                && c.Body["items"]?[0]?["quantity"]?.GetValue<int>() == seats).Body;
            if (change is not null)
            {
                // The next comparison starts from what Paddle now has.
                paddle.Subscriptions[subscriptionId]["items"]![0]!["quantity"] = seats;
                return change;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException($"No change to {seats} seats reached Paddle.");
    }

    private static Task<HttpResponseMessage> DeliverAsync(WebApplicationFactory<Program> server, string body, string? from = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (from is not null)
        {
            request.Headers.Add(FromAddress.Header, from);
        }

        request.Headers.Add("Paddle-Signature", PaddleSignatureTests.Sign(body, WebhookSecret, DateTimeOffset.UtcNow));
        return server.CreateClient().SendAsync(request, Ct);
    }

    private static async Task<SubscriptionResponse> SubscriptionAsync(TestUser user, Guid organizationId) =>
        (await user.Client.GetFromJsonAsync<SubscriptionResponse>($"/api/organizations/{organizationId}/subscription", TestJson.Options, Ct))!;

    private const string WebhookPath = "/api/subscriptions/webhooks/paddle";

    private static string CheckoutPath(Guid organizationId) => $"/api/organizations/{organizationId}/subscription/checkout";

    private static string ManagePath(Guid organizationId) => $"/api/organizations/{organizationId}/subscription/manage";

    private static string Event(string eventId, string type, JsonObject data) =>
        new JsonObject { ["event_id"] = eventId, ["event_type"] = type, ["occurred_at"] = Iso(DateTimeOffset.UtcNow), ["data"] = data.DeepClone() }.ToJsonString();

    private static JsonObject Subscription(
        string id,
        string status,
        Guid organizationId,
        DateTimeOffset updatedAt,
        DateTimeOffset? periodEndsAt = null,
        DateTimeOffset? nextBilledAt = null,
        string priceId = MonthlyPrice,
        int seats = 1,
        string interval = "month") => new()
    {
        ["id"] = id,
        ["customer_id"] = "ctm_test",
        ["status"] = status,
        ["billing_cycle"] = new JsonObject { ["interval"] = interval, ["frequency"] = 1 },
        ["items"] = new JsonArray(new JsonObject { ["quantity"] = seats, ["price"] = new JsonObject { ["id"] = priceId } }),
        ["current_billing_period"] = periodEndsAt is { } end ? new JsonObject { ["starts_at"] = Iso(updatedAt), ["ends_at"] = Iso(end) } : null,
        ["next_billed_at"] = nextBilledAt is { } next ? Iso(next) : periodEndsAt is { } renews ? Iso(renews) : null,
        ["scheduled_change"] = null,
        ["custom_data"] = new JsonObject { ["organizationId"] = organizationId.ToString() },
        ["updated_at"] = Iso(updatedAt),
    };

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Gives each request the client address in <see cref="Header"/>, or Paddle's: the test server has none of its own.
    /// </summary>
    private sealed class FromAddress : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[Header].FirstOrDefault() ?? PaddleAddress);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Answers as Paddle's API would, and remembers what it was asked to do.</summary>
    private sealed class FakePaddle : HttpMessageHandler
    {
        public JsonObject? Transaction { get; private set; }

        public ConcurrentDictionary<string, JsonObject> Subscriptions { get; } = new();

        public ConcurrentBag<(string SubscriptionId, JsonObject Body)> Changes { get; } = [];

        public ConcurrentBag<string> Canceled { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();
            var segments = path.Split('/');

            JsonNode? data = (request.Method.Method, segments) switch
            {
                ("GET", ["prices", var id]) => new JsonObject
                {
                    ["id"] = id,
                    ["product_id"] = "pro_test",
                    ["name"] = id.EndsWith("yearly", StringComparison.Ordinal) ? "Yearly, per user" : "Monthly, per user",
                    ["unit_price"] = new JsonObject { ["amount"] = Amounts[id], ["currency_code"] = "EUR" },
                    ["tax_mode"] = "location",
                },
                ("GET", ["ips"]) => new JsonObject { ["ipv4_cidrs"] = new JsonArray($"{PaddleAddress}/32") },
                ("GET", ["customers"]) => new JsonArray(),
                ("POST", ["customers"]) => new JsonObject { ["id"] = "ctm_test" },
                ("POST", ["customers", _, "portal-sessions"]) => new JsonObject
                {
                    ["urls"] = new JsonObject { ["general"] = new JsonObject { ["overview"] = "https://sandbox-customer-portal.paddle.com/cpl_test" } },
                },
                ("POST", ["transactions"]) => Remember(body!),
                ("GET", ["subscriptions", var id]) => Subscriptions.TryGetValue(id, out var subscription) ? subscription.DeepClone() : null,
                ("PATCH", ["subscriptions", var id]) => Change(id, body!),
                ("POST", ["subscriptions", var id, "cancel"]) => Cancel(id),
                _ => null,
            };

            return data is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error = new { code = "not_found", detail = path } }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new JsonObject { ["data"] = data }) };
        }

        private JsonObject Remember(JsonObject transaction)
        {
            Transaction = transaction;
            return new JsonObject { ["id"] = "txn_test" };
        }

        private JsonObject Change(string id, JsonObject change)
        {
            Changes.Add((id, change));
            return Subscriptions.TryGetValue(id, out var subscription) ? (JsonObject)subscription.DeepClone() : new JsonObject { ["id"] = id };
        }

        private JsonObject Cancel(string id)
        {
            Canceled.Add(id);
            return new JsonObject { ["id"] = id, ["status"] = "canceled" };
        }
    }
}
