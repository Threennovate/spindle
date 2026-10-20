using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Spindle.Api.Auth;

/// <summary>Who the provider says signed in. The email counts only when the provider confirmed it.</summary>
/// <param name="EmailAuthoritative">
/// Whether the provider runs the mailbox itself, e.g. a Gmail or Google Workspace address, so the address still belongs
/// to this person today. Another address was only confirmed once, when the account was made, and may have changed hands.
/// </param>
internal sealed record ExternalIdentity(string Provider, string Subject, string? Email, bool EmailVerified, string? Name, bool EmailAuthoritative = false);

/// <summary>What came back to the callback besides the state: the code, and Apple's one-time name of the person.</summary>
internal sealed record ExternalCallback(string Code, string? User);

/// <summary>Google or Apple, as an OpenID Connect provider this server is a confidential client of.</summary>
internal interface IExternalSignInProvider
{
    /// <summary>The name in URLs and in the login table, e.g. <c>google</c>.</summary>
    string Name { get; }

    string DisplayName { get; }

    bool IsConfigured { get; }

    /// <summary>The provider's page the browser goes to, which comes back to <paramref name="redirectUri"/>.</summary>
    Uri AuthorizationUrl(string redirectUri, string state, string nonce, string codeChallenge);

    /// <summary>Redeems the code at the provider and checks the ID token it returns.</summary>
    Task<ExternalIdentity> RedeemAsync(ExternalCallback callback, string redirectUri, string nonce, string codeVerifier, CancellationToken cancellationToken);
}

/// <summary>
/// The parts of OpenID Connect that Google and Apple share: the code is redeemed with the client secret over the
/// back channel, and the ID token is checked against the provider's published keys, issuer, audience and our nonce.
/// </summary>
internal abstract class OidcSignInProvider : IExternalSignInProvider
{
    internal const string HttpClientName = "external-sign-in";

    private readonly IHttpClientFactory httpClients;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> configuration;

    protected OidcSignInProvider(IHttpClientFactory httpClients, string metadataAddress)
    {
        this.httpClients = httpClients;
        configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress, new OpenIdConnectConfigurationRetriever(), new FactoryDocumentRetriever(httpClients));
    }

    public abstract string Name { get; }

    public abstract string DisplayName { get; }

    public abstract bool IsConfigured { get; }

    protected abstract string ClientId { get; }

    protected abstract string[] Issuers { get; }

    public abstract Uri AuthorizationUrl(string redirectUri, string state, string nonce, string codeChallenge);

    protected abstract Dictionary<string, string> TokenRequest(string code, string redirectUri, string codeVerifier);

    /// <summary>Whether the provider runs the mailbox of <paramref name="email"/>, not just confirmed it once.</summary>
    protected abstract bool IsAuthoritativeFor(JsonWebToken idToken, string email);

    protected virtual string? NameFrom(JsonWebToken idToken, ExternalCallback callback) =>
        idToken.TryGetPayloadValue<string>("name", out var name) ? name : null;

    public async Task<ExternalIdentity> RedeemAsync(ExternalCallback callback, string redirectUri, string nonce, string codeVerifier, CancellationToken cancellationToken)
    {
        var metadata = await configuration.GetConfigurationAsync(cancellationToken);
        using var response = await httpClients.CreateClient(HttpClientName).PostAsync(
            metadata.TokenEndpoint, new FormUrlEncodedContent(TokenRequest(callback.Code, redirectUri, codeVerifier)), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalSignInException($"{DisplayName} refused the code ({(int)response.StatusCode}): {body}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.GetString() is not { Length: > 0 } idToken)
        {
            throw new ExternalSignInException($"{DisplayName} returned no ID token.");
        }

        var token = await ValidateAsync(idToken, metadata, cancellationToken);
        if (!token.TryGetPayloadValue<string>("nonce", out var tokenNonce) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(tokenNonce), System.Text.Encoding.UTF8.GetBytes(nonce)))
        {
            throw new ExternalSignInException($"The ID token from {DisplayName} is for another sign-in.");
        }

        token.TryGetPayloadValue<string>("email", out var email);
        var verified = IsTrue(token, "email_verified");
        return new ExternalIdentity(Name, token.Subject, email, verified, NameFrom(token, callback),
            EmailAuthoritative: verified && !string.IsNullOrWhiteSpace(email) && IsAuthoritativeFor(token, email.Trim()));
    }

    private async Task<JsonWebToken> ValidateAsync(string idToken, OpenIdConnectConfiguration metadata, CancellationToken cancellationToken)
    {
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(idToken, Parameters(metadata));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider rotated its keys since we last read them.
            configuration.RequestRefresh();
            result = await handler.ValidateTokenAsync(idToken, Parameters(await configuration.GetConfigurationAsync(cancellationToken)));
        }

        return result.IsValid && result.SecurityToken is JsonWebToken token && !string.IsNullOrEmpty(token.Subject)
            ? token
            : throw new ExternalSignInException($"The ID token from {DisplayName} is not valid.", result.Exception);
    }

    private TokenValidationParameters Parameters(OpenIdConnectConfiguration metadata) => new()
    {
        ValidIssuers = Issuers,
        ValidAudience = ClientId,
        IssuerSigningKeys = metadata.SigningKeys,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(2),
    };

    /// <summary>Google says <c>true</c>; Apple has said <c>"true"</c>.</summary>
    protected static bool IsTrue(JsonWebToken token, string claim) =>
        (token.TryGetPayloadValue<bool>(claim, out var flag) && flag)
        || (token.TryGetPayloadValue<string>(claim, out var text) && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));

    protected static Uri WithQuery(string endpoint, Dictionary<string, string?> query) => new(QueryHelpers.AddQueryString(endpoint, query));

    private sealed class FactoryDocumentRetriever(IHttpClientFactory httpClients) : IDocumentRetriever
    {
        public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
        {
            if (!address.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new ExternalSignInException($"Refusing to read {address} without HTTPS.");
            }

            return await httpClients.CreateClient(HttpClientName).GetStringAsync(address, cancel);
        }
    }
}

