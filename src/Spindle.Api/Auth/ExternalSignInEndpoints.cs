using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

public sealed record ExternalExchangeRequest
{
    /// <summary>The code from the redirect back to the app.</summary>
    [Required, StringLength(100)]
    public string Code { get; init; } = string.Empty;

    /// <summary>The PKCE verifier whose S256 challenge the app sent when it started.</summary>
    [Required, StringLength(128, MinimumLength = 43)]
    public string CodeVerifier { get; init; } = string.Empty;

    [StringLength(RefreshToken.DeviceNameMaxLength)]
    public string? DeviceName { get; init; }
}

public sealed record WebExternalExchangeRequest
{
    /// <summary>The code from the redirect back to the web app.</summary>
    [Required, StringLength(100)]
    public string Code { get; init; } = string.Empty;
}

/// <param name="NewAccount">Whether signing in created the account, so the app can welcome the person.</param>
public sealed record ExternalSignInResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool NewAccount);

/// <param name="NewAccount">Whether signing in created the account.</param>
/// <param name="ReturnTo">The page of this web app to go to, as asked for when signing in started.</param>
public sealed record WebExternalSignInResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, bool NewAccount, string? ReturnTo);

/// <summary>A provider this server offers for signing in, e.g. Google.</summary>
public sealed record SignInProviderResponse(string Name, string DisplayName);

/// <summary>A Google or Apple login of the person's account.</summary>
public sealed record ExternalLoginResponse(string Provider, string DisplayName);

/// <summary>
/// Signing in with Google or Apple. This server is the OpenID Connect client: the browser or app opens
/// <c>start</c>, the provider comes back to <c>callback</c>, and the server redirects to the web app or the app with a
/// one-time code. The code is worth a session only together with the secret of the client that started: the web
/// app's cookie or the app's PKCE verifier, so a code caught on its way is useless, and nobody can sign someone else
/// in to their own account.
/// </summary>
internal static class ExternalSignInEndpoints
{
    public const string RateLimit = "external-sign-in";
    public const string BindingCookieName = "tt_external";
    private const string BindingCookiePath = "/api/auth/web/external";

    /// <summary>Where the Mac, iPhone and Android apps get the code back.</summary>
    public const string AppRedirectUri = "spindle://auth/callback";

    /// <summary>Where the web app gets the code back, a page of its own.</summary>
    public const string WebReturnPath = "/external-sign-in";

    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    private const string ExpiredSignIn = "This sign-in took too long or was already used. Please start again.";

