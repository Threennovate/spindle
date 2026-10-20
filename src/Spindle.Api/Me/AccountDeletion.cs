using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Me;

/// <summary>
/// Deletes a person's account. What only they used goes entirely: workspaces where they were alone, with all their
/// data, and organizations left empty. In workspaces shared with others their time stays, for the team's reports,
/// under the name "Deleted user", and every other trace of them (name, email, password, sessions, agent activity) is
/// removed.
/// </summary>
internal sealed class AccountDeletion(AppDbContext db, SubscriptionBilling billing, SeatChanges seatChanges, TimeProvider timeProvider)
{
    // The plans of organizations that go with the account, ended with the provider once the deletion is saved.
    private readonly List<string> endedPlans = [];

    public const string DeletedName = "Deleted user";

    /// <summary>Why the account cannot be deleted yet, if it cannot: a shared workspace would be left without an admin.</summary>
    public async Task<string?> BlockerAsync(Guid userId, CancellationToken cancellationToken)
    {
        var workspaces = await (
            from member in db.WorkspaceMembers
            join workspace in db.Workspaces on member.WorkspaceId equals workspace.Id
            where member.UserId == userId
                && member.Role == WorkspaceRole.Admin
                && db.WorkspaceMembers.Any(o => o.WorkspaceId == member.WorkspaceId && o.UserId != userId)
                && !db.WorkspaceMembers.Any(o => o.WorkspaceId == member.WorkspaceId && o.UserId != userId && o.Role == WorkspaceRole.Admin)
            orderby workspace.Name
            select workspace.Name)
            .ToListAsync(cancellationToken);

        return workspaces.Count == 0
            ? null
            : $"You are the only admin of {string.Join(", ", workspaces)}. Make someone else an admin there first.";
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        // The organizations the person takes a seat in, which have one fewer afterwards.
        var seated = await db.WorkspaceMembers
            .Where(m => m.UserId == userId)
            .Join(db.Workspaces, m => m.WorkspaceId, w => w.Id, (m, w) => w.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            endedPlans.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = timeProvider.GetUtcNow();

            var solo = await db.WorkspaceMembers
                .Where(m => m.UserId == userId && !db.WorkspaceMembers.Any(o => o.WorkspaceId == m.WorkspaceId && o.UserId != userId))
                .Select(m => m.WorkspaceId)
                .ToListAsync(ct);
            foreach (var workspaceId in solo)
            {
                await EraseWorkspaceAsync(workspaceId, ct);
            }

            // A timer left running in a shared workspace stops now.
            foreach (var running in await db.TimeEntries.Where(e => e.UserId == userId && e.Stop == null).ToListAsync(ct))
            {
                running.StopAt(now);
            }

            db.WorkspaceMembers.RemoveRange(await db.WorkspaceMembers.Where(m => m.UserId == userId).ToListAsync(ct));
            db.ProjectMembers.RemoveRange(await db.ProjectMembers.Where(m => m.UserId == userId).ToListAsync(ct));
            db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.UserId == userId).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await db.SavedReports.IgnoreQueryFilters().Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);

            await LeaveOrganizationsAsync(userId, ct);
            await ForgetUserAsync(userId, now, ct);

