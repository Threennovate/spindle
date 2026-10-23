using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Workspaces;

/// <summary>Where a workspace the person creates goes, so the apps can say so before they create it.</summary>
/// <param name="Allowed">Whether the person may create a workspace at all.</param>
/// <param name="OrganizationId">
/// The organization it goes in: the current workspace's, when the person owns or administers it, or else another one
/// they do. Empty when they run none: then it starts an organization of their own, which they own and pay for, apart
/// from the organizations that invited them.
/// </param>
public sealed record NewWorkspaceResponse(bool Allowed, Guid? OrganizationId)
{
    /// <summary>How long the trial of the new organization of their own lasts, on the hosted service.</summary>
    public int? TrialDays { get; init; }
}

/// <summary>
/// Creating workspaces. Owners and admins of an organization add them to it. Anyone else, such as someone who joined
/// by invitation, gets an organization of their own with it, as at sign-up, and so its trial. Each person has at most
/// one such trial, since the organization they own stays theirs. On a server that takes new people only by
/// invitation, only owners and admins create workspaces, as anyone else couldn't have signed up for one either.
/// </summary>
internal sealed class NewWorkspaces(AppDbContext db, OrganizationSubscriptions subscriptions, IOptions<AuthOptions> auth)
{
    /// <param name="organizations">The person's organizations, as <see cref="MeResponse.Organizations"/> lists them.</param>
    /// <param name="currentOrganizationId">The organization of the workspace the person works in.</param>
    public NewWorkspaceResponse Describe(IReadOnlyList<OrganizationSummary> organizations, Guid? currentOrganizationId)
    {
        var managed = organizations.Where(o => o.Role != OrganizationRole.Member).Select(o => o.Id).ToList();
        if (managed.Count > 0)
        {
            return new(true, currentOrganizationId is { } current && managed.Contains(current) ? current : managed[0]);
        }

        return auth.Value.AllowRegistration
            ? new(true, null) { TrialDays = subscriptions.Enabled ? subscriptions.TrialDays : null }
            : new(false, null);
    }

    /// <summary>
    /// Creates the workspace where <see cref="Describe"/> says, with the person as its admin, and switches them to it.
    /// Null when they may not create one. The caller saves the changes; when the person started an organization of their
    /// own at the same moment, saving fails with <see cref="DbUpdateConcurrencyException"/>, and creating again puts
    /// the workspace in that organization.
    /// </summary>
    public async Task<WorkspaceSummary?> CreateAsync(AppUser user, string name, CancellationToken cancellationToken)
    {
        var organizations = await (
            from member in db.OrganizationMembers
            join organization in db.Organizations on member.OrganizationId equals organization.Id
            where member.UserId == user.Id
            orderby organization.Name
            select new OrganizationSummary(organization.Id, organization.Name, member.Role))
            .ToListAsync(cancellationToken);
        var currentOrganizationId = await db.Workspaces
            .Where(w => w.Id == user.DefaultWorkspaceId)
            .Select(w => (Guid?)w.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);

        var target = Describe(organizations, currentOrganizationId);
        if (!target.Allowed)
        {
            return null;
        }

        if (target.OrganizationId is null)
        {
            // Two requests at once would each find no organization of the person's own and start one, each with a
            // trial. A new stamp makes saving the person fail for the later one, which then starts over.
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
        }

        var workspace = target.OrganizationId is { } organizationId
            ? AddWorkspace(organizationId, name, user)
            : StartOwnOrganization(user, name);
        return new WorkspaceSummary(workspace.Id, workspace.OrganizationId, workspace.Name, WorkspaceRole.Admin);
    }

    /// <summary>
    /// Starts an organization of the person's own, named after them, with one workspace they run and its trial. Every
    /// account that signs up without an invitation starts with one.
    /// </summary>
    public Workspace StartOwnOrganization(AppUser user, string workspaceName)
    {
        var organization = Organization.Create(user.FullName);
        db.AddRange(organization, OrganizationMember.Create(organization.Id, user.Id, OrganizationRole.Owner));
        subscriptions.StartTrial(organization.Id);
        return AddWorkspace(organization.Id, workspaceName, user);
    }

    private Workspace AddWorkspace(Guid organizationId, string name, AppUser user)
    {
        var workspace = Workspace.Create(organizationId, name);
        db.AddRange(workspace, WorkspaceMember.Create(workspace.Id, user.Id, WorkspaceRole.Admin));
        user.DefaultWorkspaceId = workspace.Id;
        return workspace;
    }
}