    public static IEndpointRouteBuilder MapExternalSignInEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth/external").WithTags("Auth").AllowAnonymous()
            .RequireRateLimiting(RateLimit).ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapGet("/{provider}/start", StartAsync).WithName("startExternalSignIn")
            .Produces(StatusCodes.Status302Found).ProducesProblem(StatusCodes.Status404NotFound);
        // Google comes back with a GET; Apple posts a form, so the request carries no cookies of ours and needs none.
        group.MapMethods("/{provider}/callback", [HttpMethods.Get, HttpMethods.Post], CallbackAsync)
            .DisableAntiforgery().ExcludeFromDescription();
        group.MapPost("/exchange", ExchangeAsync).WithName("exchangeExternalSignIn")
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized);

        routes.MapPost("/auth/web/external/exchange", WebExchangeAsync).WithName("webExchangeExternalSignIn")
            .WithTags("Web auth").AllowAnonymous().RequireRateLimiting(RateLimit)
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var me = routes.MapGroup("/me/logins").WithTags("Me").RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized);
        me.MapGet("/", ListLoginsAsync).WithName("listExternalLogins");
        me.MapDelete("/{provider}", RemoveLoginAsync).WithName("removeExternalLogin");

        return routes;
    }

    /// <summary>The providers this server is set up for, in the order the apps show them.</summary>
    public static IReadOnlyList<SignInProviderResponse> Offered(IEnumerable<IExternalSignInProvider> providers) =>
        providers.Where(p => p.IsConfigured).Select(p => new SignInProviderResponse(p.Name, p.DisplayName)).ToList();

    /// <summary>
    /// Sends the browser to Google or Apple. The web app opens it as a page; an app opens it in the system browser
    /// with <paramref name="redirectUri"/> and the S256 <paramref name="codeChallenge"/> of a verifier it keeps.
    /// </summary>
    /// <param name="returnTo">For the web app: the page to go to afterwards.</param>
    /// <param name="invitation">The token of an invitation to the person's address, for a new account to join its workspace.</param>
    /// <param name="timeZone">The device's IANA time zone, for a new account.</param>
    /// <param name="acceptTerms">That the person agrees to the server's terms, shown next to the button, for a new account.</param>
    private static Results<RedirectHttpResult, NotFound, ValidationProblem> StartAsync(
        string provider,
        [FromQuery] string? redirectUri,
        [FromQuery] string? codeChallenge,
        [FromQuery] string? returnTo,
        [FromQuery] string? invitation,
        [FromQuery] string? timeZone,
        [FromQuery] bool? acceptTerms,
        IEnumerable<IExternalSignInProvider> providers,
        IDataProtectionProvider protection,
        IOptions<AppOptions> app,
        HttpContext http)
    {
        if (Find(providers, provider) is not { } signIn)
        {
            return TypedResults.NotFound();
        }

        string bindingHash;
        if (redirectUri is null && codeChallenge is null)
        {
            var secret = RandomToken();
            bindingHash = Hash(secret);
            http.Response.Cookies.Append(BindingCookieName, secret, BindingCookieOptions(http, FlowLifetime));
        }
        else if (redirectUri is null || !IsAppRedirect(redirectUri))
        {
            return Problems.Validation("redirectUri", $"Use {AppRedirectUri} or a loopback address such as http://127.0.0.1:port/auth/callback.");
        }
        else if (codeChallenge is not { Length: 43 } || !IsBase64Url(codeChallenge))
        {
            return Problems.Validation("codeChallenge", "Send the base64url S256 challenge of a PKCE verifier.");
        }
        else
        {
            bindingHash = codeChallenge;
        }

        var verifier = RandomToken();
        var state = new ExternalSignInState(
            signIn.Name, RandomToken(), verifier, bindingHash, redirectUri, SafeReturnPath(returnTo),
            invitation is { Length: <= 100 } ? invitation : null, timeZone, acceptTerms == true);
        var protectedState = Protector(protection).Protect(JsonSerializer.Serialize(state), FlowLifetime);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Redirect(signIn.AuthorizationUrl(CallbackUri(app.Value, signIn), protectedState, state.Nonce, Hash(verifier)).AbsoluteUri);
    }

    private static async Task<IResult> CallbackAsync(
        string provider,
        IEnumerable<IExternalSignInProvider> providers,
        IDataProtectionProvider protection,
        ExternalAccounts accounts,
        AppDbContext db,
        IOptions<AppOptions> app,
        TimeProvider clock,
        ILogger<ExternalAccounts> logger,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(cancellationToken) : null;
        string? Value(string key) => form is not null && form.TryGetValue(key, out var posted) ? posted.FirstOrDefault() : http.Request.Query[key].FirstOrDefault();

        http.Response.Headers.CacheControl = "no-store";
        var signIn = Find(providers, provider);
        var state = signIn is null ? null : Unprotect(protection, Value("state"));
        if (signIn is null || state is null || state.Provider != signIn.Name)
        {
            // Without the state there's no telling which app started, so the web app says it.
            return Results.Redirect(app.Value.Link($"{WebReturnPath}?error=expired"));
        }

        if (Value("error") is { } error)
        {
            // Apple says user_cancelled_authorize, Google access_denied, when the person turns back.
            if (error is "access_denied" or "user_cancelled_authorize")
            {
                return Results.Redirect(ClientRedirect(app.Value, state, "error", "cancelled"));
            }

            logger.LogWarning("{Provider} sign-in came back with {Error}", signIn.Name, error);
            return Results.Redirect(ClientRedirect(app.Value, state, "code",
                await StoreAsync(db, clock, state, new(null, $"{signIn.DisplayName} couldn't sign you in. Please try again.", false), cancellationToken)));
        }

        ExternalSignInOutcome outcome;
        if (Value("code") is not { Length: > 0 and <= 4096 } code)
        {
            outcome = new(null, $"{signIn.DisplayName} couldn't sign you in. Please try again.", false);
        }
        else
        {
            try
            {
                var identity = await signIn.RedeemAsync(new ExternalCallback(code, Value("user")), CallbackUri(app.Value, signIn), state.Nonce, state.CodeVerifier, cancellationToken);
                outcome = await accounts.SignInAsync(identity, signIn.DisplayName, new(state.InvitationToken, state.TimeZone, state.AcceptTerms), cancellationToken);
            }
            catch (Exception exception) when (exception is ExternalSignInException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Couldn't complete the {Provider} sign-in", signIn.Name);
                outcome = new(null, $"{signIn.DisplayName} couldn't confirm who you are. Please try again.", false);
            }
        }

        return Results.Redirect(ClientRedirect(app.Value, state, "code", await StoreAsync(db, clock, state, outcome, cancellationToken)));
    }

    private static async Task<Results<Ok<ExternalSignInResponse>, ProblemHttpResult>> ExchangeAsync(
        ExternalExchangeRequest request, AppDbContext db, AuthService auth, TimeProvider clock, HttpContext http, CancellationToken cancellationToken)
    {
        var (code, problem) = await RedeemAsync(db, clock, request.Code, Hash(request.CodeVerifier), cancellationToken);
        if (code is null)
        {
            return problem!;
        }

        var session = await auth.StartSessionAsync(code.UserId!.Value, request.DeviceName, cancellationToken);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new ExternalSignInResponse(session.AccessToken.Value, session.AccessToken.ExpiresAt,
            session.RefreshToken.Value, session.RefreshToken.ExpiresAt, code.NewAccount));
    }

    private static async Task<Results<Ok<WebExternalSignInResponse>, ProblemHttpResult>> WebExchangeAsync(
        WebExternalExchangeRequest request, AppDbContext db, AuthService auth, TimeProvider clock, HttpContext http, CancellationToken cancellationToken)
    {
        var secret = http.Request.Cookies[BindingCookieName];
        http.Response.Cookies.Delete(BindingCookieName, BindingCookieOptions(http, null));
        if (secret is not { Length: > 0 and <= 100 })
        {
            // Codes are deleted even so, so one can't wait for a cookie.
            await RedeemAsync(db, clock, request.Code, string.Empty, cancellationToken);
            return Expired();
        }

        var (code, problem) = await RedeemAsync(db, clock, request.Code, Hash(secret), cancellationToken);
        if (code is null)
        {
            return problem!;
        }

        var session = await auth.StartSessionAsync(code.UserId!.Value, DeviceNames.FromUserAgent(http.Request.Headers.UserAgent), cancellationToken);
        var access = WebAuthEndpoints.StartCookieSession(http, session);
        return TypedResults.Ok(new WebExternalSignInResponse(access.AccessToken, access.AccessTokenExpiresAt, code.NewAccount, code.ReturnPath));
    }

    private static async Task<Ok<List<ExternalLoginResponse>>> ListLoginsAsync(
        System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, IEnumerable<IExternalSignInProvider> providers, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var logins = await db.UserLogins.Where(l => l.UserId == userId).Select(l => l.LoginProvider).ToListAsync(cancellationToken);
        return TypedResults.Ok(logins.Order()
            .Select(name => new ExternalLoginResponse(name, providers.FirstOrDefault(p => p.Name == name)?.DisplayName ?? name))
            .ToList());
    }

    /// <summary>Removes a Google or Apple login, as long as the person can still sign in some other way.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> RemoveLoginAsync(
        string provider, System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var logins = await db.UserLogins.Where(l => l.UserId == userId).ToListAsync(cancellationToken);
        var login = logins.FirstOrDefault(l => l.LoginProvider == provider);
        if (login is null)
        {
            return TypedResults.NotFound();
        }

        var hasPassword = await db.Users.AnyAsync(u => u.Id == userId && u.PasswordHash != null, cancellationToken);
        var hasPasskey = await db.UserPasskeys.AnyAsync(p => p.UserId == userId, cancellationToken);
        if (!hasPassword && !hasPasskey && logins.Count == 1)
        {
            return Problems.Validation("provider", $"This is the only way you sign in. Set a password first, so you can still sign in without {login.ProviderDisplayName ?? provider}.");
        }

        db.UserLogins.Remove(login);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<(ExternalSignInCode? Code, ProblemHttpResult? Problem)> RedeemAsync(
        AppDbContext db, TimeProvider clock, string token, string bindingHash, CancellationToken cancellationToken)
    {
        var hash = TokenHash(token);
        var code = await db.ExternalSignInCodes.AsNoTracking().SingleOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);
        // Only one request can use a code, even two at the same moment.
        var consumed = await db.ExternalSignInCodes.Where(c => c.TokenHash == hash).ExecuteDeleteAsync(cancellationToken);
        if (consumed != 1 || code is null || code.ExpiresAt <= clock.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code.BindingHash), Encoding.ASCII.GetBytes(bindingHash)))
        {
            return (null, Expired());
        }

        if (code.Error is not null || code.UserId is null)
        {
            return (null, TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: code.Error ?? ExpiredSignIn));
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == code.UserId, cancellationToken);
        return user is { DeletedAt: null } ? (code, null) : (null, Expired());
    }

    private static async Task<string> StoreAsync(AppDbContext db, TimeProvider clock, ExternalSignInState state, ExternalSignInOutcome outcome, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.ExternalSignInCodes.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var token = RandomToken();
        db.ExternalSignInCodes.Add(new ExternalSignInCode
        {
            TokenHash = TokenHash(token),
            BindingHash = state.BindingHash,
            UserId = outcome.User?.Id,
            Error = outcome.Error is { Length: > ExternalSignInCode.ErrorMaxLength } error ? error[..ExternalSignInCode.ErrorMaxLength] : outcome.Error,
            NewAccount = outcome.NewAccount,
            ReturnPath = state.ReturnPath,
            ExpiresAt = now + CodeLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    private static string ClientRedirect(AppOptions app, ExternalSignInState state, string key, string value) =>
        QueryHelpers.AddQueryString(state.AppRedirectUri ?? app.Link(WebReturnPath), key, value);

    private static string CallbackUri(AppOptions app, IExternalSignInProvider provider) =>
        app.Link($"/api/auth/external/{provider.Name}/callback");

    private static IExternalSignInProvider? Find(IEnumerable<IExternalSignInProvider> providers, string name) =>
        providers.FirstOrDefault(p => p.IsConfigured && string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>The apps' own scheme, or a loopback address on any port for the Windows app (RFC 8252).</summary>
    internal static bool IsAppRedirect(string value) =>
        value == AppRedirectUri
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "[::1]"
            && uri.AbsolutePath == "/auth/callback" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo));

    /// <summary>A path on this site, never another site: <c>//evil.example</c> and <c>/\evil.example</c> are refused.</summary>
    internal static string? SafeReturnPath(string? path) =>
        path is { Length: > 0 and <= ExternalSignInCode.ReturnPathMaxLength } && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal)
        && !path.Any(char.IsControl) ? path : null;

    private static ExternalSignInState? Unprotect(IDataProtectionProvider protection, string? value)
    {
        if (value is not { Length: > 0 and <= 4096 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExternalSignInState>(Protector(protection).Unprotect(value));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector("Spindle.ExternalSignIn.State").ToTimeLimitedDataProtector();

    private static ProblemHttpResult Expired() => TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: ExpiredSignIn);

    private static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 of PKCE: base64url of the SHA-256.</summary>
    internal static string Hash(string value) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool IsBase64Url(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static CookieOptions BindingCookieOptions(HttpContext http, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        // Strict is enough: the cookie is read by the web app's own request after it is back from the provider.
        SameSite = SameSiteMode.Strict,
        Path = BindingCookiePath,
        MaxAge = lifetime,
        IsEssential = true,
    };
}

/// <summary>What the server remembers about a sign-in while the person is at Google or Apple, sealed in the state.</summary>
internal sealed record ExternalSignInState(
    string Provider,
    string Nonce,
    string CodeVerifier,
    string BindingHash,
    string? AppRedirectUri,
    string? ReturnPath,
    string? InvitationToken,
    string? TimeZone,
    bool AcceptTerms);
