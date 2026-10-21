using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Projects;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Me;

/// <summary>A person's own data, as a file they can keep or take elsewhere.</summary>
internal static class DataExport
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed record Export(DateTimeOffset ExportedAt, Profile Profile, IReadOnlyList<WorkspaceData> Workspaces, AgentActivityData AgentActivity);

    public sealed record Profile(string FullName, string Email, string TimeZone, DateTimeOffset CreatedAt, PreferencesResponse Preferences);

    /// <param name="Role">The person's role there, or none if they have left the workspace.</param>
    public sealed record WorkspaceData(Guid Id, string Name, WorkspaceRole? Role, IReadOnlyList<Entry> TimeEntries);

    public sealed record Entry(
        Guid Id,
        string Description,
        string? Project,
        string? Client,
        string? Task,
        IReadOnlyList<string> Tags,
        bool Billable,
        DateTimeOffset Start,
        DateTimeOffset? Stop,
        IReadOnlyList<Share> Split);

    /// <summary>What the person's Macs uploaded from agent logs, if they turned that on, the projects they chose for repositories, and the hashes of those they ignore.</summary>
    public sealed record AgentActivityData(
        bool Sync, bool Titles, IReadOnlyList<RepositoryLink> RepositoryLinks, IReadOnlyList<AgentSessionData> Sessions, IReadOnlyList<string> IgnoredRepositories);

    public sealed record RepositoryLink(string Repository, string Identity, string? Project, IReadOnlyList<BranchRuleData> BranchRules);

    public sealed record BranchRuleData(string Pattern, string? Project, string? Task);

    public sealed record AgentSessionData(
        Guid Device,
        string Source,
        string SessionId,
        string? ParentSessionId,
        string? Repository,
        string? RepositoryIdentity,
        string? Title,
        IReadOnlyList<string> Branches,
        IReadOnlyList<DateTimeOffset> Prompts,
        IReadOnlyList<AgentRun> Runs);

    public sealed record AgentRun(DateTimeOffset Start, DateTimeOffset End);

    /// <summary>One part of an entry whose time was split between projects; the entry's own project is its largest.</summary>
    public sealed record Share(string? Project, string? Client, string? Task, bool Billable, int Percent);

    public static async Task<Export> CreateAsync(AppDbContext db, AppUser user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .OrderBy(e => e.Start)
            .ToListAsync(cancellationToken);
        var roles = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .ToDictionaryAsync(m => m.WorkspaceId, m => m.Role, cancellationToken);
        var workspaceIds = entries.Select(e => e.WorkspaceId).Concat(roles.Keys).Distinct().ToArray();

        var workspaces = await db.Workspaces.AsNoTracking().Where(w => workspaceIds.Contains(w.Id)).OrderBy(w => w.Name).ToListAsync(cancellationToken);
        var projects = await db.Projects.AsNoTracking().Where(p => workspaceIds.Contains(p.WorkspaceId)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var clients = await db.Clients.AsNoTracking().Where(c => workspaceIds.Contains(c.WorkspaceId)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var tasks = await db.Tasks.AsNoTracking().Where(t => workspaceIds.Contains(t.WorkspaceId)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var tags = await db.Tags.AsNoTracking().Where(t => workspaceIds.Contains(t.WorkspaceId)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var byWorkspace = entries.ToLookup(e => e.WorkspaceId);
        var links = await (
            from link in db.AgentRepositoryLinks
            join project in db.Projects.IgnoreQueryFilters() on link.ProjectId equals (Guid?)project.Id into linked
            from project in linked.DefaultIfEmpty()
            where link.UserId == user.Id
            orderby link.Name
            select new { link, Project = project != null && project.DeletedAt == null ? project.Name : null })
            .ToListAsync(cancellationToken);
        // Branch rules name projects and tasks by ID; a deleted one has no name left to show.
        var ruleProjectIds = links.SelectMany(l => l.link.BranchRules).Select(r => r.ProjectId).Distinct().ToList();
        var ruleTaskIds = links.SelectMany(l => l.link.BranchRules).Select(r => r.TaskId).OfType<Guid>().Distinct().ToList();
        var ruleProjects = await db.Projects.AsNoTracking().Where(p => ruleProjectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var ruleTasks = await db.Tasks.AsNoTracking().Where(t => ruleTaskIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var repositoryLinks = links.Select(l => new RepositoryLink(
            l.link.Name,
            l.link.Identity,
            l.Project,
            l.link.BranchRules.Select(r => new BranchRuleData(
                r.Pattern, ruleProjects.GetValueOrDefault(r.ProjectId), r.TaskId is { } taskId ? ruleTasks.GetValueOrDefault(taskId) : null)).ToList()))
            .ToList();
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .OrderBy(s => s.FirstAt)
            .ToListAsync(cancellationToken);
        // Hashes alone: the server never knew which repositories they are.
        var ignored = await db.IgnoredRepositories.AsNoTracking()
            .Where(i => i.UserId == user.Id)
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.Hash)
            .ToListAsync(cancellationToken);

        return new Export(
            now,
            new Profile(user.FullName, user.Email!, user.TimeZone, user.CreatedAt, PreferencesResponse.From(user)),
            workspaces.Select(workspace => new WorkspaceData(
                workspace.Id,
                workspace.Name,
                roles.TryGetValue(workspace.Id, out var role) ? role : null,
                byWorkspace[workspace.Id].Select(e =>
                {
                    Project? ProjectOf(Guid? id) => id is { } projectId ? projects.GetValueOrDefault(projectId) : null;
                    string? ClientOf(Project? project) => project?.ClientId is { } clientId ? clients.GetValueOrDefault(clientId) : null;
                    string? TaskOf(Guid? id) => id is { } taskId ? tasks.GetValueOrDefault(taskId) : null;
                    var project = ProjectOf(e.ProjectId);
                    return new Entry(
                        e.Id,
                        e.Description,
                        project?.Name,
                        ClientOf(project),
                        TaskOf(e.TaskId),
                        e.TagIds.Where(tags.ContainsKey).Select(id => tags[id]).ToList(),
                        e.IsBillable,
                        e.Start,
                        e.Stop,
                        e.Shares.Select(s => new Share(ProjectOf(s.ProjectId)?.Name, ClientOf(ProjectOf(s.ProjectId)), TaskOf(s.TaskId), s.IsBillable, s.Percent)).ToList());
                }).ToList())).ToList(),
            new AgentActivityData(
                user.AgentActivitySync,
                user.AgentActivityTitles,
                repositoryLinks,
                sessions.Select(s => new AgentSessionData(
                    s.DeviceId,
                    s.Source,
                    s.SessionId,
                    s.ParentSessionId,
                    s.RepositoryName,
                    s.RepositoryIdentity,
                    s.Title,
                    s.Branches,
                    s.Touches.Select(DateTimeOffset.FromUnixTimeMilliseconds).ToList(),
                    s.RunStarts.Zip(s.RunEnds, (start, end) => new AgentRun(DateTimeOffset.FromUnixTimeMilliseconds(start), DateTimeOffset.FromUnixTimeMilliseconds(end))).ToList()))
                    .ToList(),
                ignored));
    }
}
