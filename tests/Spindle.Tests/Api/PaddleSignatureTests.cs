using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Subscriptions;

namespace Spindle.Tests.Api;

public sealed class PaddleSignatureTests
{
    private const string Secret = "pdl_ntfset_test_secret";
    private const string Body = """{"event_id":"evt_1","event_type":"subscription.created"}""";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var h1 = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}:{body}")));
        return $"ts={timestamp};h1={h1}";
    }

    [Fact]
    public void A_webhook_signed_with_the_secret_is_accepted()
    {
        Assert.True(PaddleProvider.SignatureIsValid(Sign(Body, Secret, Now), Body, Secret, Now));
        // While a secret is rotated, either of two signatures will do.
        Assert.True(PaddleProvider.SignatureIsValid($"{Sign(Body, "old", Now)};h1=nope", Body, "old", Now));
    }

    [Fact]
    public void A_changed_body_another_secret_or_an_old_timestamp_is_refused()
    {
        var header = Sign(Body, Secret, Now);

        Assert.False(PaddleProvider.SignatureIsValid(header, Body + " ", Secret, Now));
        Assert.False(PaddleProvider.SignatureIsValid(header, Body, "another secret", Now));
        Assert.False(PaddleProvider.SignatureIsValid(header, Body, Secret, Now + PaddleProvider.SignatureTolerance + TimeSpan.FromSeconds(1)));
        Assert.False(PaddleProvider.SignatureIsValid("", Body, Secret, Now));
        Assert.False(PaddleProvider.SignatureIsValid(header, Body, null, Now));
    }

    [Fact]
    public void A_paddle_subscription_is_read_in_our_words()
    {
        var data = JsonNode.Parse("""
            {
              "id": "sub_1", "customer_id": "ctm_1", "status": "active",
              "billing_cycle": { "interval": "year", "frequency": 1 },
              "items": [ { "quantity": 4, "price": { "id": "pri_1" } } ],
              "current_billing_period": { "starts_at": "2026-10-10T00:00:00Z", "ends_at": "2027-10-10T00:00:00Z" },
              "next_billed_at": "2027-10-10T00:00:00Z",
              "canceled_at": null,
              "scheduled_change": { "action": "cancel", "effective_at": "2027-10-10T00:00:00Z" },
              "updated_at": "2026-10-11T08:00:00.123Z"
            }
            """)!.AsObject();

        var subscription = PaddleProvider.Subscription(data);

        Assert.Equal(
            new ProviderSubscription(
                "paddle", "sub_1", "ctm_1", ProviderSubscriptionStatus.Active, BillingInterval.Year, 4,
                DateTimeOffset.Parse("2027-10-10T00:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2027-10-10T00:00:00Z", CultureInfo.InvariantCulture),
                null, true, DateTimeOffset.Parse("2026-10-11T08:00:00.123Z", CultureInfo.InvariantCulture)),
            subscription);
    }
}
