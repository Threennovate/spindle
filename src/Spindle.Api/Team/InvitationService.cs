using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Common;
using Spindle.Api.Email;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Invitations;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Team;

internal sealed class InvitationService(
    AppDbContext db,
    IEmailSender emailSender,
    IOptions<AppOptions> app,
    TimeProvider timeProvider,
    ILogger<InvitationService> logger,
    SeatChanges seatChanges)
{
    public static (string Token, byte[] Hash) NewToken()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public string Link(string token) => app.Value.Link($"/invitations/{token}");

    public Task<Invitation?> FindAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        return db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
    }

    /// <summary>Emails the invitation. Returns false when sending failed; the invitation stays valid and its link can be shared by hand.</summary>
    public async Task<bool> SendAsync(Invitation invitation, string token, CancellationToken cancellationToken)
    {
        var details = await (
            from workspace in db.Workspaces
            join inviter in db.Users on invitation.InvitedByUserId equals inviter.Id
            where workspace.Id == invitation.WorkspaceId
            select new { WorkspaceName = workspace.Name, InviterName = inviter.FullName })
            .SingleAsync(cancellationToken);

        try
        {
            await emailSender.SendAsync(EmailTemplates.Invitation(invitation.Email, details.InviterName, details.WorkspaceName, Link(token)), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not email the invitation {InvitationId}", invitation.Id);
            return false;
        }
    }

    /// <summary>Makes the user a member of the invitation's organization and workspace, and switches them to it. Does not save.</summary>
    public async Task AcceptAsync(Invitation invitation, AppUser user, CancellationToken cancellationToken)
    {
        invitation.Accept(user.Id, timeProvider.GetUtcNow());

        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == user.Id, cancellationToken))
        {
            db.OrganizationMembers.Add(OrganizationMember.Create(invitation.OrganizationId, user.Id, OrganizationRole.Member));
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == user.Id, cancellationToken))
        {
            db.WorkspaceMembers.Add(WorkspaceMember.Create(invitation.WorkspaceId, user.Id, invitation.Role));
        }

        user.DefaultWorkspaceId = invitation.WorkspaceId;
        seatChanges.Changed(invitation.OrganizationId);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
