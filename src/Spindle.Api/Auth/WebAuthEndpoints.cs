using Microsoft.AspNetCore.Http.HttpResults;

namespace Spindle.Api.Auth;

public sealed record AccessTokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt);

/// <summary>
/// Sign-in for the web app. The refresh token lives in an HttpOnly cookie that scripts cannot read, so an XSS
/// bug cannot steal a long-lived session; only the short-lived access token reaches JavaScript, which keeps it in
/// memory. After a reload the page calls <c>refresh</c> to get a new access token from the cookie.
/// </summary>
/// <remarks>
/// The cookie is <c>SameSite=Strict</c> and only sent to these endpoints, so other sites cannot trigger a
/// refresh or sign-out, and login needs a JSON body, which a cross-site form cannot send.
/// </remarks>
internal static class WebAuthEndpoints
{
    public const string RefreshCookieName = "tt_refresh";
    public const string RefreshCookiePath = "/api/auth/web";

    public static IEndpointRouteBuilder MapWebAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth/web").WithTags("Web auth").AllowAnonymous();

        group.MapPost("/register", RegisterAsync).WithName("webRegister");
        group.MapPost("/login", LoginAsync)
            .WithName("webLogin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/refresh", RefreshAsync)
            .WithName("webRefresh")
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        group.MapPost("/logout", LogoutAsync).WithName("webLogout");

        return routes;
    }

    private static async Task<Results<Ok<AccessTokenResponse>, ValidationProblem>> RegisterAsync(
        RegisterRequest request,
        AuthService auth,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request with { DeviceName = request.DeviceName ?? DeviceNames.FromUserAgent(http.Request.Headers.UserAgent) }, cancellationToken);
        return result.Session is { } session
            ? TypedResults.Ok(StartCookieSession(http, session))
            : TypedResults.ValidationProblem(result.Errors);
    }

    private static async Task<Results<Ok<AccessTokenResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AuthService auth,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request with { DeviceName = request.DeviceName ?? DeviceNames.FromUserAgent(http.Request.Headers.UserAgent) }, cancellationToken);
        return result.Session is { } session
            ? TypedResults.Ok(StartCookieSession(http, session))
            : AuthProblems.ForFailedLogin(result.Status);
    }

    private static async Task<Results<Ok<AccessTokenResponse>, ProblemHttpResult>> RefreshAsync(
        AuthService auth,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var refreshToken = http.Request.Cookies[RefreshCookieName];
        var session = string.IsNullOrEmpty(refreshToken) ? null : await auth.RefreshAsync(refreshToken, cancellationToken);
        if (session is null)
        {
            DeleteRefreshCookie(http);
            return AuthProblems.InvalidRefreshToken();
        }

        return TypedResults.Ok(StartCookieSession(http, session));
    }

    private static async Task<NoContent> LogoutAsync(
        AuthService auth,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        if (http.Request.Cookies[RefreshCookieName] is { Length: > 0 } refreshToken)
        {
            await auth.LogoutAsync(refreshToken, cancellationToken);
        }

        DeleteRefreshCookie(http);
        return TypedResults.NoContent();
    }

    internal static AccessTokenResponse StartCookieSession(HttpContext http, AuthSession session)
    {
        http.Response.Cookies.Append(
            RefreshCookieName,
            session.RefreshToken.Value,
            CookieOptions(http, session.RefreshToken.ExpiresAt));

        return new AccessTokenResponse(session.AccessToken.Value, session.AccessToken.ExpiresAt);
    }

    private static void DeleteRefreshCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookieName, CookieOptions(http, expires: null));

    private static CookieOptions CookieOptions(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        // Always Secure outside development, even if a proxy in front of the app terminates TLS.
        Secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
