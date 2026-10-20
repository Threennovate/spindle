using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Spindle.Api.Auth;

internal static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("The authenticated principal has no 'sub' claim.");

        return Guid.Parse(subject);
    }

    /// <summary>The device session the request comes from, if its access token names one.</summary>
    public static Guid? GetSessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(AccessTokenIssuer.SessionClaim), out var sessionId) ? sessionId : null;
}
