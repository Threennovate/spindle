using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.Workspaces;
using Spindle.Domain.Invitations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Team;

public sealed record InvitationResponse(Guid Id, string Email, WorkspaceRole Role, DateTimeOffset ExpiresAt, bool IsExpired, string InvitedBy);

/// <summary>A new or renewed invitation. The link is shown to the admin so it can be shared by hand if the email does not arrive.</summary>
public sealed record SentInvitationResponse(InvitationResponse Invitation, string InviteUrl, bool EmailSent);

public sealed record InviteRequest
{
    [Required]
    [EmailAddress]
    [StringLength(Invitation.EmailMaxLength)]
    public string Email { get; init; } = string.Empty;

    public WorkspaceRole Role { get; init; } = WorkspaceRole.Member;
}

/// <summary>What the person following an invitation link sees before accepting.</summary>
public sealed record InvitationPreview(string Email, string WorkspaceName, string OrganizationName, string InvitedBy, InvitationStatus Status);

public sealed record AcceptedInvitationResponse(Guid WorkspaceId);

internal static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/invitations").WithTags("Team").RequireWorkspaceAdmin();

        group.MapGet("", ListAsync).WithName("listInvitations");
        group.MapPost("", InviteAsync).WithName("invite");
        group.MapPost("/{invitationId:guid}/resend", ResendAsync).WithName("resendInvitation");
        group.MapDelete("/{invitationId:guid}", RevokeAsync).WithName("revokeInvitation").AllowWhenReadOnly();

        return workspace;
    }

    /// <summary>Opening and accepting an invitation link, outside any workspace.</summary>
    public static IEndpointRouteBuilder MapPublicInvitationEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/invitations/{token}").WithTags("Team");

        group.MapGet("", PreviewAsync).WithName("previewInvitation").AllowAnonymous();
        group.MapPost("/accept", AcceptAsync)
            .WithName("acceptInvitation")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Ok<List<InvitationResponse>>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invitations = await (
            from invitation in db.Invitations
            join inviter in db.Users on invitation.InvitedByUserId equals inviter.Id
            where invitation.WorkspaceId == workspace.Id && invitation.AcceptedAt == null && invitation.RevokedAt == null
            orderby invitation.CreatedAt descending
            select new InvitationResponse(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, invitation.ExpiresAt <= now, inviter.FullName))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(invitations);
    }

    /// <summary>Invites someone by email. Inviting an address that already has an open invitation sends it again.</summary>
    private static async Task<Results<Created<SentInvitationResponse>, ValidationProblem>> InviteAsync(
        InviteRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        InvitationService invitations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var email = Invitation.NormalizeEmail(request.Email);
        var normalizedEmail = email.ToUpperInvariant();
        var alreadyMember = await (
            from member in db.WorkspaceMembers
            join user in db.Users on member.UserId equals user.Id
            where member.WorkspaceId == workspace.Id && user.NormalizedEmail == normalizedEmail
            select member).AnyAsync(cancellationToken);
        if (alreadyMember)
        {
            return Problems.Validation("email", "This person is already a member of the workspace.");
        }

        var now = timeProvider.GetUtcNow();
        var (token, hash) = InvitationService.NewToken();
        var invitation = await db.Invitations.SingleOrDefaultAsync(
            i => i.WorkspaceId == workspace.Id && i.Email == email && i.AcceptedAt == null && i.RevokedAt == null,
            cancellationToken);

        if (invitation is null)
        {
            var organizationId = await db.Workspaces.Where(w => w.Id == workspace.Id).Select(w => w.OrganizationId).SingleAsync(cancellationToken);
            invitation = Invitation.Create(organizationId, workspace.Id, email, request.Role, workspace.UserId, hash, now);
            db.Invitations.Add(invitation);
        }
        else
        {
            invitation.Renew(hash, request.Role, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created((string?)null, await SendAsync(db, invitations, invitation, token, cancellationToken));
    }

    private static async Task<Results<Ok<SentInvitationResponse>, NotFound, ValidationProblem>> ResendAsync(
        Guid invitationId,
        CurrentWorkspace workspace,
        AppDbContext db,
        InvitationService invitations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.WorkspaceId == workspace.Id, cancellationToken);
        if (invitation is null)
        {
            return TypedResults.NotFound();
        }

        var (token, hash) = InvitationService.NewToken();
        invitation.Renew(hash, invitation.Role, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await SendAsync(db, invitations, invitation, token, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(
        Guid invitationId,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.WorkspaceId == workspace.Id, cancellationToken);
        if (invitation is null)
        {
            return TypedResults.NotFound();
        }

        invitation.Revoke(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<InvitationPreview>, NotFound>> PreviewAsync(
        string token,
        AppDbContext db,
        InvitationService invitations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindAsync(token, cancellationToken);
        if (invitation is null)
        {
            return TypedResults.NotFound();
        }

        var details = await (
            from workspace in db.Workspaces
            join organization in db.Organizations on workspace.OrganizationId equals organization.Id
            join inviter in db.Users on invitation.InvitedByUserId equals inviter.Id
            where workspace.Id == invitation.WorkspaceId
            select new { WorkspaceName = workspace.Name, OrganizationName = organization.Name, InviterName = inviter.FullName })
            .SingleAsync(cancellationToken);

        return TypedResults.Ok(new InvitationPreview(
            invitation.Email,
            details.WorkspaceName,
            details.OrganizationName,
            details.InviterName,
            invitation.StatusAt(timeProvider.GetUtcNow())));
    }

    /// <summary>Accepts the invitation as the signed-in user, whose email must be the one invited.</summary>
    private static async Task<Results<Ok<AcceptedInvitationResponse>, NotFound, ValidationProblem, ProblemHttpResult>> AcceptAsync(
        string token,
        ClaimsPrincipal principal,
        AppDbContext db,
        InvitationService invitations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindAsync(token, cancellationToken);
        if (invitation is null)
        {
            return TypedResults.NotFound();
        }

        if (invitation.StatusAt(timeProvider.GetUtcNow()) != InvitationStatus.Pending)
        {
            return Problems.Validation("invitation", "This invitation has expired, was withdrawn or was already used.");
        }

        var userId = principal.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "This invitation is for another email address.",
                detail: $"Sign in as {invitation.Email} to accept it.");
        }

        await invitations.AcceptAsync(invitation, user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new AcceptedInvitationResponse(invitation.WorkspaceId));
    }

    private static async Task<SentInvitationResponse> SendAsync(
        AppDbContext db,
        InvitationService invitations,
        Invitation invitation,
        string token,
        CancellationToken cancellationToken)
    {
        var emailSent = await invitations.SendAsync(invitation, token, cancellationToken);
        var inviter = await db.Users.Where(u => u.Id == invitation.InvitedByUserId).Select(u => u.FullName).SingleAsync(cancellationToken);
        return new SentInvitationResponse(
            new InvitationResponse(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, IsExpired: false, inviter),
            invitations.Link(token),
            emailSent);
    }
}
