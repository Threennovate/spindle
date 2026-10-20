using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Spindle.Api.Common;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

public sealed record PasskeyOptionsResponse(string OptionsJson);
public sealed record NativePasskeyOptionsResponse(string OptionsJson, string ChallengeToken);
public sealed record PasskeyResponse(string Id, string Name, DateTimeOffset CreatedAt);

public sealed record PasskeyCreationRequest
{
    /// <summary>The account's password. An account without one signs in again first instead.</summary>
    [StringLength(PasswordRules.MaxLength)]
    public string? Password { get; init; }
}

public sealed record SavePasskeyRequest
{
    [Required, StringLength(65536)]
    public string CredentialJson { get; init; } = string.Empty;
    [Required, StringLength(100)]
    public string Name { get; init; } = string.Empty;
}

public sealed record PasskeyLoginRequest
{
    [Required, StringLength(65536)]
    public string CredentialJson { get; init; } = string.Empty;
    [StringLength(RefreshToken.DeviceNameMaxLength)]
    public string? DeviceName { get; init; }
}

public sealed record NativePasskeyLoginRequest
{
    [Required, StringLength(100)]
    public string ChallengeToken { get; init; } = string.Empty;
    [Required, StringLength(65536)]
    public string CredentialJson { get; init; } = string.Empty;
    [StringLength(RefreshToken.DeviceNameMaxLength)]
    public string? DeviceName { get; init; }
}

internal static class PasskeyEndpoints
{
    internal const string RateLimit = "passkeys";
    private const string InvalidCredential = "The passkey could not be verified. Please try again.";

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var login = routes.MapGroup("/auth/web/passkeys").WithTags("Web auth").AllowAnonymous()
            .RequireRateLimiting(RateLimit).ProducesProblem(StatusCodes.Status429TooManyRequests);
        login.MapPost("/options", LoginOptionsAsync).WithName("webPasskeyOptions");
        login.MapPost("/login", LoginAsync).WithName("webPasskeyLogin").ProducesProblem(StatusCodes.Status401Unauthorized);

        var native = routes.MapGroup("/auth/passkeys").WithTags("Auth").AllowAnonymous()
            .RequireRateLimiting(RateLimit).ProducesProblem(StatusCodes.Status429TooManyRequests);
        native.MapPost("/options", NativeOptionsAsync).WithName("nativePasskeyOptions");
        native.MapPost("/login", NativeLoginAsync).WithName("nativePasskeyLogin").ProducesProblem(StatusCodes.Status401Unauthorized);

