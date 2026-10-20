using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spindle.Tests.Integration;

/// <summary>
/// Google and Apple as far as the server sees them: discovery documents, signing keys and token endpoints, answered
/// here instead of on the internet. A test says which identity a code stands for, then the real provider code redeems
/// it and checks the ID token signed with this key.
/// </summary>
public sealed class FakeIdentityProviders : HttpMessageHandler
{
    public const string GoogleClientId = "google-client.apps.googleusercontent.com";
    public const string AppleServicesId = "com.spindletrack.tests";

    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly ConcurrentDictionary<string, Identity> codes = new();

    /// <summary>The Sign in with Apple key the server signs its client secret with, as a .p8 PEM.</summary>
    public ECDsa AppleKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string ApplePrivateKeyPem => AppleKey.ExportPkcs8PrivateKeyPem();

    /// <summary>The form of the last token request, by provider.</summary>
    public ConcurrentDictionary<string, Dictionary<string, string>> TokenRequests { get; } = new();

    /// <param name="HostedDomain">Google's <c>hd</c>: the Workspace domain that runs the address's mailbox.</param>
    /// <param name="PrivateEmail">Apple's <c>is_private_email</c>: an address Apple relays.</param>
    public sealed record Identity(string Provider, string Subject, string? Email, bool EmailVerified, string? Name, string Nonce,
        string? HostedDomain = null, bool PrivateEmail = false);

    /// <summary>Makes <paramref name="code"/> stand for the identity, once.</summary>
    public void Issue(string code, Identity identity) => codes[code] = identity;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        return url switch
        {
            "https://accounts.google.com/.well-known/openid-configuration" => Json(Discovery("https://accounts.google.com", "https://oauth2.googleapis.com/token", "https://www.googleapis.com/oauth2/v3/certs")),
            "https://appleid.apple.com/.well-known/openid-configuration" => Json(Discovery("https://appleid.apple.com", "https://appleid.apple.com/auth/token", "https://appleid.apple.com/auth/keys")),
            "https://www.googleapis.com/oauth2/v3/certs" or "https://appleid.apple.com/auth/keys" => Json(new { keys = new[] { Jwk() } }),
            "https://oauth2.googleapis.com/token" => await TokenAsync("google", "https://accounts.google.com", GoogleClientId, request, cancellationToken),
            "https://appleid.apple.com/auth/token" => await TokenAsync("apple", "https://appleid.apple.com", AppleServicesId, request, cancellationToken),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(string provider, string issuer, string audience, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        TokenRequests[provider] = form;
        if (!codes.TryRemove(form["code"], out var identity) || identity.Provider != provider)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        }

        var claims = new Dictionary<string, object>
        {
            ["sub"] = identity.Subject,
            ["nonce"] = identity.Nonce,
            // Apple has sent the flag as a string; Google as a boolean.
            ["email_verified"] = provider == "apple" ? (identity.EmailVerified ? "true" : "false") : identity.EmailVerified,
        };
        if (identity.Email is not null)
        {
            claims["email"] = identity.Email;
        }
        if (identity.Name is not null && provider == "google")
        {
            claims["name"] = identity.Name;
        }
        if (identity.HostedDomain is not null)
        {
            claims["hd"] = identity.HostedDomain;
        }
        if (identity.PrivateEmail)
        {
            claims["is_private_email"] = "true";
        }

        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
        return Json(new { access_token = "unused", token_type = "Bearer", id_token = idToken });
    }

    private static object Discovery(string issuer, string tokenEndpoint, string jwks) => new
    {
        issuer,
        authorization_endpoint = issuer + "/authorize",
        token_endpoint = tokenEndpoint,
        jwks_uri = jwks,
        response_types_supported = new[] { "code" },
        subject_types_supported = new[] { "public" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
    };

    private object Jwk()
    {
        var parameters = key.Rsa.ExportParameters(false);
        return new
        {
            kty = "RSA",
            use = "sig",
            alg = "RS256",
            kid = key.KeyId,
            n = Base64UrlEncoder.Encode(parameters.Modulus),
            e = Base64UrlEncoder.Encode(parameters.Exponent),
        };
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
