using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

internal sealed class PasskeyChallenges(AppDbContext db, TimeProvider clock)
{
    internal const string CookieName = "tt_passkey";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task StoreAsync(HttpContext http, string operation, string state, AppUser? user, CancellationToken ct)
    {
        var token = await StoreNativeAsync(operation, state, user, ct);
        var expires = clock.GetUtcNow() + Lifetime;
        if (http.Request.Cookies[CookieName] is { } previous)
        {
            var previousHash = Hash(previous);
            await db.PasskeyChallenges.Where(c => c.TokenHash == previousHash).ExecuteDeleteAsync(ct);
        }
        http.Response.Cookies.Append(CookieName, token, CookieOptions(http, expires));
        http.Response.Headers.CacheControl = "no-store";
    }

    public async Task<string> StoreNativeAsync(string operation, string state, AppUser? user, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.PasskeyChallenges.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expires = now + Lifetime;
        db.PasskeyChallenges.Add(new PasskeyChallenge
        {
            TokenHash = Hash(token), Operation = operation, State = state,
            UserId = user?.Id, SecurityStamp = user?.SecurityStamp, ExpiresAt = expires,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<PasskeyChallenge?> ConsumeAsync(HttpContext http, string operation, Guid? userId, CancellationToken ct)
    {
        http.Response.Cookies.Delete(CookieName, CookieOptions(http, null));
        if (http.Request.Cookies[CookieName] is not { Length: > 0 and <= 100 } token)
        {
            return null;
        }

        return await ConsumeNativeAsync(token, operation, userId, ct);
    }

    public async Task<PasskeyChallenge?> ConsumeNativeAsync(string token, string operation, Guid? userId, CancellationToken ct)
    {
        var hash = Hash(token);
        var challenge = await db.PasskeyChallenges.AsNoTracking().SingleOrDefaultAsync(c => c.TokenHash == hash, ct);
        // Only one concurrent request can consume this state, including replays with a copied cookie.
        var consumed = await db.PasskeyChallenges.Where(c => c.TokenHash == hash).ExecuteDeleteAsync(ct);
        return consumed == 1 && challenge is not null && challenge.ExpiresAt > clock.GetUtcNow()
            && challenge.Operation == operation && challenge.UserId == userId ? challenge : null;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static CookieOptions CookieOptions(HttpContext http, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps || !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
        SameSite = SameSiteMode.Strict, Path = "/api", Expires = expires, IsEssential = true,
    };
}