        var manage = routes.MapGroup("/me/passkeys").WithTags("Me").RequireAuthorization()
            .RequireRateLimiting(RateLimit).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        manage.MapGet("/", ListAsync).WithName("listPasskeys");
        manage.MapPost("/options", CreationOptionsAsync).WithName("passkeyCreationOptions");
        manage.MapPost("/", SaveAsync).WithName("savePasskey");
        manage.MapDelete("/{credentialId}", RemoveAsync).WithName("removePasskey");
        return routes;
    }

    private static async Task<Ok<NativePasskeyOptionsResponse>> NativeOptionsAsync(
        IPasskeyHandler<AppUser> handler, PasskeyChallenges challenges, HttpContext http, CancellationToken ct)
    {
        var result = await handler.MakeRequestOptionsAsync(null, http);
        var token = await challenges.StoreNativeAsync("native-assertion", result.AssertionState!, null, ct);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new NativePasskeyOptionsResponse(result.RequestOptionsJson, token));
    }

    private static async Task<Results<Ok<AuthTokensResponse>, ProblemHttpResult>> NativeLoginAsync(
        NativePasskeyLoginRequest request, IPasskeyHandler<AppUser> handler, PasskeyChallenges challenges,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, AuthService auth, HttpContext http, CancellationToken ct)
    {
        var state = await challenges.ConsumeNativeAsync(request.ChallengeToken, "native-assertion", null, ct);
        var result = state is null ? new LoginResult(LoginStatus.InvalidCredentials, null)
            : await VerifyAsync(request.CredentialJson, state.State, request.DeviceName, handler, users, signIn, auth, http, ct);
        if (result.Session is not { } session)
            return result.Status == LoginStatus.LockedOut ? AuthProblems.ForFailedLogin(result.Status) : FailedLogin();
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new AuthTokensResponse(session.AccessToken.Value, session.AccessToken.ExpiresAt,
            session.RefreshToken.Value, session.RefreshToken.ExpiresAt));
    }

    private static async Task<Ok<PasskeyOptionsResponse>> LoginOptionsAsync(
        IPasskeyHandler<AppUser> handler, PasskeyChallenges challenges, HttpContext http, CancellationToken ct)
    {
        // Discoverable credentials let the authenticator choose the account without disclosing email addresses.
        var result = await handler.MakeRequestOptionsAsync(null, http);
        await challenges.StoreAsync(http, "assertion", result.AssertionState!, null, ct);
        return TypedResults.Ok(new PasskeyOptionsResponse(result.RequestOptionsJson));
    }

    private static async Task<Results<Ok<AccessTokenResponse>, ProblemHttpResult>> LoginAsync(
        PasskeyLoginRequest request, IPasskeyHandler<AppUser> handler, PasskeyChallenges challenges,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, AuthService auth, HttpContext http, CancellationToken ct)
    {
        var state = await challenges.ConsumeAsync(http, "assertion", null, ct);
        if (state is null)
        {
            return FailedLogin();
        }

        var verified = await VerifyAsync(request.CredentialJson, state.State, request.DeviceName, handler, users, signIn, auth, http, ct);
        return verified.Session is { } session ? TypedResults.Ok(WebAuthEndpoints.StartCookieSession(http, session))
            : verified.Status == LoginStatus.LockedOut ? AuthProblems.ForFailedLogin(verified.Status) : FailedLogin();
    }

    private static async Task<LoginResult> VerifyAsync(string credentialJson, string state, string? deviceName,
        IPasskeyHandler<AppUser> handler, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        AuthService auth, HttpContext http, CancellationToken ct)
    {
        var result = await handler.PerformAssertionAsync(new()
        {
            CredentialJson = credentialJson, AssertionState = state, HttpContext = http,
        });
        if (!result.Succeeded || result.User.DeletedAt is not null || !await signIn.CanSignInAsync(result.User))
        {
            return new LoginResult(LoginStatus.InvalidCredentials, null);
        }
        if (await users.IsLockedOutAsync(result.User))
        {
            return new LoginResult(LoginStatus.LockedOut, null);
        }
        if (!(await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey)).Succeeded
            || !(await users.ResetAccessFailedCountAsync(result.User)).Succeeded)
        {
            return new LoginResult(LoginStatus.InvalidCredentials, null);
        }

        var session = await auth.StartSessionAsync(result.User.Id,
            deviceName ?? DeviceNames.FromUserAgent(http.Request.Headers.UserAgent), ct);
        return new LoginResult(LoginStatus.Succeeded, session);
    }

    private static async Task<Results<Ok<PasskeyResponse[]>, UnauthorizedHttpResult>> ListAsync(UserManager<AppUser> users, HttpContext http)
    {
        var user = await FindUserAsync(users, http);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok((await users.GetPasskeysAsync(user))
            .OrderBy(p => p.CreatedAt).Select(ToResponse).ToArray());
    }

    private static async Task<Results<Ok<PasskeyOptionsResponse>, UnauthorizedHttpResult, ValidationProblem>> CreationOptionsAsync(
        PasskeyCreationRequest request, UserManager<AppUser> users, SignInManager<AppUser> signIn,
        IPasskeyHandler<AppUser> handler, PasskeyChallenges challenges, AppDbContext db, TimeProvider clock,
        HttpContext http, CancellationToken ct)
    {
        var user = await FindUserAsync(users, http);
        if (user is null) return TypedResults.Unauthorized();
        if (user.PasswordHash is null)
        {
            if (!await RecentSignIn.IsRecentAsync(db, http.User, clock.GetUtcNow(), ct))
                return Problems.Validation("password", RecentSignIn.Required);
        }
        else if (!(await signIn.CheckPasswordSignInAsync(user, request.Password ?? string.Empty, lockoutOnFailure: true)).Succeeded)
            return Problems.Validation("password", "The password is incorrect or the account is temporarily locked.");
        if ((await users.GetPasskeysAsync(user)).Count >= 10)
            return Problems.Validation("name", "You can save up to 10 passkeys. Remove one before adding another.");

        var result = await handler.MakeCreationOptionsAsync(new()
        {
            Id = user.Id.ToString(), Name = user.Email!, DisplayName = user.FullName,
        }, http);
        await challenges.StoreAsync(http, "attestation", result.AttestationState!, user, ct);
        return TypedResults.Ok(new PasskeyOptionsResponse(result.CreationOptionsJson));
    }

    private static async Task<Results<Ok<PasskeyResponse>, UnauthorizedHttpResult, ValidationProblem>> SaveAsync(
        SavePasskeyRequest request, UserManager<AppUser> users, IPasskeyHandler<AppUser> handler,
        PasskeyChallenges challenges, HttpContext http, CancellationToken ct)
    {
        var user = await FindUserAsync(users, http);
        if (user is null) return TypedResults.Unauthorized();
        var state = await challenges.ConsumeAsync(http, "attestation", user.Id, ct);
        if (state is null || state.SecurityStamp != user.SecurityStamp)
            return Problems.Validation("credentialJson", InvalidCredential);

        var result = await handler.PerformAttestationAsync(new()
        {
            CredentialJson = request.CredentialJson, AttestationState = state.State, HttpContext = http,
        });
        if (!result.Succeeded || result.UserEntity.Id != user.Id.ToString()
            || await users.FindByPasskeyIdAsync(result.Passkey.CredentialId) is not null
            || (await users.GetPasskeysAsync(user)).Count >= 10)
            return Problems.Validation("credentialJson", InvalidCredential);

        result.Passkey.Name = request.Name.Trim();
        var saved = await users.AddOrUpdatePasskeyAsync(user, result.Passkey);
        return saved.Succeeded ? TypedResults.Ok(ToResponse(result.Passkey))
            : Problems.Validation("credentialJson", InvalidCredential);
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, NotFound, ValidationProblem>> RemoveAsync(
        string credentialId, UserManager<AppUser> users, HttpContext http)
    {
        var user = await FindUserAsync(users, http);
        if (user is null) return TypedResults.Unauthorized();
        byte[] id;
        try { id = WebEncoders.Base64UrlDecode(credentialId); }
        catch (FormatException) { return TypedResults.NotFound(); }
        if (await users.GetPasskeyAsync(user, id) is null) return TypedResults.NotFound();
        return (await users.RemovePasskeyAsync(user, id)).Succeeded ? TypedResults.NoContent()
            : Problems.Validation("credentialId", "Couldn't remove the passkey. Please try again.");
    }

    private static async Task<AppUser?> FindUserAsync(UserManager<AppUser> users, HttpContext http)
    {
        var user = await users.FindByIdAsync(http.User.GetUserId().ToString());
        return user?.DeletedAt is null ? user : null;
    }

    private static PasskeyResponse ToResponse(UserPasskeyInfo passkey) =>
        new(WebEncoders.Base64UrlEncode(passkey.CredentialId), passkey.Name ?? "Passkey", passkey.CreatedAt);

    private static ProblemHttpResult FailedLogin() => TypedResults.Problem(InvalidCredential, statusCode: StatusCodes.Status401Unauthorized);
}