            await transaction.CommitAsync(ct);
        }, cancellationToken);

        await billing.CancelEndedAsync(endedPlans, cancellationToken);
        foreach (var organizationId in seated)
        {
            seatChanges.Changed(organizationId);
        }
    }

    /// <summary>Removes a workspace and everything in it for good, tombstones included.</summary>
    private async Task EraseWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        await db.Users.Where(u => u.DefaultWorkspaceId == workspaceId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DefaultWorkspaceId, (Guid?)null), cancellationToken);
        await db.TimeEntries.IgnoreQueryFilters().Where(e => e.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.BilledTimes.IgnoreQueryFilters().Where(b => b.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Rates.IgnoreQueryFilters().Where(r => r.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.ProjectMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Tasks.IgnoreQueryFilters().Where(t => t.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Agreements.IgnoreQueryFilters().Where(a => a.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Projects.IgnoreQueryFilters().Where(p => p.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Clients.IgnoreQueryFilters().Where(c => c.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Tags.IgnoreQueryFilters().Where(t => t.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.GroupMembers.IgnoreQueryFilters()
            .Where(m => db.Groups.IgnoreQueryFilters().Any(g => g.Id == m.GroupId && g.WorkspaceId == workspaceId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Groups.IgnoreQueryFilters().Where(g => g.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Invitations.IgnoreQueryFilters().Where(i => i.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.SavedReports.IgnoreQueryFilters().Where(r => r.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.SharedReports.IgnoreQueryFilters().Where(r => r.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentProposals.Where(p => p.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.WorkspaceMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == workspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.Workspaces.IgnoreQueryFilters().Where(w => w.Id == workspaceId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Leaves each organization. One left without members or workspaces goes; otherwise, if the person owned it,
    /// the ownership passes on: to an organization admin, else an admin of one of its workspaces, else the member
    /// who joined first.
    /// </summary>
    private async Task LeaveOrganizationsAsync(Guid userId, CancellationToken cancellationToken)
    {
        foreach (var membership in await db.OrganizationMembers.Where(m => m.UserId == userId).ToListAsync(cancellationToken))
        {
            var organizationId = membership.OrganizationId;
            var others = await db.OrganizationMembers
                .Where(m => m.OrganizationId == organizationId && m.UserId != userId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync(cancellationToken);
            var hasWorkspaces = await db.Workspaces.AnyAsync(w => w.OrganizationId == organizationId, cancellationToken);

            if (others.Count == 0 && !hasWorkspaces)
            {
                // Nobody is left to pay for it, so its plan ends too.
                endedPlans.AddRange(await db.OrganizationSubscriptions
                    .Where(s => s.OrganizationId == organizationId && s.ProviderSubscriptionId != null
                        && (s.ProviderStatus == ProviderSubscriptionStatus.Trialing
                            || s.ProviderStatus == ProviderSubscriptionStatus.Active
                            || s.ProviderStatus == ProviderSubscriptionStatus.PastDue))
                    .Select(s => s.ProviderSubscriptionId!)
                    .ToListAsync(cancellationToken));
                await db.Invitations.IgnoreQueryFilters().Where(i => i.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                await db.Workspaces.IgnoreQueryFilters().Where(w => w.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                await db.OrganizationMembers.IgnoreQueryFilters().Where(m => m.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                await db.Organizations.IgnoreQueryFilters().Where(o => o.Id == organizationId).ExecuteDeleteAsync(cancellationToken);
                continue;
            }

            if (membership.Role == OrganizationRole.Owner && others.Count > 0 && others.All(m => m.Role != OrganizationRole.Owner))
            {
                var workspaceAdmins = await db.WorkspaceMembers
                    .Where(m => m.Role == WorkspaceRole.Admin && m.UserId != userId
                        && db.Workspaces.Any(w => w.Id == m.WorkspaceId && w.OrganizationId == organizationId))
                    .Select(m => m.UserId)
                    .ToListAsync(cancellationToken);
                var heir = others.FirstOrDefault(m => m.Role == OrganizationRole.Admin)
                    ?? others.FirstOrDefault(m => workspaceAdmins.Contains(m.UserId))
                    ?? others[0];
                heir.ChangeRole(OrganizationRole.Owner);
            }

            db.OrganizationMembers.Remove(membership);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Keeps the row that shared time points to, and nothing personal in it.</summary>
    private async Task ForgetUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.PushDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.OAuthAuthorizationCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentRepositoryLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.SplitSuggestionDismissals.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.AgentProposals.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserPasskeys.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.UserLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.ExternalSignInCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.PasskeyChallenges.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        var address = $"deleted-{userId:N}@deleted.invalid";
        user.Email = user.UserName = address;
        user.NormalizedEmail = user.NormalizedUserName = address.ToUpperInvariant();
        user.EmailConfirmed = false;
        user.FullName = DeletedName;
        user.PasswordHash = null;
        user.PhoneNumber = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.DefaultWorkspaceId = null;
        user.AgentActivitySync = user.AgentActivityTitles = false;
        user.AgentActivitySyncedSince = null;
        user.DeletedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }
}
