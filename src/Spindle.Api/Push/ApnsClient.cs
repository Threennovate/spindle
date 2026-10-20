using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spindle.Api.Push;

/// <summary>What became of a push.</summary>
internal enum ApnsResult
{
    Sent,

    /// <summary>The token no longer reaches a device, e.g. the app was removed or the activity ended: forget it.</summary>
    Gone,

    Failed,
}

/// <summary>The signed token every request to APNs carries. Apple wants a new one at most every 20 minutes, and at least hourly.</summary>
internal sealed class ApnsProviderToken(IOptions<PushOptions> options, TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly Lock gate = new();
    private (string Value, DateTimeOffset IssuedAt)? current;

    public string Get()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            if (current is { } token && now - token.IssuedAt < Lifetime)
            {
                return token.Value;
            }

            var apns = options.Value.Apns;
            using var key = ECDsa.Create();
            key.ImportFromPem(apns.PrivateKey!.Replace("\\n", "\n", StringComparison.Ordinal));
            // APNs reads only the issuer and the time; the default expiry and not-before claims stay out.
            var value = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = apns.TeamId,
                Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Iat] = now.ToUnixTimeSeconds() },
                // Signed once an hour; a cached signer would outlive the key.
                SigningCredentials = new SigningCredentials(
                    new ECDsaSecurityKey(key) { KeyId = apns.KeyId, CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } },
                    SecurityAlgorithms.EcdsaSha256),
            });
            current = (value, now);
            return value;
        }
    }
}

/// <summary>Sends Live Activity pushes through APNs, over HTTP/2.</summary>
internal sealed class ApnsClient(HttpClient http, ApnsProviderToken token, IOptions<PushOptions> options, ILogger<ApnsClient> logger)
{
    public static void Configure(HttpClient client) => client.Timeout = TimeSpan.FromSeconds(15);

    public async Task<ApnsResult> SendLiveActivityAsync(string deviceToken, bool sandbox, JsonObject payload, CancellationToken cancellationToken)
    {
        var host = sandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{deviceToken}")
        {
            Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            // APNs speaks only HTTP/2. A request made by hand doesn't take the client's default version; it would be 1.1.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {token.Get()}");
        request.Headers.Add("apns-push-type", "liveactivity");
        request.Headers.Add("apns-topic", $"{options.Value.Apns.BundleId}.push-type.liveactivity");
        request.Headers.Add("apns-priority", "10");

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return ApnsResult.Sent;
        }

        var reason = await ReasonAsync(response, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "Unregistered")
        {
            return ApnsResult.Gone;
        }

        logger.LogWarning("APNs refused a Live Activity push: {Status} {Reason}", (int)response.StatusCode, reason);
        return ApnsResult.Failed;
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            return body?["reason"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
