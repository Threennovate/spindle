using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Spindle.Domain.Subscriptions;

namespace Spindle.Api.Subscriptions;

/// <summary>Paddle answered with an error, e.g. a refused change. <see cref="Code"/> is Paddle's error code.</summary>
internal sealed class PaddleException(string message, string? code) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>
/// Paddle Billing, which sells the hosted service as merchant of record: it charges the customer, handles their taxes
/// and invoices, and pays us out. It speaks Paddle's REST API directly (there is no .NET SDK), version 1.
/// </summary>
/// <remarks>
/// The checkout always uses a price made for that checkout, from the catalog price of the organization's price cohort
/// and the chosen interval: with the days left of our trial as Paddle's trial, so the buyer sees when the first charge
/// comes, and the seats fixed, so they can't be changed there. Once the subscription exists,
/// <see cref="ReconcileAsync"/> moves it to the catalog price and its first charge to the exact end of our trial.
/// </remarks>
internal sealed class PaddleProvider(
    HttpClient http,
    IOptions<SubscriptionOptions> options,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<PaddleProvider> logger) : ISubscriptionProvider
{
    public const string ProviderName = "paddle";

    /// <summary>How far a webhook's timestamp may be from now, against replaying an old one.</summary>
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PriceCacheLifetime = TimeSpan.FromHours(1);

    /// <summary>How long Paddle's list of the addresses it sends webhooks from is kept before asking again.</summary>
    private static readonly TimeSpan SourceCacheLifetime = TimeSpan.FromHours(1);

    private PaddleOptions Settings => options.Value.Paddle;

    public string Name => ProviderName;

    public bool IsConfigured => Settings.IsConfigured
        && options.Value.CurrentPriceCohort is { } current
        && HasPrices(current);

    public IReadOnlyCollection<string> PriceCohorts => [.. Settings.PriceCohorts.Where(c => c.Value.IsComplete).Select(c => c.Key)];

    public bool HasPrices(string priceCohort) => Settings.PriceId(priceCohort, BillingInterval.Month) is not null;

    public static Uri BaseAddress(string environment) =>
        new(environment == "production" ? "https://api.paddle.com/" : "https://sandbox-api.paddle.com/");

    public static void Configure(HttpClient client, PaddleOptions settings)
    {
        client.BaseAddress = BaseAddress(settings.Environment);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        client.DefaultRequestHeaders.Add("Paddle-Version", "1");
        client.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<IReadOnlyList<PlanPrice>> PricesAsync(string priceCohort, CancellationToken cancellationToken)
    {
        var prices = new List<PlanPrice>();
        foreach (var interval in Enum.GetValues<BillingInterval>())
        {
            var price = await CatalogPriceAsync(priceCohort, interval, cancellationToken);
            prices.Add(new PlanPrice(interval, price.UnitAmount, price.Currency));
        }

        return prices;
    }

    public async Task<CheckoutStart> StartCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var catalog = await CatalogPriceAsync(request.PriceCohort, request.Interval, cancellationToken);
        var customerId = request.CustomerId ?? await CustomerIdAsync(request.BuyerEmail, cancellationToken);

        var price = new JsonObject
        {
            ["description"] = $"Checkout for organization {request.OrganizationId}",
            ["name"] = catalog.Name,
            ["product_id"] = catalog.ProductId,
            ["unit_price"] = new JsonObject { ["amount"] = catalog.Amount, ["currency_code"] = catalog.Currency },
            ["billing_cycle"] = new JsonObject { ["interval"] = IntervalName(request.Interval), ["frequency"] = 1 },
            ["tax_mode"] = catalog.TaxMode,
            ["quantity"] = new JsonObject { ["minimum"] = request.Seats, ["maximum"] = request.Seats },
        };
        if (request.TrialDays > 0)
        {
            price["trial_period"] = new JsonObject { ["interval"] = "day", ["frequency"] = request.TrialDays };
        }

        var transaction = await SendAsync(HttpMethod.Post, "transactions", new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["quantity"] = request.Seats, ["price"] = price }),
            ["customer_id"] = customerId,
            ["custom_data"] = new JsonObject { ["organizationId"] = request.OrganizationId.ToString() },
        }, cancellationToken);

        return new CheckoutStart(ProviderName, Settings.Environment, Settings.ClientToken!, Text(transaction, "id")!, customerId);
    }

    public async Task<Uri> ManageUrlAsync(string customerId, string subscriptionId, CancellationToken cancellationToken)
    {
        var session = await SendAsync(
            HttpMethod.Post,
            $"customers/{Uri.EscapeDataString(customerId)}/portal-sessions",
            new JsonObject { ["subscription_ids"] = new JsonArray(subscriptionId) },
            cancellationToken);
        return new Uri(session["urls"]?["general"]?["overview"]?.GetValue<string>()
            ?? throw new PaddleException("Paddle returned no customer portal link.", null));
    }

    public async Task ReconcileAsync(string subscriptionId, SubscriptionTarget target, CancellationToken cancellationToken)
    {
        var subscription = await SendAsync(HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(subscriptionId)}", null, cancellationToken);
        var status = Text(subscription, "status");
        // Ended or ending: nothing to keep in line. A scheduled cancellation also refuses changes to items.
        if (status is "canceled" or "paused" || subscription["scheduled_change"] is JsonObject)
        {
            return;
        }

        var trialing = status == "trialing";
        var priceId = PriceId(target.PriceCohort, target.Interval);
        var items = subscription["items"]?.AsArray() ?? [];
        // In the trial nothing is charged; afterwards seats added or removed are settled with the next bill, so
        // every change doesn't become a charge of its own.
        var proration = trialing ? "do_not_bill" : "prorated_next_billing_period";
        var path = $"subscriptions/{Uri.EscapeDataString(subscriptionId)}";

        if (items.Count != 1
            || items[0]?["price"]?["id"]?.GetValue<string>() != priceId
            || items[0]?["quantity"]?.GetValue<int>() != target.Seats)
        {
            await SendAsync(HttpMethod.Patch, path, new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["price_id"] = priceId, ["quantity"] = target.Seats }),
                ["proration_billing_mode"] = proration,
            }, cancellationToken);
            logger.LogInformation("Set Paddle subscription {SubscriptionId} to {Seats} seats at {PriceId}", subscriptionId, target.Seats, priceId);
        }

        // Paddle changes the items and the billing date only in separate requests.
        if (trialing
            && target.FirstChargeAt is { } firstCharge
            && firstCharge > timeProvider.GetUtcNow()
            && Date(subscription, "next_billed_at") is { } nextBilled
            && (nextBilled - firstCharge).Duration() > TimeSpan.FromMinutes(1))
        {
            await SendAsync(HttpMethod.Patch, path, new JsonObject
            {
                ["next_billed_at"] = firstCharge.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["proration_billing_mode"] = proration,
            }, cancellationToken);
            logger.LogInformation("Moved the first charge of Paddle subscription {SubscriptionId} to the end of the trial, {FirstCharge}", subscriptionId, firstCharge);
        }
    }

    public async Task CancelAsync(string subscriptionId, CancellationToken cancellationToken) =>
        await SendAsync(
            HttpMethod.Post,
            $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/cancel",
            new JsonObject { ["effective_from"] = "immediately" },
            cancellationToken);

    public async Task<ProviderEvent?> ReadWebhookAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (Settings.CheckWebhookSources && !await IsFromPaddleAsync(request.HttpContext.Connection.RemoteIpAddress, cancellationToken))
        {
            logger.LogWarning("Refused a webhook from {Address}, which is not one of Paddle's", request.HttpContext.Connection.RemoteIpAddress);
            return null;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);
        if (!SignatureIsValid(request.Headers["Paddle-Signature"].ToString(), body, Settings.WebhookSecret, timeProvider.GetUtcNow()))
        {
            return null;
        }

        var notification = JsonNode.Parse(body)?.AsObject()
            ?? throw new PaddleException("The webhook has no body.", null);
        var eventId = Text(notification, "event_id") ?? throw new PaddleException("The webhook has no event ID.", null);
        var data = notification["data"] as JsonObject;
        if (Text(notification, "event_type") is not { } type || !type.StartsWith("subscription.", StringComparison.Ordinal) || data is null)
        {
            return new ProviderEvent(eventId, null, null);
        }

        Guid? organizationId = Guid.TryParse(data["custom_data"]?["organizationId"]?.GetValue<string>(), out var id) ? id : null;
        return new ProviderEvent(eventId, organizationId, Subscription(data));
    }

    /// <summary>
    /// Checks the <c>Paddle-Signature</c> header: <c>ts=…;h1=…</c>, where h1 is the HMAC-SHA256 of
    /// <c>{ts}:{body}</c> with the destination's secret. A rotated secret means two h1 values for a while.
    /// </summary>
    public static bool SignatureIsValid(string header, string body, string? secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "ts")
            {
                timestamp = pair[1];
            }
            else if (pair[0] == "h1")
            {
                signatures.Add(pair[1]);
            }
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > SignatureTolerance)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}:{body}"))));
        return signatures.Any(signature => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(signature)));
    }

    /// <summary>A subscription as Paddle's webhooks and API describe it.</summary>
    public static ProviderSubscription Subscription(JsonObject data)
    {
        var status = Text(data, "status") switch
        {
            "trialing" => ProviderSubscriptionStatus.Trialing,
            "active" => ProviderSubscriptionStatus.Active,
            "past_due" => ProviderSubscriptionStatus.PastDue,
            "paused" => ProviderSubscriptionStatus.Paused,
            "canceled" => ProviderSubscriptionStatus.Canceled,
            var other => throw new PaddleException($"Unknown subscription status '{other}'.", null),
        };
        var interval = data["billing_cycle"]?["interval"]?.GetValue<string>() == "year" ? BillingInterval.Year : BillingInterval.Month;
        var seats = (data["items"]?.AsArray() ?? []).Sum(item => item?["quantity"]?.GetValue<int>() ?? 0);

        return new ProviderSubscription(
            ProviderName,
            Text(data, "id") ?? throw new PaddleException("The subscription has no ID.", null),
            Text(data, "customer_id") ?? throw new PaddleException("The subscription has no customer.", null),
            status,
            interval,
            seats,
            Date(data["current_billing_period"] as JsonObject, "ends_at"),
            Date(data, "next_billed_at"),
            Date(data, "canceled_at") ?? Date(data, "paused_at"),
            data["scheduled_change"]?["action"]?.GetValue<string>() == "cancel",
            Date(data, "updated_at") ?? throw new PaddleException("The subscription has no update time.", null));
    }

    private string PriceId(string priceCohort, BillingInterval interval) =>
        Settings.PriceId(priceCohort, interval)
        ?? throw new InvalidOperationException($"The Paddle prices of price cohort '{priceCohort}' are not configured.");

    private async Task<CatalogPrice> CatalogPriceAsync(string priceCohort, BillingInterval interval, CancellationToken cancellationToken)
    {
        var id = PriceId(priceCohort, interval);
        if (cache.TryGetValue<CatalogPrice>(("paddle-price", id), out var cached) && cached is not null)
        {
            return cached;
        }

        var price = await SendAsync(HttpMethod.Get, $"prices/{Uri.EscapeDataString(id)}", null, cancellationToken);
        var currency = price["unit_price"]?["currency_code"]?.GetValue<string>() ?? "EUR";
        var amount = price["unit_price"]?["amount"]?.GetValue<string>() ?? "0";
        var catalog = new CatalogPrice(
            Text(price, "product_id")!,
            Text(price, "name") ?? "Spindle",
            amount,
            currency,
            decimal.Parse(amount, CultureInfo.InvariantCulture) / MinorUnits(currency),
            Text(price, "tax_mode") ?? "account_setting");
        return cache.Set(("paddle-price", id), catalog, PriceCacheLifetime);
    }

    /// <summary>The Paddle customer with this email: the one there is, or a new one.</summary>
    private async Task<string> CustomerIdAsync(string email, CancellationToken cancellationToken)
    {
        var found = await SendListAsync($"customers?email={Uri.EscapeDataString(email)}", cancellationToken);
        if (found.Count > 0 && found[0]?["id"]?.GetValue<string>() is { } existing)
        {
            return existing;
        }

        return Text(await SendAsync(HttpMethod.Post, "customers", new JsonObject { ["email"] = email }, cancellationToken), "id")!;
    }

    /// <summary>
    /// Whether the address is one Paddle sends webhooks from, by Paddle's own list for the environment
    /// (<c>GET /ips</c>), which can change and so is asked for rather than kept here.
    /// </summary>
    private async Task<bool> IsFromPaddleAsync(IPAddress? address, CancellationToken cancellationToken)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!cache.TryGetValue<IPNetwork[]>(("paddle-ips", Settings.Environment), out var networks) || networks is null)
        {
            var ips = await SendAsync(HttpMethod.Get, "ips", null, cancellationToken);
            networks = [.. (ips["ipv4_cidrs"] as JsonArray ?? []).Select(cidr => IPNetwork.Parse(cidr!.GetValue<string>()))];
            cache.Set(("paddle-ips", Settings.Environment), networks, SourceCacheLifetime);
        }

        return networks.Any(network => network.Contains(address));
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken) =>
        (await SendRawAsync(method, path, body, cancellationToken))["data"] as JsonObject
        ?? throw new PaddleException($"Paddle returned no data for {method} {path}.", null);

    private async Task<JsonArray> SendListAsync(string path, CancellationToken cancellationToken) =>
        (await SendRawAsync(HttpMethod.Get, path, null, cancellationToken))["data"] as JsonArray ?? [];

    private async Task<JsonNode> SendRawAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = json?["error"];
            throw new PaddleException(
                $"Paddle refused {method} {path} ({(int)response.StatusCode}): {error?["detail"]?.GetValue<string>() ?? "no detail"}",
                error?["code"]?.GetValue<string>());
        }

        return json ?? throw new PaddleException($"Paddle returned nothing for {method} {path}.", null);
    }

    private static string IntervalName(BillingInterval interval) => interval == BillingInterval.Year ? "year" : "month";

    // Paddle's amounts are in the lowest unit; these currencies have none below the main one.
    private static decimal MinorUnits(string currency) => currency is "JPY" or "KRW" or "CLP" or "VND" ? 1m : 100m;

    private static string? Text(JsonObject? node, string name) => node?[name]?.GetValue<string>();

    private static DateTimeOffset? Date(JsonObject? node, string name) =>
        Text(node, name) is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    private sealed record CatalogPrice(string ProductId, string Name, string Amount, string Currency, decimal UnitAmount, string TaxMode);
}
