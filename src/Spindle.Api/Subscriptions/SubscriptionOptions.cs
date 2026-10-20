using System.ComponentModel.DataAnnotations;
using Spindle.Domain.Subscriptions;

namespace Spindle.Api.Subscriptions;

public sealed class SubscriptionOptions : IValidatableObject
{
    public const string SectionName = "Subscriptions";

    /// <summary>
    /// Whether this server is the hosted service, where organizations have a trial and then pay. Off by default, which
    /// is what a self-hosted server wants: then nobody is ever limited and the apps show no trial or plan.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How long the trial of a new organization lasts.</summary>
    [Range(1, 365)]
    public int TrialDays { get; set; } = 90;

    /// <summary>
    /// The price cohort new organizations join at sign-up, e.g. <c>founding</c>; its prices are under
    /// <see cref="PaddleOptions.PriceCohorts"/>. Every organization keeps the cohort it joined, so changing this changes
    /// the price for new organizations only. Lower case, digits and underscores, e.g. <c>standard_2027</c>: the name is
    /// part of environment variable names, where a hyphen doesn't work everywhere.
    /// </summary>
    [RegularExpression("^[a-z0-9][a-z0-9_]{0,39}$")]
    public string? CurrentPriceCohort { get; set; }

    /// <summary>Paddle, which takes the payments. Without its settings nobody can pay yet, and trials end as before.</summary>
    public PaddleOptions Paddle { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) => Paddle.Validate();
}

/// <summary>
/// The Paddle account the hosted service sells through. The sandbox and the live account differ only in these
/// settings. The API key and webhook secret are secrets; the client token is public, as Paddle.js sends it from the
/// browser anyway.
/// </summary>
public sealed class PaddleOptions
{
    /// <summary><c>sandbox</c> for testing, <c>production</c> for real payments.</summary>
    [RegularExpression("^(sandbox|production)$")]
    public string Environment { get; set; } = "sandbox";

    public string? ApiKey { get; set; }

    /// <summary>The client-side token Paddle.js opens the checkout with.</summary>
    public string? ClientToken { get; set; }

    /// <summary>The secret of the notification destination, which signs every webhook.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Whether webhooks are taken only from the addresses Paddle sends them from, besides being signed. Behind a reverse
    /// proxy that needs <c>ReverseProxy:KnownNetworks</c>, or every webhook seems to come from the proxy. Turn it off
    /// only to receive sandbox webhooks through a tunnel while developing.
    /// </summary>
    public bool CheckWebhookSources { get; set; } = true;

    /// <summary>
    /// The catalog prices of each price cohort, by its name. Cohorts are only ever added: an organization in a cohort
    /// that is missing here can't pay, and its subscription isn't touched.
    /// </summary>
    public Dictionary<string, PaddleCohortPrices> PriceCohorts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys of the other environment than <see cref="Environment"/>, e.g. live keys with the default <c>sandbox</c>:
    /// the server would start, and every checkout would fail. Keys without a known prefix aren't judged.
    /// </summary>
    internal IEnumerable<ValidationResult> Validate()
    {
        var (apiKeyPrefix, clientTokenPrefix) = Environment == "production" ? ("pdl_sdbx_", "test_") : ("pdl_live_", "live_");
        if (ApiKey?.StartsWith(apiKeyPrefix, StringComparison.Ordinal) == true)
        {
            yield return new ValidationResult(
                $"Subscriptions:Paddle:ApiKey is a key of the other environment than Subscriptions:Paddle:Environment ({Environment}).",
                [nameof(ApiKey)]);
        }

        if (ClientToken?.StartsWith(clientTokenPrefix, StringComparison.Ordinal) == true)
        {
            yield return new ValidationResult(
                $"Subscriptions:Paddle:ClientToken is a token of the other environment than Subscriptions:Paddle:Environment ({Environment}).",
                [nameof(ClientToken)]);
        }
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ClientToken)
        && !string.IsNullOrWhiteSpace(WebhookSecret);

    /// <summary>The cohort's catalog price per seat for the interval, if both of its prices are set.</summary>
    public string? PriceId(string cohort, BillingInterval interval) =>
        PriceCohorts.TryGetValue(cohort, out var prices) && prices.IsComplete
            ? interval == BillingInterval.Year ? prices.YearlyPriceId : prices.MonthlyPriceId
            : null;
}

/// <summary>A price cohort's two catalog prices, per seat. Each cohort has its own, even when the amounts are the same.</summary>
public sealed class PaddleCohortPrices
{
    public string? MonthlyPriceId { get; set; }

    public string? YearlyPriceId { get; set; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(MonthlyPriceId) && !string.IsNullOrWhiteSpace(YearlyPriceId);
}
