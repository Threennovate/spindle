using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.Team;
using Spindle.Api.Workspaces;
using Spindle.Domain.Invitations;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

/// <summary>A signed-in device: a short-lived access token plus the refresh token that renews it.</summary>
internal sealed record AuthSession(AccessToken AccessToken, IssuedRefreshToken RefreshToken);

internal sealed record RegisterResult(AuthSession? Session, Dictionary<string, string[]> Errors);

/// <summary>Who a new account is for. It has a password, a Google or Apple login, or both.</summary>
internal sealed record NewAccount(string Email, string FullName, string TimeZone, string? InvitationToken, bool AcceptTerms)
{
    public string? Password { get; init; }

    public UserLoginInfo? Login { get; init; }

    /// <summary>That the address is known to be theirs already, e.g. because Google runs its mailbox.</summary>
    public bool EmailConfirmed { get; init; }
}

internal enum LoginStatus
{
    Succeeded,
    InvalidCredentials,
    LockedOut,
}

internal sealed record LoginResult(LoginStatus Status, AuthSession? Session);

/// <summary>
/// Account and session logic shared by the app endpoints (tokens in the body) and the web endpoints
/// (refresh token in an HttpOnly cookie).
/// </summary>
internal sealed class AuthService(
    AppDbContext db,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    AccessTokenIssuer accessTokens,
    RefreshTokenService refreshTokens,
    InvitationService invitations,
    NewWorkspaces newWorkspaces,
    AccountEmails emails,
    IOptions<AppOptions> app,
    IOptions<AuthOptions> auth,
    TimeProvider timeProvider)
{
    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var (user, errors) = await CreateAccountAsync(new NewAccount(
            request.Email.Trim(), request.FullName.Trim(), request.TimeZone, request.InvitationToken, request.AcceptTerms)
        {
            Password = request.Password,
        }, cancellationToken);
        if (user is null)
        {
            return new RegisterResult(null, errors);
        }

        if (!user.EmailConfirmed)
        {
            await emails.SendConfirmationAsync(user, cancellationToken);
        }

        return new RegisterResult(await StartSessionAsync(user.Id, request.DeviceName, cancellationToken), []);
    }

    /// <summary>
    /// Creates an account with its own organization, workspace and trial, or in the workspace of an invitation to its
    /// address. With a password, or with a Google or Apple login and no password.
    /// </summary>
    internal async Task<(AppUser? User, Dictionary<string, string[]> Errors)> CreateAccountAsync(NewAccount account, CancellationToken cancellationToken)
    {
        var email = account.Email;
        var fullName = account.FullName;
        if (!auth.Value.AllowRegistration && string.IsNullOrEmpty(account.InvitationToken))
        {
            return (null, new() { ["invitationToken"] = ["This server takes new people only by invitation. Ask someone on it to invite you."] });
        }

        var terms = app.Value;
        if (terms.HasTerms && !account.AcceptTerms)
        {
            return (null, new() { ["acceptTerms"] = ["Agree to the terms of service and privacy policy to create an account."] });
        }

        // The user and their organization are created atomically. The execution strategy retries the
        // whole transaction on transient SQL errors, so each attempt starts from a clean change tracker.
        var strategy = db.Database.CreateExecutionStrategy();
        var (user, errors) = await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            Invitation? invitation = null;
            if (account.InvitationToken is { Length: > 0 } token)
            {
                invitation = await invitations.FindAsync(token, ct);
                if (invitation is null || invitation.StatusAt(timeProvider.GetUtcNow()) != InvitationStatus.Pending)
                {
                    return ((AppUser?)null, [InvitationError("invitationToken", "This invitation has expired, was withdrawn or was already used.")]);
                }
                if (!string.Equals(invitation.Email, email, StringComparison.OrdinalIgnoreCase))
                {
                    return ((AppUser?)null, [InvitationError("email", $"The invitation is for {invitation.Email}.")]);
                }
            }

            var newUser = new AppUser
            {
                UserName = email,
                Email = email,
                FullName = fullName,
                TimeZone = account.TimeZone,
                CreatedAt = timeProvider.GetUtcNow(),
                TermsAcceptedAt = terms.HasTerms ? timeProvider.GetUtcNow() : null,
                TermsVersion = terms.HasTerms ? terms.TermsVersion : null,
                // An invitation came to this address, so it is theirs already.
                EmailConfirmed = invitation is not null || account.EmailConfirmed,
            };

            var result = account.Password is { } password
                ? await userManager.CreateAsync(newUser, password)
                : await userManager.CreateAsync(newUser);
            if (result.Succeeded && account.Login is { } login)
            {
                result = await userManager.AddLoginAsync(newUser, login);
            }
            if (!result.Succeeded)
            {
                return ((AppUser?)null, result.Errors);
            }

            if (invitation is not null)
            {
                await invitations.AcceptAsync(invitation, newUser, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (newUser, []);
            }

            // Like Toggl, every new account starts with its own organization and workspace, and the organization with
            // its trial.
            newWorkspaces.StartOwnOrganization(newUser, fullName);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return (newUser, []);
        }, cancellationToken);

        return user is null ? (null, ToValidationErrors(errors)) : (user, []);
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            return new LoginResult(LoginStatus.InvalidCredentials, null);
        }

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return new LoginResult(LoginStatus.LockedOut, null);
        }

        if (!result.Succeeded)
        {
            return new LoginResult(LoginStatus.InvalidCredentials, null);
        }

        return new LoginResult(LoginStatus.Succeeded, await StartSessionAsync(user.Id, request.DeviceName, cancellationToken));
    }

    /// <summary>Renews a session, or returns <see langword="null"/> if the refresh token must not be honoured.</summary>
    public async Task<AuthSession?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // An agent's session renews only its MCP tokens, at the OAuth token endpoint, never full access to the API.
        if (await refreshTokens.SessionOfAsync(refreshToken, cancellationToken) is { } session
            && await db.AgentConnections.AnyAsync(c => c.SessionId == session.FamilyId, cancellationToken))
        {
            return null;
        }

        var rotated = await refreshTokens.RotateAsync(refreshToken, cancellationToken);
        return rotated is null ? null : new AuthSession(accessTokens.Issue(rotated.UserId, rotated.FamilyId), rotated);
    }

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken) =>
        refreshTokens.RevokeSessionAsync(refreshToken, cancellationToken);

    internal async Task<AuthSession> StartSessionAsync(Guid userId, string? deviceName, CancellationToken cancellationToken)
    {
        var refreshToken = await refreshTokens.IssueAsync(userId, deviceName, cancellationToken);
        return new AuthSession(accessTokens.Issue(userId, refreshToken.FamilyId), refreshToken);
    }

    /// <summary>An invitation problem, carried like Identity's errors; the code names the field.</summary>
    private static IdentityError InvitationError(string field, string description) => new() { Code = $"Invitation:{field}", Description = description };

    private static Dictionary<string, string[]> ToValidationErrors(IEnumerable<IdentityError> errors) =>
        errors
            // The user name is the email address, so user name errors would only repeat the email ones.
            .Where(e => !e.Code.Contains("UserName", StringComparison.Ordinal))
            .GroupBy(e => e.Code switch
            {
                _ when e.Code.StartsWith("Invitation:", StringComparison.Ordinal) => e.Code["Invitation:".Length..],
                _ when e.Code.StartsWith("Password", StringComparison.Ordinal) => "password",
                _ when e.Code.Contains("Email", StringComparison.Ordinal) => "email",
                _ => string.Empty,
            })
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
