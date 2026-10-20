using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Workspaces;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.AgentActivity;

/// <summary>
/// Works out split suggestions from the agent activity a person's Macs uploaded, by the same rules as the Mac. Only
/// for the caller's own entries: activity is personal and never says anything about anyone else.
/// </summary>
internal sealed class SplitSuggestions(AppDbContext db, CurrentWorkspace workspace, WorkspacePolicy policy, TimeProvider timeProvider)
{
    /// <summary>Links are learned from the entries of this long before the entries suggested for.</summary>
    public static readonly TimeSpan LearningPeriod = TimeSpan.FromDays(28);

    /// <summary>Suggestions for the caller's entries in this workspace that start within [from, to).</summary>
    public async Task<List<SplitSuggestionResponse>> ForRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var userId = workspace.UserId;
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AgentActivitySync, u.TimeZone })
            .SingleAsync(cancellationToken);
        if (!user.AgentActivitySync)
        {
            return [];
        }

        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == userId && e.Start >= from && e.Start < to && !e.IsSplit)
            .Where(e => !db.SplitSuggestionDismissals.Any(d => d.UserId == userId && d.EntryId == e.Id))
            .OrderBy(e => e.Start)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return [];
        }

        var zone = Zone(user.TimeZone);
        var now = timeProvider.GetUtcNow();
        var windows = entries.Select(e => Window(e.Start, e.Stop ?? now, zone)).ToList();
        var (attention, links, projects) = await ActivityAsync(windows.Min(w => w.Start), windows.Max(w => w.End), now, cancellationToken);

        var suggestions = new List<SplitSuggestionResponse>();
        foreach (var entry in entries)
        {
            if (Suggest(entry.Start, entry.Stop ?? now, entry.ProjectId, attention, links, projects, zone) is { } suggestion)
            {
                suggestions.Add(SplitSuggestionResponse.From(entry.Id, suggestion));
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Agent attention in [<paramref name="from"/>, <paramref name="to"/>) that none of the caller's entries, in any of
    /// their workspaces, nor <paramref name="pending"/> (proposals waiting for review) covers, day by day in the
    /// person's time zone, with the projects of this workspace it went to. As on the Mac, a stretch needs a prompt of
    /// its own: the minutes the logs assume after the last prompt before a stop are no sign of work nobody tracked.
    /// Folders outside git say nothing about a project, so they don't count.
    /// </summary>
    public async Task<List<UntrackedTimeResponse>> UntrackedAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IEnumerable<TimeRange> pending,
        CancellationToken cancellationToken)
    {
        var userId = workspace.UserId;
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AgentActivitySync, u.TimeZone })
            .SingleAsync(cancellationToken);
        if (!user.AgentActivitySync)
        {
            return [];
        }

        var zone = Zone(user.TimeZone);
        var now = timeProvider.GetUtcNow();
        var (since, until) = (StartOfDay(from, zone), StartOfDay(to - TimeSpan.FromTicks(1), zone, plusDays: 1));
        var (attention, links, projects) = await ActivityAsync(since, until, now, cancellationToken);
        var inGit = attention.Where(a => a.Repository is not null).ToList();
        if (inGit.Count == 0)
        {
            return [];
        }

        // Time tracked in another workspace was tracked too. An entry from the day before may run into the first day.
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.Start < until && e.Start >= since.AddDays(-1))
            .Select(e => new { e.Start, e.Stop })
            .ToListAsync(cancellationToken);
        var covered = entries.Select(e => new TimeRange(e.Start.ToUnixTimeMilliseconds(), (e.Stop ?? now).ToUnixTimeMilliseconds()))
            .Concat(pending)
            .ToList();
        var prompts = inGit.SelectMany(a => a.Prompts).ToList();

        var untracked = new List<UntrackedTimeResponse>();
        for (var day = since; day < until; day = StartOfDay(day, zone, plusDays: 1))
        {
            var bounds = new TimeRange(day.ToUnixTimeMilliseconds(), StartOfDay(day, zone, plusDays: 1).ToUnixTimeMilliseconds());
            foreach (var stretch in UntrackedActivity.Stretches(inGit, covered, bounds, now.ToUnixTimeMilliseconds()))
            {
                if (!prompts.Any(at => at >= stretch.Start && at <= stretch.End))
                {
                    continue;
                }

                // Each project's attention within it, most first, as the Mac names it.
                var blocks = UntrackedActivity.Merge(covered.Select(c => c.Intersect(stretch)).OfType<TimeRange>(), 0);
                var attended = inGit
                    .Select(a => (Project: links.ProjectFor(a.Repository!, projects), Range: new TimeRange(a.Start, a.End).Intersect(stretch)))
                    .Where(a => a.Range is { Duration: > 0 })
                    .GroupBy(a => a.Project ?? Guid.Empty)
                    .ToDictionary(g => g.Key, g => g.Sum(a => UntrackedActivity.Subtract(blocks, a.Range!.Value).Sum(r => r.Duration)));
                var projectIds = attended.Where(a => a.Key != Guid.Empty && a.Value > 0)
                    .OrderByDescending(a => a.Value).ThenBy(a => a.Key)
                    .Select(a => a.Key)
                    .ToList();
                var (start, end) = (SplitSuggestionResponse.Time(stretch.Start), SplitSuggestionResponse.Time(stretch.End));
                var suggestion = Suggest(start, end, projectIds.Count > 0 ? projectIds[0] : null, attention, links, projects, zone);
                var split = suggestion is null ? null : SplitSuggestionResponse.From(Guid.Empty, suggestion);
                untracked.Add(new UntrackedTimeResponse(
                    start,
                    end,
                    projectIds,
                    (long)Math.Round(attended.GetValueOrDefault(Guid.Empty) / 1000.0),
                    split?.Parts,
                    split?.Stretches));
            }
        }

        return untracked;
    }

    /// <summary>How far back "Agents now" in the project picker looks.</summary>
    public static readonly TimeSpan AgentsNowWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The projects of this workspace the caller's agents had attention in during the last <see cref="AgentsNowWindow"/>,
    /// placed by the same links as suggestions, the most recent first: "Agents now" in the project picker. Repositories
    /// without a project and folders outside git don't count.
    /// </summary>
    public async Task<List<AgentsNowResponse>> NowAsync(CancellationToken cancellationToken)
    {
        var sync = await db.Users.AsNoTracking().Where(u => u.Id == workspace.UserId).Select(u => u.AgentActivitySync).SingleAsync(cancellationToken);
        if (!sync)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var from = (now - AgentsNowWindow).ToUnixTimeMilliseconds();
        var (attention, links, projects) = await ActivityAsync(now - AgentsNowWindow, now, now, cancellationToken);
        return attention
            .Where(a => a.Repository is not null && a.End > from)
            .Select(a => (Project: links.ProjectFor(a.Repository!, projects), Start: Math.Max(a.Start, from), a.End))
            .Where(a => a.Project is not null)
            .GroupBy(a => a.Project!.Value)
            .Select(g => new AgentsNowResponse(g.Key, SplitSuggestionResponse.Time(g.Max(a => a.End)), (long)Math.Round(g.Sum(a => a.End - a.Start) / 1000.0)))
            .OrderByDescending(p => p.LastAt).ThenByDescending(p => p.Seconds).ThenBy(p => p.ProjectId)
            .ToList();
    }

    /// <summary>
    /// The person's attention over [<paramref name="from"/>, <paramref name="to"/>), and the links and projects to
    /// place it with, learned from the <see cref="LearningPeriod"/> before.
    /// </summary>
    private async Task<(List<AttentionSegment> Attention, RepositoryLinks Links, List<LinkableProject> Projects)> ActivityAsync(
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var userId = workspace.UserId;
        var since = from - LearningPeriod;
        var sessions = await db.AgentSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ParentSessionId == null && s.FirstAt < until
                && s.LastAt >= since.AddMilliseconds(-AttentionTimeline.IdleCap))
            .Select(s => new { s.SessionId, s.RepositoryIdentity, s.RepositoryName, s.Branches, s.Touches })
            .ToListAsync(cancellationToken);
        var touches = sessions.SelectMany(s =>
        {
            var repository = s.RepositoryIdentity is { } identity ? new AgentRepository(identity, s.RepositoryName ?? identity) : null;
            return s.Touches.Select(at => new AgentTouch(at, s.SessionId, repository, s.Branches));
        });
        var attention = AttentionTimeline.Build(touches, since.ToUnixTimeMilliseconds(), until.ToUnixTimeMilliseconds());

        var tracked = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == userId && e.Start >= since && e.Start < until && !e.IsSplit)
            .Select(e => new RepositoryLinks.Entry(e.Start.ToUnixTimeMilliseconds(), e.Stop.HasValue ? e.Stop.Value.ToUnixTimeMilliseconds() : null, e.ProjectId, e.IsSplit))
            .ToListAsync(cancellationToken);
        var chosenLinks = await db.AgentRepositoryLinks.AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToListAsync(cancellationToken);
        var links = RepositoryLinks.Of(chosenLinks, RepositoryLinks.Learn(tracked, attention, now.ToUnixTimeMilliseconds()));
        var projects = await policy.VisibleProjects().AsNoTracking()
            .Where(p => p.ArchivedAt == null)
            .OrderBy(p => p.Name)
            .Select(p => new LinkableProject(p.Id, p.Name))
            .ToListAsync(cancellationToken);
        return (attention, links, projects);
    }

    private static DateTimeZone Zone(string timeZone) => DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ?? DateTimeZone.Utc;

    /// <summary>
    /// The split suggested for time tracked from <paramref name="start"/> to <paramref name="stop"/> on
    /// <paramref name="projectId"/>. As on the Mac, it sees the attention of the days it starts and ends on, in the
    /// person's time zone, out of <paramref name="attention"/> built over a longer range.
    /// </summary>
    internal static SplitSuggestion? Suggest(
        DateTimeOffset start,
        DateTimeOffset stop,
        Guid? projectId,
        IReadOnlyList<AttentionSegment> attention,
        RepositoryLinks links,
        IReadOnlyList<LinkableProject> projects,
        DateTimeZone zone)
    {
        var window = Window(start, stop, zone);
        var (from, to) = (window.Start.ToUnixTimeMilliseconds(), window.End.ToUnixTimeMilliseconds());
        var seen = attention
            .Where(s => s.End > from && s.Start < to)
            .Select(s => s with { Start = Math.Max(s.Start, from), End = Math.Min(s.End, to) })
            .ToList();
        return SplitSuggestion.Suggest(
            start.ToUnixTimeMilliseconds(),
            stop.ToUnixTimeMilliseconds(),
            seen,
            projectId,
            segment => links.Place(segment, projects));
    }

    /// <summary>The days from <paramref name="start"/>'s to <paramref name="stop"/>'s, in the person's time zone.</summary>
    internal static (DateTimeOffset Start, DateTimeOffset End) Window(DateTimeOffset start, DateTimeOffset stop, DateTimeZone zone) =>
        (StartOfDay(start, zone), StartOfDay(stop, zone, plusDays: 1));

    private static DateTimeOffset StartOfDay(DateTimeOffset instant, DateTimeZone zone, int plusDays = 0) =>
        Instant.FromDateTimeOffset(instant).InZone(zone).Date.PlusDays(plusDays).AtStartOfDayInZone(zone).ToDateTimeOffset();
}
