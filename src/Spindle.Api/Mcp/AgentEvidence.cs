using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.AgentActivity;
using Spindle.Api.Workspaces;
using Spindle.Domain.AgentActivity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Mcp;

/// <summary>
/// What the server knows of the person's agent work, for the MCP tools: only what their Macs synced, and only while
/// they let them (agent activity sync). It says when the person prompted agents, when agents were busy, in which
/// repository and on which branches, and session titles if those are synced too; never prompts or commits. So the
/// evidence names the repository, branches and stretch of time, and an agent that has the repository at hand reads
/// the commits itself.
/// </summary>
internal sealed class AgentEvidence(AppDbContext db, CurrentWorkspace workspace, WorkspacePolicy policy, TimeProvider timeProvider)
{
    /// <summary>The synced activity around a range, with the projects its repositories belong to.</summary>
    public sealed record Activity(
        bool Sync,
        DateTimeOffset? SyncedSince,
        IReadOnlyList<AgentSession> Sessions,
        IReadOnlyList<AttentionSegment> Attention,
        RepositoryLinks Links,
        IReadOnlyList<LinkableProject> Projects)
    {
        public Guid? ProjectFor(AgentRepository repository) => Links.ProjectFor(repository, Projects);
    }

    /// <summary>Reads what was synced for [<paramref name="from"/>, <paramref name="to"/>), and enough before it to learn links.</summary>
    public async Task<Activity> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var userId = workspace.UserId;
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AgentActivitySync, u.AgentActivitySyncedSince })
            .SingleAsync(cancellationToken);
        var projects = await policy.VisibleProjects().AsNoTracking()
            .Where(p => p.ArchivedAt == null)
            .OrderBy(p => p.Name)
            .Select(p => new LinkableProject(p.Id, p.Name))
            .ToListAsync(cancellationToken);
        if (!user.AgentActivitySync)
        {
            return new Activity(false, null, [], [], new RepositoryLinks(new Dictionary<string, Guid>(), new Dictionary<string, Guid>()), projects);
        }

        // As for split suggestions: repositories are linked to the projects of the entries their work fell in lately.
        var since = from - SplitSuggestions.LearningPeriod;
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.FirstAt < to && s.LastAt >= since.AddMilliseconds(-AttentionTimeline.IdleCap))
            .ToListAsync(cancellationToken);
        var touches = sessions.Where(s => s.ParentSessionId == null)
            .SelectMany(s => s.Touches.Select(at => new AgentTouch(at, s.SessionId, s.Repository)));
        var attention = AttentionTimeline.Build(touches, since.ToUnixTimeMilliseconds(), to.ToUnixTimeMilliseconds());

        var now = timeProvider.GetUtcNow();
        var tracked = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == userId && e.Start >= since && e.Start < to && !e.IsSplit)
            .Select(e => new RepositoryLinks.Entry(e.Start.ToUnixTimeMilliseconds(), e.Stop.HasValue ? e.Stop.Value.ToUnixTimeMilliseconds() : null, e.ProjectId, e.IsSplit))
            .ToListAsync(cancellationToken);
        var chosen = await db.AgentRepositoryLinks.AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToListAsync(cancellationToken);
        var links = RepositoryLinks.Of(chosen, RepositoryLinks.Learn(tracked, attention, now.ToUnixTimeMilliseconds()));

        var start = from.ToUnixTimeMilliseconds();
        return new Activity(
            true,
            user.AgentActivitySyncedSince,
            sessions,
            attention.Where(a => a.End > start).Select(a => a with { Start = Math.Max(a.Start, start) }).ToList(),
            links,
            projects);
    }

    /// <summary>
    /// Per repository, what happened in [<paramref name="start"/>, <paramref name="end"/>): the person's attention and
    /// the agents' busy time in minutes, prompts, branches, titles, its project, and how to read its commits.
    /// </summary>
    public static List<object> Repositories(Activity activity, DateTimeOffset start, DateTimeOffset end, IReadOnlyDictionary<Guid, string> projectNames, DateTimeZone zone)
    {
        var (from, to) = (start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());
        var active = activity.Sessions
            .Where(s => s.Touches.Any(t => t >= from && t < to) || s.RunStarts.Zip(s.RunEnds).Any(r => r.First < to && r.Second > from))
            .ToList();
        var attention = activity.Attention
            .Where(a => a.Repository is not null && a.End > from && a.Start < to)
            .GroupBy(a => a.Repository!.Identity)
            .ToDictionary(g => g.Key, g => g.Sum(a => Math.Min(a.End, to) - Math.Max(a.Start, from)));

        var repositories = new List<object>();
        foreach (var group in active.Where(s => s.Repository is not null).GroupBy(s => s.RepositoryIdentity!).OrderByDescending(g => attention.GetValueOrDefault(g.Key)))
        {
            var repository = group.First().Repository!;
            var busy = UntrackedActivity.Merge(
                group.SelectMany(s => s.RunStarts.Zip(s.RunEnds))
                    .Where(r => r.First < to && r.Second > from)
                    .Select(r => new TimeRange(Math.Max(r.First, from), Math.Min(r.Second, to))),
                0);
            var projectId = activity.ProjectFor(repository);
            var branches = group.SelectMany(s => s.Branches).Distinct().Order(StringComparer.Ordinal).ToArray();
            repositories.Add(new
            {
                name = repository.Name,
                git = Git(repository, branches, start, end, zone),
                project = projectId is { } id ? new { id, name = projectNames.GetValueOrDefault(id) ?? "" } : null,
                minutes = Math.Round(attention.GetValueOrDefault(group.Key) / 60_000.0),
                agent_busy_minutes = Math.Round(busy.Sum(r => r.Duration) / 60_000.0),
                prompts = group.Where(s => s.ParentSessionId == null).Sum(s => s.Touches.Count(t => t >= from && t < to)),
                branches,
                session_titles = group.Select(s => s.Title).OfType<string>().Distinct().ToArray(),
            });
        }

        return repositories;
    }

    /// <summary>Where the repository is and how to read the person's commits in the window, for an agent that has it.</summary>
    private static object Git(AgentRepository repository, string[] branches, DateTimeOffset start, DateTimeOffset end, DateTimeZone zone)
    {
        var origin = repository.Identity.StartsWith("origin:", StringComparison.Ordinal) ? repository.Identity["origin:".Length..] : null;
        var since = Iso(start, zone);
        var until = Iso(end, zone);
        return new
        {
            origin,
            since,
            until,
            log = $"git log --all --author=\"$(git config user.email)\" --since={since} --until={until} --date=iso-strict --format=\"%h %ad %s\"",
            note = origin is null ? "A folder without a git origin; the Mac knows it by name only." : null,
        };
    }

    public static string Iso(DateTimeOffset instant, DateTimeZone zone) =>
        Instant.FromDateTimeOffset(instant).InZone(zone).ToDateTimeOffset().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>What to tell the agent when there is nothing synced to go by, or null when there is.</summary>
    public static string? Missing(Activity activity, DateTimeOffset start, IEnumerable<object> found)
    {
        if (!activity.Sync)
        {
            return "Agent activity sync is off for this account, so Spindle's server doesn't know when agents were busy. "
                + "If you have the repository, run git log for the time yourself; otherwise ask the person. "
                + "They can turn it on on the web under Account › Agent activity (“Sync agent activity from your computers”).";
        }

        if (found.Any())
        {
            return null;
        }

        return activity.SyncedSince is { } since && since > start
            ? $"No synced agent activity here: sync was turned on only at {since:yyyy-MM-dd HH:mm} UTC. If you have the repository, run git log for the time yourself; otherwise ask the person."
            : "No synced agent activity for this time. If you have the repository, run git log for the time yourself; otherwise ask the person.";
    }
}
