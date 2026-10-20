using Microsoft.AspNetCore.Http.HttpResults;

namespace Spindle.Api.Auth;

/// <summary>
/// Sign-in for the iOS and macOS apps: both tokens travel in the body and the app keeps the refresh token
/// in the Keychain. The browser uses <see cref="WebAuthEndpoints"/> instead.
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/register", RegisterAsync).WithName("register");
        group.MapPost("/login", LoginAsync)
            .WithName("login")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/refresh", RefreshAsync)
            .WithName("refreshTokens")
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        group.MapPost("/logout", LogoutAsync).WithName("logout");

        return routes;
    }

    private static async Task<Results<Ok<AuthTokensResponse>, ValidationProblem>> RegisterAsync(
        RegisterRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request, cancellationToken);
        return result.Session is { } session
            ? TypedResults.Ok(ToResponse(session))
            : TypedResults.ValidationProblem(result.Errors);
    }

    private static async Task<Results<Ok<AuthTokensResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request, cancellationToken);
        return result.Session is { } session
            ? TypedResults.Ok(ToResponse(session))
            : AuthProblems.ForFailedLogin(result.Status);
    }

    private static async Task<Results<Ok<AuthTokensResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var session = await auth.RefreshAsync(request.RefreshToken, cancellationToken);
        return session is null
            ? AuthProblems.InvalidRefreshToken()
            : TypedResults.Ok(ToResponse(session));
    }

    private static async Task<NoContent> LogoutAsync(
        LogoutRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        await auth.LogoutAsync(request.RefreshToken, cancellationToken);
        return TypedResults.NoContent();
    }

    private static AuthTokensResponse ToResponse(AuthSession session) => new(
        session.AccessToken.Value,
        session.AccessToken.ExpiresAt,
        session.RefreshToken.Value,
        session.RefreshToken.ExpiresAt);
}

internal static class AuthProblems
{
    public static ProblemHttpResult ForFailedLogin(LoginStatus status) => status == LoginStatus.LockedOut
        ? TypedResults.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many failed sign-in attempts.",
            detail: "The account is temporarily locked. Try again later.")
        : TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.");

    public static ProblemHttpResult InvalidRefreshToken() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid refresh token.");
}
