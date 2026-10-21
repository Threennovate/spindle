using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Spindle.Api.Auth;
using Spindle.Api.Push;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Billing;
using Spindle.Domain.Clients;
using Spindle.Domain.Groups;
using Spindle.Domain.Invitations;
using Spindle.Domain.Organizations;
using Spindle.Domain.Projects;
using Spindle.Domain.Reports;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Api.Sync;

/// <summary>
/// Notices what each save changes and, once it is saved, tells the apps that show it. Every endpoint is covered
/// without having to remember to. The device that made the change is left out: it knows already.
/// </summary>
internal sealed class SyncInterceptor(
    IHubContext<SyncHub, ISyncClient> hub,
    IHttpContextAccessor http,
    LiveActivityChanges liveActivities,
    ILogger<SyncInterceptor> logger) : SaveChangesInterceptor
{
    /// <summary>The header in which the web app names its own sync connection.</summary>
    public const string ConnectionHeader = "X-Sync-Connection";

    private readonly HashSet<SyncChange> pending = [];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await CollectAsync(context, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
        {
            CollectAsync(context, CancellationToken.None).GetAwaiter().GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await PublishAsync();
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishAsync().GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        pending.Clear();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private async Task CollectAsync(DbContext context, CancellationToken cancellationToken)
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            switch (entry.Entity)
            {
                case TimeEntry e:
                    pending.Add(new SyncChange("entries", e.WorkspaceId, e.UserId));
                    break;
                case Project p:
                    pending.Add(new SyncChange("catalog", p.WorkspaceId, null));
                    break;
                case ProjectTask t:
                    pending.Add(new SyncChange("catalog", t.WorkspaceId, null));
                    break;
                case ProjectMember m:
                    pending.Add(new SyncChange("catalog", m.WorkspaceId, null));
                    break;
                case Client c:
                    pending.Add(new SyncChange("catalog", c.WorkspaceId, null));
                    break;
                case Tag t:
                    pending.Add(new SyncChange("catalog", t.WorkspaceId, null));
                    break;
                case WorkspaceMember m:
                    pending.Add(new SyncChange("team", m.WorkspaceId, null));
                    // Their workspaces and role changed.
                    pending.Add(new SyncChange("me", null, m.UserId));
                    break;
                case Group g:
                    pending.Add(new SyncChange("team", g.WorkspaceId, null));
                    break;
                case GroupMember m:
                    var group = context.ChangeTracker.Entries<Group>().FirstOrDefault(g => g.Entity.Id == m.GroupId)?.Entity
                        ?? await context.Set<Group>().IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(g => g.Id == m.GroupId, cancellationToken);
                    if (group is not null)
                    {
                        pending.Add(new SyncChange("team", group.WorkspaceId, null));
                    }

                    break;
                case Invitation i:
                    pending.Add(new SyncChange("team", i.WorkspaceId, null));
                    break;
                case Workspace w:
                    pending.Add(new SyncChange("settings", w.Id, null));
                    break;
                case SavedReport r:
                    pending.Add(new SyncChange("savedReports", r.WorkspaceId, r.UserId));
                    break;
                case SharedReport r:
                    pending.Add(new SyncChange("sharedReports", r.WorkspaceId, null));
                    break;
                // A rate shows where it is set: with the project, the member, or the workspace's settings.
                case Rate r:
                    pending.Add(new SyncChange(r.ProjectId is not null ? "catalog" : r.UserId is not null ? "team" : "settings", r.WorkspaceId, null));
                    break;
                case BilledTime b:
                    pending.Add(new SyncChange("billing", b.WorkspaceId, null));
                    break;
                // Agreements show with their projects, and close or reopen their tasks.
                case Agreement a:
                    pending.Add(new SyncChange("catalog", a.WorkspaceId, null));
                    break;
                // A person's own agent activity: repository links, dismissed suggestions, ignored repositories and uploaded sessions.
                case AgentSession s:
                    pending.Add(new SyncChange("agentActivity", null, s.UserId));
                    break;
                case AgentRepositoryLink l:
                    pending.Add(new SyncChange("agentActivity", null, l.UserId));
                    break;
                case SplitSuggestionDismissal d:
                    pending.Add(new SyncChange("agentActivity", null, d.UserId));
                    break;
                case IgnoredRepository i:
                    pending.Add(new SyncChange("agentActivity", null, i.UserId));
                    break;
                // What the person's agents proposed, waiting in any of their apps.
                case AgentProposal p:
                    pending.Add(new SyncChange("agentProposals", p.WorkspaceId, p.UserId));
                    break;
                case AppUser u:
                    pending.Add(new SyncChange("me", null, u.Id));
                    break;
                case OrganizationMember m:
                    pending.Add(new SyncChange("me", null, m.UserId));
                    break;
            }
        }
    }

    private async Task PublishAsync()
    {
        if (pending.Count == 0)
        {
            return;
        }

        var changes = pending.ToList();
        pending.Clear();
        var origin = http.HttpContext?.Request.Headers[ConnectionHeader].ToString();
        IReadOnlyList<string> except = string.IsNullOrEmpty(origin) ? [] : [origin];
        // The running timer shows in iPhones' Live Activities, which hear about it through APNs.
        foreach (var userId in changes.Where(c => c.Topic == "entries").Select(c => c.UserId).OfType<Guid>().Distinct())
        {
            liveActivities.Changed(userId, http.HttpContext?.User.GetSessionId());
        }

        try
        {
            foreach (var change in changes)
            {
                // A person's own devices hear about their entries in any workspace, for the running timer;
                // a workspace's watchers hear about everyone's, for reports and the team.
                if (change.UserId is { } userId && change.Topic is "entries" or "me" or "savedReports" or "agentActivity" or "agentProposals")
                {
                    await hub.Clients.GroupExcept(SyncGroups.User(userId), except).Changed(change);
                }

                if (change.WorkspaceId is { } workspaceId && change.Topic is not ("savedReports" or "agentProposals"))
                {
                    await hub.Clients.GroupExcept(SyncGroups.Workspace(workspaceId), except).Changed(change);
                }
            }
        }
        catch (Exception exception)
        {
            // The change is saved; apps that miss the news catch up when they next load.
            logger.LogWarning(exception, "Could not tell the apps about {Count} changes", changes.Count);
        }
    }
}
