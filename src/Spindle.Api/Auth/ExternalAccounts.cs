using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

/// <summary>The account someone signed in to with Google or Apple, or why they couldn't.</summary>
internal sealed record ExternalSignInOutcome(AppUser? User, string? Error, bool NewAccount);

/// <summary>What the person asked for when they started, kept in the state that travels through the provider.</summary>
internal sealed record ExternalSignUpWishes(string? InvitationToken, string? TimeZone, bool AcceptTerms);

/// <summary>
/// Finds or creates the account for a Google or Apple identity. A login seen before signs in to its account. Otherwise
/// the provider's confirmed email decides: it joins the account with that address, or creates one. Only an address
/// whose mailbox the provider runs joins an existing account; any other may have changed hands since the provider
/// checked it, so it only makes a new account, which then confirms the address by email like any other.
/// </summary>
internal sealed class ExternalAccounts(
    AppDbContext db,
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    AuthService auth,
    AccountEmails emails,
    TimeProvider clock,
    ILogger<ExternalAccounts> logger)
{
    public async Task<ExternalSignInOutcome> SignInAsync(ExternalIdentity identity, string displayName, ExternalSignUpWishes wishes, CancellationToken cancellationToken)
    {
        if (await users.FindByLoginAsync(identity.Provider, identity.Subject) is { } known)
        {
            return await ExistingAsync(known);
        }

        if (!identity.EmailVerified || string.IsNullOrWhiteSpace(identity.Email))
        {
            return Failed($"Your {displayName} account has no confirmed email address, so it can't be used to sign in here.");
        }

        var email = identity.Email.Trim();
        var login = new UserLoginInfo(identity.Provider, identity.Subject, displayName);
        if (await users.FindByEmailAsync(email) is { DeletedAt: null } user)
        {
            if (!identity.EmailAuthoritative)
            {
                return Failed($"There's already a Spindle account for {email}. Sign in with its password or a passkey; {displayName} can't vouch that the address is still yours.");
            }

            if (!user.EmailConfirmed)
            {
                await TakeOverAsync(user, cancellationToken);
            }

            var linked = await users.AddLoginAsync(user, login);
            if (!linked.Succeeded)
            {
                logger.LogWarning("Couldn't add a {Provider} login to an account: {Errors}", identity.Provider, string.Join(", ", linked.Errors.Select(e => e.Code)));
                return Failed($"Couldn't sign in with {displayName}. Please try again.");
            }

            return await ExistingAsync(user);
        }

        var (created, errors) = await auth.CreateAccountAsync(new NewAccount(
            email, FullName(identity.Name, email), TimeZone(wishes.TimeZone), wishes.InvitationToken, wishes.AcceptTerms)
        {
            Login = login,
            EmailConfirmed = identity.EmailAuthoritative,
        }, cancellationToken);
        if (created is null)
        {
            return Failed(errors.Values.SelectMany(e => e).FirstOrDefault() ?? "Couldn't create the account. Please try again.");
        }

        if (!created.EmailConfirmed)
        {
            await emails.SendConfirmationAsync(created, cancellationToken);
        }

        return new ExternalSignInOutcome(created, null, NewAccount: true);
    }

    private async Task<ExternalSignInOutcome> ExistingAsync(AppUser user)
    {
        if (user.DeletedAt is not null || !await signIn.CanSignInAsync(user))
        {
            return Failed("This account can't sign in.");
        }

        return await users.IsLockedOutAsync(user)
            ? Failed("Too many failed sign-in attempts. The account is temporarily locked; try again later.")
            : new ExternalSignInOutcome(user, null, NewAccount: false);
    }

    /// <summary>
    /// Someone made this account with the address but never confirmed it, and the provider says the address belongs
    /// to the person signing in now. Whoever chose the password may not be them, so it goes, with every passkey and
    /// session that came from it.
    /// </summary>
    private async Task TakeOverAsync(AppUser user, CancellationToken cancellationToken)
    {
        if (await users.HasPasswordAsync(user))
        {
            await users.RemovePasswordAsync(user);
        }

        await db.UserPasskeys.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, clock.GetUtcNow()), cancellationToken);
        user.EmailConfirmed = true;
        await users.UpdateSecurityStampAsync(user);
    }

    private static string FullName(string? name, string email)
    {
        var full = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim();
        return full.Length <= AppUser.FullNameMaxLength ? full : full[..AppUser.FullNameMaxLength];
    }

    private static string TimeZone(string? id) =>
        id is { Length: <= AppUser.TimeZoneMaxLength } && DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) is not null ? id : "UTC";

    private static ExternalSignInOutcome Failed(string error) => new(null, error, NewAccount: false);
}