internal sealed class GoogleSignIn(IHttpClientFactory httpClients, IOptions<GoogleSignInOptions> options)
    : OidcSignInProvider(httpClients, "https://accounts.google.com/.well-known/openid-configuration")
{
    public override string Name => "google";

    public override string DisplayName => "Google";

    public override bool IsConfigured => options.Value.IsConfigured;

    protected override string ClientId => options.Value.ClientId!;

    protected override string[] Issuers => ["https://accounts.google.com", "accounts.google.com"];

    /// <summary>
    /// Gmail, or a Google Workspace account (<c>hd</c>) of the address's own domain, as Google's guide to ID tokens
    /// says. For any other address Google only checked it when the account was made.
    /// </summary>
    protected override bool IsAuthoritativeFor(JsonWebToken idToken, string email) =>
        email.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase)
        || (idToken.TryGetPayloadValue<string>("hd", out var domain) && !string.IsNullOrEmpty(domain)
            && email.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase));

    public override Uri AuthorizationUrl(string redirectUri, string state, string nonce, string codeChallenge) =>
        WithQuery("https://accounts.google.com/o/oauth2/v2/auth", new()
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Someone with several Google accounts chooses one, instead of silently getting the last one used.
            ["prompt"] = "select_account",
        });

    protected override Dictionary<string, string> TokenRequest(string code, string redirectUri, string codeVerifier) => new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = redirectUri,
        ["client_id"] = ClientId,
        ["client_secret"] = options.Value.ClientSecret!,
        ["code_verifier"] = codeVerifier,
    };
}

/// <summary>
/// Sign in with Apple on the web. Apple posts the result back as a form, sends the person's name only the first time
/// they sign in, and wants a client secret that we sign ourselves with the team's key.
/// </summary>
internal sealed class AppleSignIn(IHttpClientFactory httpClients, IOptions<AppleSignInOptions> options, TimeProvider clock)
    : OidcSignInProvider(httpClients, "https://appleid.apple.com/.well-known/openid-configuration")
{
    private const string Issuer = "https://appleid.apple.com";

    public override string Name => "apple";

    public override string DisplayName => "Apple";

    public override bool IsConfigured => options.Value.IsConfigured;

    protected override string ClientId => options.Value.ServicesId!;

    protected override string[] Issuers => [Issuer];

    private static readonly string[] AppleMailDomains = ["icloud.com", "me.com", "mac.com", "privaterelay.appleid.com"];

    /// <summary>A hidden address Apple relays, or Apple's own mail; any other address was only checked once.</summary>
    protected override bool IsAuthoritativeFor(JsonWebToken idToken, string email) =>
        IsTrue(idToken, "is_private_email")
        || AppleMailDomains.Any(domain => email.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase));

    public override Uri AuthorizationUrl(string redirectUri, string state, string nonce, string codeChallenge) =>
        WithQuery("https://appleid.apple.com/auth/authorize", new()
        {
            ["response_type"] = "code",
            // Apple sends the name and email only to a form post.
            ["response_mode"] = "form_post",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "name email",
            ["state"] = state,
            ["nonce"] = nonce,
        });

    protected override Dictionary<string, string> TokenRequest(string code, string redirectUri, string codeVerifier) => new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = redirectUri,
        ["client_id"] = ClientId,
        ["client_secret"] = ClientSecret(),
    };

    protected override string? NameFrom(JsonWebToken idToken, ExternalCallback callback)
    {
        if (callback.User is not { Length: > 0 and < 4096 } user)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(user);
            if (!json.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Part(string key) => name.TryGetProperty(key, out var part) && part.ValueKind == JsonValueKind.String ? part.GetString() : null;
            var full = $"{Part("firstName")} {Part("lastName")}".Trim();
            return full.Length > 0 ? full : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>An ES256 token signed with the Sign in with Apple key, as Apple asks for in place of a fixed secret.</summary>
    internal string ClientSecret()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Value.TeamId,
            Audience = Issuer,
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = ClientId },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey.Value, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>
    /// The key, read once and kept while the server runs. IdentityModel keeps the signer it makes for a key by the key's
    /// ID, so a key made and disposed for each token would leave the next token with a disposed one.
    /// </summary>
    private readonly Lazy<ECDsaSecurityKey> signingKey = new(() =>
    {
        var key = ECDsa.Create();
        key.ImportFromPem(Pem(options.Value.PrivateKey!));
        return new ECDsaSecurityKey(key) { KeyId = options.Value.KeyId };
    });

    /// <summary>The key as a PEM, whether the setting has its line breaks, <c>\n</c> in their place, or only the base64.</summary>
    private static string Pem(string value)
    {
        var text = value.Replace("\\n", "\n", StringComparison.Ordinal).Trim();
        return text.StartsWith("-----", StringComparison.Ordinal)
            ? text
            : $"-----BEGIN PRIVATE KEY-----\n{text}\n-----END PRIVATE KEY-----";
    }
}

internal sealed class ExternalSignInException(string message, Exception? inner = null) : Exception(message, inner);
