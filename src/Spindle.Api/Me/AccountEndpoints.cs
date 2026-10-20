using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Mcp;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Me;

public sealed record ChangePasswordRequest
{
    [Required]
    [StringLength(PasswordRules.MaxLength)]
    public string CurrentPassword { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)]
    public string NewPassword { get; init; } = string.Empty;
}

public sealed record DeleteAccountRequest
{
    /// <summary>The account's password. An account without one signs in again first instead.</summary>
    [StringLength(PasswordRules.MaxLength)]
    public string? Password { get; init; }
}

/// <summary>A signed-in device: one sign-in, kept alive by its refresh tokens.</summary>
/// <param name="IsCurrent">Whether it is the device this request came from.</param>
/// <param name="Agent">For an agent connected over MCP, what it may reach.</param>
public sealed record SessionResponse(Guid Id, string? DeviceName, DateTimeOffset SignedInAt, DateTimeOffset LastActiveAt, bool IsCurrent, AgentAccessResponse? Agent = null);

/// <summary>What an agent connected over MCP may reach: one workspace, to read or also to change things.</summary>
public sealed record AgentAccessResponse(Guid WorkspaceId, string WorkspaceName, bool CanWrite);

/// <summary>The person's own account: password, signed-in devices, their data, and deleting it all.</summary>
internal static class AccountEndpoints
{
    private const string WrongPassword = "That is not your current password.";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/me").WithTags("Me").RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/password", ChangePasswordAsync).WithName("changePassword");
        group.MapPost("/confirmation-email", SendConfirmationAsync)
            .WithName("sendConfirmationEmail")
            .RequireRateLimiting(PasswordEndpoints.EmailRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/sessions", ListSessionsAsync).WithName("listSessions");
        group.MapDelete("/sessions/{sessionId:guid}", RevokeSessionAsync).WithName("revokeSession");
        group.MapPost("/sessions/revoke-others", RevokeOtherSessionsAsync).WithName("revokeOtherSessions");
        group.MapGet("/export", ExportAsync).WithName("exportMyData").Produces<byte[]>(contentType: "application/json");
        // A POST with the password, not a DELETE: some proxies drop the body of a DELETE.
        group.MapPost("/delete", DeleteAccountAsync).WithName("deleteAccount");

        return routes;
    }

    /// <summary>Changes the password and signs every other device out, in case someone else knew the old one.</summary>
    private static async Task<Results<NoContent, UnauthorizedHttpResult, ValidationProblem>> ChangePasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        UserManager<AppUser> users,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await MeEndpoints.FindAsync(db, principal, cancellationToken, tracking: true);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.PasswordHash is null)
        {
            return Problems.Validation("currentPassword", "Your account has no password yet. Ask for a password reset email to set one.");
        }

        if (await PasswordProblemAsync(users, user, request.CurrentPassword, "currentPassword") is { } problem)
        {
            return problem;
        }

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            return Problems.Validation("newPassword", result.Errors.First().Description);
        }

        await RevokeSessionsAsync(db, user.Id, keep: principal.GetSessionId(), timeProvider.GetUtcNow(), cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Emails a new link to confirm the address, unless it is confirmed already. Says so when the mail server can't
    /// take the email, instead of claiming it was sent.
    /// </summary>
    private static async Task<Results<Accepted, NoContent, UnauthorizedHttpResult, ProblemHttpResult>> SendConfirmationAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        UserManager<AppUser> users,
        AccountEmails emails,
        CancellationToken cancellationToken)
    {
        var user = await MeEndpoints.FindAsync(db, principal, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.EmailConfirmed)
        {
            return TypedResults.NoContent();
        }

        if (!await emails.SendConfirmationAsync((await users.FindByIdAsync(user.Id.ToString()))!, cancellationToken))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The email couldn't be sent. Try again in a few minutes.");
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Ok<List<SessionResponse>>> ListSessionsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var current = principal.GetSessionId();
        var now = timeProvider.GetUtcNow();

        var sessions = await db.RefreshTokens
            .Where(t => t.UserId == userId)
            .GroupBy(t => t.FamilyId)
            .Where(g => g.Any(t => t.RevokedAt == null && t.ExpiresAt > now))
            .Select(g => new
            {
                Id = g.Key,
                DeviceName = g.Max(t => t.DeviceName),
                SignedInAt = g.Min(t => t.CreatedAt),
                LastActiveAt = g.Max(t => t.CreatedAt),
            })
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync(cancellationToken);

        var ids = sessions.Select(s => s.Id).ToList();
        var agents = await (
            from connection in db.AgentConnections
            join workspace in db.Workspaces on connection.WorkspaceId equals workspace.Id
            where connection.UserId == userId && ids.Contains(connection.SessionId)
            select new { connection.SessionId, connection.WorkspaceId, workspace.Name, connection.Scope })
            .ToDictionaryAsync(a => a.SessionId, a => new AgentAccessResponse(a.WorkspaceId, a.Name, McpOAuth.CanWrite(a.Scope)), cancellationToken);

        return TypedResults.Ok(sessions
            .Select(s => new SessionResponse(s.Id, s.DeviceName, s.SignedInAt, s.LastActiveAt, s.Id == current, agents.GetValueOrDefault(s.Id)))
            .OrderByDescending(s => s.IsCurrent)
            .ToList());
    }

    /// <summary>Signs a device out. Its access token lapses within minutes; it cannot renew it.</summary>
    private static async Task<Results<NoContent, NotFound>> RevokeSessionAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var revoked = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.FamilyId == sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, timeProvider.GetUtcNow()), cancellationToken);
        return revoked > 0 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<NoContent> RevokeOtherSessionsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await RevokeSessionsAsync(db, principal.GetUserId(), keep: principal.GetSessionId(), timeProvider.GetUtcNow(), cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Everything the person tracked, in every workspace, as JSON: their copy of their data.</summary>
    private static async Task<Results<FileContentHttpResult, UnauthorizedHttpResult>> ExportAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await MeEndpoints.FindAsync(db, principal, cancellationToken);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        var export = await DataExport.CreateAsync(db, user, now, cancellationToken);
        var json = JsonSerializer.SerializeToUtf8Bytes(export, DataExport.JsonOptions);
        return TypedResults.File(json, "application/json", $"spindle-data-{now:yyyy-MM-dd}.json");
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult, ValidationProblem>> DeleteAccountAsync(
        DeleteAccountRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        UserManager<AppUser> users,
        AccountDeletion deletion,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await MeEndpoints.FindAsync(db, principal, cancellationToken, tracking: true);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        if (user.PasswordHash is null)
        {
            if (!await RecentSignIn.IsRecentAsync(db, principal, timeProvider.GetUtcNow(), cancellationToken))
            {
                return Problems.Validation("password", RecentSignIn.Required);
            }
        }
        else if (await PasswordProblemAsync(users, user, request.Password ?? string.Empty, "password") is { } problem)
        {
            return problem;
        }

        if (await deletion.BlockerAsync(user.Id, cancellationToken) is { } blocker)
        {
            return Problems.Validation("account", blocker);
        }

        await deletion.DeleteAsync(user.Id, cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Checks the password someone typed to confirm who they are. Wrong guesses count towards the same lockout as
    /// signing in, so a stolen session cannot be used to try passwords.
    /// </summary>
    private static async Task<ValidationProblem?> PasswordProblemAsync(UserManager<AppUser> users, AppUser user, string password, string field)
    {
        if (await users.IsLockedOutAsync(user))
        {
            return Problems.Validation(field, "Too many wrong passwords. Try again in 15 minutes.");
        }

        if (await users.CheckPasswordAsync(user, password))
        {
            await users.ResetAccessFailedCountAsync(user);
            return null;
        }

        await users.AccessFailedAsync(user);
        return Problems.Validation(field, WrongPassword);
    }

    private static Task RevokeSessionsAsync(AppDbContext db, Guid userId, Guid? keep, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && (keep == null || t.FamilyId != keep))
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);
}
