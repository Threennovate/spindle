using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

public sealed record ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;
}

public sealed record ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The token from the link in the reset email.</summary>
    [Required]
    [StringLength(2000)]
    public string Token { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)]
    public string NewPassword { get; init; } = string.Empty;
}

public sealed record ConfirmEmailRequest
{
    [Required]
    public Guid? UserId { get; init; }

    /// <summary>The token from the link in the confirmation email.</summary>
    [Required]
    [StringLength(2000)]
    public string Token { get; init; } = string.Empty;
}

/// <summary>Getting back into an account with a forgotten password, and confirming an email address.</summary>
internal static class PasswordEndpoints
{
    public const string EmailRateLimit = "account-email";

    private const string ExpiredLink = "This link has expired or was already used. Ask for a new one.";

    public static IEndpointRouteBuilder MapPasswordEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/forgot-password", ForgotPasswordAsync)
            .WithName("forgotPassword")
            .RequireRateLimiting(EmailRateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        group.MapPost("/reset-password", ResetPasswordAsync).WithName("resetPassword");
        group.MapPost("/confirm-email", ConfirmEmailAsync).WithName("confirmEmail");

        return routes;
    }

    /// <summary>
    /// Emails a link to choose a new password. The answer is the same whether or not an account uses the address,
    /// so nobody can find out who has one.
    /// </summary>
    private static async Task<Accepted> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        UserManager<AppUser> users,
        AccountEmails emails,
        CancellationToken cancellationToken)
    {
        if (await users.FindByEmailAsync(request.Email.Trim()) is { DeletedAt: null } user)
        {
            await emails.SendPasswordResetAsync(user, cancellationToken);
        }

        return TypedResults.Accepted((string?)null);
    }

    /// <summary>Sets a new password from a reset link, and signs out every device that used the old one.</summary>
    private static async Task<Results<NoContent, ValidationProblem>> ResetPasswordAsync(
        ResetPasswordRequest request,
        UserManager<AppUser> users,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        var token = AccountEmails.Decode(request.Token);
        if (user is not { DeletedAt: null } || token is null)
        {
            return Problems.Validation("token", ExpiredLink);
        }

        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? Problems.Validation("token", ExpiredLink)
                : Problems.Validation("newPassword", result.Errors.First().Description);
        }

        // Until now nobody had shown the address is theirs, so a Google or Apple login or a passkey added before may
        // be someone else's; the person with the inbox keeps the account.
        if (!user.EmailConfirmed)
        {
            await db.UserLogins.Where(l => l.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            await db.UserPasskeys.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        }

        // The link came to the inbox, so the address is theirs; and a locked account opens again.
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, timeProvider.GetUtcNow()), cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ValidationProblem>> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        UserManager<AppUser> users)
    {
        var user = await users.FindByIdAsync(request.UserId!.Value.ToString());
        var token = AccountEmails.Decode(request.Token);
        if (user is not { DeletedAt: null } || token is null)
        {
            return Problems.Validation("token", ExpiredLink);
        }

        if (user.EmailConfirmed)
        {
            return TypedResults.NoContent();
        }

        var result = await users.ConfirmEmailAsync(user, token);
        return result.Succeeded ? TypedResults.NoContent() : Problems.Validation("token", ExpiredLink);
    }
}
