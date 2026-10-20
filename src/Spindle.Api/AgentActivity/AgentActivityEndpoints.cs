using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.AgentActivity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.AgentActivity;

/// <summary>
/// Agent activity from the Mac app's reading of Claude Code and Codex logs, and the split suggestions made from it.
/// It is the person's own: everything here is under their account and only ever about their own entries, never
/// visible to admins or in reports.
/// </summary>
internal static class AgentActivityEndpoints
{
    private static readonly TimeSpan MaxSuggestionRange = TimeSpan.FromDays(62);

    // Unix milliseconds the apps may send: from 2000 to 2100.
    private const long EarliestTime = 946_684_800_000;
    private const long LatestTime = 4_102_444_800_000;

    public static IEndpointRouteBuilder MapAgentActivityEndpoints(this IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("/me/agent-activity").WithTags("Agent activity").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        me.MapGet("/settings", GetSettingsAsync).WithName("getAgentActivitySettings");
        me.MapPut("/settings", UpdateSettingsAsync).WithName("updateAgentActivitySettings");
        me.MapPut("/sessions", UploadAsync).WithName("uploadAgentSessions").ProducesProblem(StatusCodes.Status409Conflict);
        me.MapGet("/links", ListLinksAsync).WithName("listRepositoryLinks");
        me.MapGet("/dismissals", ListDismissalsAsync).WithName("listSplitSuggestionDismissals");

        return api;
    }

    public static IEndpointRouteBuilder MapSplitSuggestionEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("").WithTags("Agent activity");

        group.MapGet("/time-entries/split-suggestions", SuggestAsync).WithName("listSplitSuggestions");
        group.MapPost("/time-entries/{entryId:guid}/split-suggestion/dismiss", DismissAsync).WithName("dismissSplitSuggestion").AllowWhenReadOnly();
        group.MapPut("/agent-activity/links", LinkAsync).WithName("linkRepository").AllowWhenReadOnly();
        group.MapGet("/agent-activity/untracked", UntrackedAsync).WithName("listUntrackedTime");
        group.MapGet("/agent-activity/now", NowAsync).WithName("listAgentsNow");

        return workspace;
    }

    private static async Task<Ok<AgentActivitySettingsResponse>> GetSettingsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, cancellationToken);
        return TypedResults.Ok(AgentActivitySettingsResponse.From(user));
    }

    /// <summary>Turns sync on or off for all the person's Macs. Off deletes everything uploaded; titles off deletes the titles.</summary>
    private static async Task<Ok<AgentActivitySettingsResponse>> UpdateSettingsAsync(
        AgentActivitySettingsRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (request.Sync && !user.AgentActivitySync)
        {
            user.AgentActivitySyncedSince = timeProvider.GetUtcNow();
        }

        if (!request.Sync)
        {
            user.AgentActivitySyncedSince = null;
            await db.AgentSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        }
        else if (!request.Titles && user.AgentActivityTitles)
        {
            await db.AgentSessions.Where(s => s.UserId == userId && s.Title != null)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Title, (string?)null), cancellationToken);
        }
        else if (request.Titles && !user.AgentActivityTitles)
        {
            // Titles reach the server only when the Macs upload everything again.
            user.AgentActivitySyncedSince = timeProvider.GetUtcNow();
        }

        user.AgentActivitySync = request.Sync;
        user.AgentActivityTitles = request.Sync && request.Titles;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(AgentActivitySettingsResponse.From(user));
    }

    /// <summary>
    /// Keeps sessions a Mac or Windows PC read from the agents' logs, each replacing what that device uploaded for it before. Only while
    /// sync is on; titles only while they may be synced.
    /// </summary>
    private static async Task<Results<Ok<AgentActivityUploadResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        AgentActivityUploadRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.DeviceId == Guid.Empty)
        {
            return Problems.Validation("deviceId", "Name the device.");
        }

        var invalid = new Dictionary<string, string[]>();
        for (var i = 0; i < request.Sessions.Count; i++)
        {
            var session = request.Sessions[i];
            if (session.Touches.Any(t => t is < EarliestTime or > LatestTime)
                || session.Runs.Any(r => r.Start is < EarliestTime or > LatestTime || r.End < r.Start || r.End > LatestTime))
            {
                invalid[$"sessions[{i}]"] = ["Times must be Unix milliseconds, and runs must not end before they start."];
            }
            else if (session.Branches.Any(b => b.Length is 0 or > AgentSession.BranchMaxLength))
            {
                invalid[$"sessions[{i}].branches"] = [$"Branch names must have from 1 to {AgentSession.BranchMaxLength} characters."];
            }
        }

        if (request.Sessions.GroupBy(s => (s.Source, s.SessionId)).Any(g => g.Count() > 1))
        {
            invalid["sessions"] = ["Send each session once."];
        }

        if (invalid.Count > 0)
        {
            return Problems.Validation(invalid);
        }

        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.AgentActivitySync, u.AgentActivityTitles })
            .SingleAsync(cancellationToken);
        if (!user.AgentActivitySync)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent activity sync is off.");
        }

        var now = timeProvider.GetUtcNow();
        var ids = request.Sessions.Select(s => s.SessionId).ToList();
        var existing = await db.AgentSessions
            .Where(s => s.UserId == userId && s.DeviceId == request.DeviceId && ids.Contains(s.SessionId))
            .ToListAsync(cancellationToken);
        var saved = 0;
        foreach (var upload in request.Sessions)
        {
            var session = existing.SingleOrDefault(s => s.Source == upload.Source && s.SessionId == upload.SessionId);
            var touches = upload.Touches.Order().ToArray();
            var runs = upload.Runs.OrderBy(r => r.Start).ToList();
            var times = touches.Concat(runs.Select(r => r.Start)).Concat(runs.Select(r => r.End)).ToList();
            if (times.Count == 0)
            {
                // Nothing left to say about it, e.g. its prompts were all Spindle's own.
                if (session is not null)
                {
                    db.AgentSessions.Remove(session);
                }

                continue;
            }

            if (session is null)
            {
                session = new AgentSession { UserId = userId, DeviceId = request.DeviceId, Source = upload.Source, SessionId = upload.SessionId };
                db.AgentSessions.Add(session);
            }

            session.ParentSessionId = upload.ParentSessionId;
            session.RepositoryIdentity = upload.Repository?.Identity;
            session.RepositoryName = upload.Repository?.Name.Trim();
            session.Title = user.AgentActivityTitles && !string.IsNullOrWhiteSpace(upload.Title) ? upload.Title.Trim() : null;
            session.Branches = upload.Branches.Distinct().Order(StringComparer.Ordinal).ToArray();
            session.Touches = touches;
            session.RunStarts = runs.Select(r => r.Start).ToArray();
            session.RunEnds = runs.Select(r => r.End).ToArray();
            session.FirstAt = DateTimeOffset.FromUnixTimeMilliseconds(times.Min());
            session.LastAt = DateTimeOffset.FromUnixTimeMilliseconds(times.Max());
            session.ParserVersion = upload.ParserVersion;
            session.UploadedAt = now;
            saved++;
        }

        await db.SaveChangesAsync(cancellationToken);
        // As on the Mac, what is older than the retention goes.
        var oldest = now - AgentActivityLimits.Retention;
        await db.AgentSessions.Where(s => s.UserId == userId && s.LastAt < oldest).ExecuteDeleteAsync(cancellationToken);
        return TypedResults.Ok(new AgentActivityUploadResponse(saved));
    }

    /// <summary>
    /// The projects the person chose for their repositories, in every workspace, and with <paramref name="includeNoProject"/>
    /// the repositories they said go to no project. Apps that don't know those yet don't ask for them.
    /// </summary>
    private static async Task<Ok<List<RepositoryLinkResponse>>> ListLinksAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken,
        bool includeNoProject = false)
    {
        var userId = principal.GetUserId();
        var links = await (
            from link in db.AgentRepositoryLinks
            join project in db.Projects on link.ProjectId equals (Guid?)project.Id into linked
            from project in linked.DefaultIfEmpty()
            where link.UserId == userId && (project != null || (includeNoProject && link.ProjectId == null))
            orderby link.Name, link.Identity
            select new { link, WorkspaceId = project != null ? project.WorkspaceId : (Guid?)null })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(links
            .Select(l => new RepositoryLinkResponse(
                l.link.Identity, l.link.Name, l.link.ProjectId, l.WorkspaceId, l.link.BranchRules.Select(BranchRuleDto.From).ToList()))
            .ToList());
    }

    /// <summary>
    /// Links a repository to a project of this workspace from now on, or with no project forgets the link, unless
    /// <see cref="RepositoryLinkRequest.NoProject"/> says it goes to no project from now on. Branch rules, when sent,
    /// replace the link's: each names an active project of this workspace, and a task of that project.
    /// </summary>
    private static async Task<Results<NoContent, ValidationProblem>> LinkAsync(
        RepositoryLinkRequest request,
        CurrentWorkspace workspace,
        WorkspacePolicy policy,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var identity = request.Repository.Identity;
        var link = await db.AgentRepositoryLinks.SingleOrDefaultAsync(l => l.UserId == workspace.UserId && l.Identity == identity, cancellationToken);
        if (request.ProjectId is not { } projectId)
        {
            if (request.NoProject)
            {
                if (link is null)
                {
                    link = new AgentRepositoryLink { UserId = workspace.UserId, Identity = identity };
                    db.AgentRepositoryLinks.Add(link);
                }

                link.Name = request.Repository.Name.Trim();
                link.ProjectId = null;
                link.BranchRules = [];
                link.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }
            else if (link is not null)
            {
                db.AgentRepositoryLinks.Remove(link);
                await db.SaveChangesAsync(cancellationToken);
            }

            return TypedResults.NoContent();
        }

        if (request.NoProject)
        {
            return Problems.Validation("noProject", "Send a project or no project, not both.");
        }

        if (!await policy.VisibleProjects().AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, cancellationToken))
        {
            return Problems.Validation("projectId", "Choose an active project of this workspace.");
        }

        List<BranchRule>? rules = null;
        if (request.BranchRules is { } requested)
        {
            rules = requested.Select(r => new BranchRule(r.Pattern.Trim(), r.ProjectId, r.TaskId)).ToList();
            var invalid = new Dictionary<string, string[]>();
            var ruleProjects = rules.Select(r => r.ProjectId).Distinct().ToList();
            var active = await policy.VisibleProjects().Where(p => ruleProjects.Contains(p.Id) && p.ArchivedAt == null)
                .Select(p => p.Id).ToListAsync(cancellationToken);
            var ruleTasks = rules.Where(r => r.TaskId is not null).Select(r => r.TaskId!.Value).Distinct().ToList();
            var tasks = await db.Tasks.Where(t => ruleTasks.Contains(t.Id) && t.WorkspaceId == workspace.Id)
                .ToDictionaryAsync(t => t.Id, t => t.ProjectId, cancellationToken);
            for (var i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule.Pattern.Length == 0)
                {
                    invalid[$"branchRules[{i}].pattern"] = ["Name the branches, e.g. acme/*."];
                }
                else if (rules.Take(i).Any(r => string.Equals(r.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    invalid[$"branchRules[{i}].pattern"] = ["Each pattern can have one rule."];
                }

                if (!active.Contains(rule.ProjectId))
                {
                    invalid[$"branchRules[{i}].projectId"] = ["Choose an active project of this workspace."];
                }
                else if (rule.TaskId is { } taskId && tasks.GetValueOrDefault(taskId) != rule.ProjectId)
                {
                    invalid[$"branchRules[{i}].taskId"] = ["Choose a task of the rule's project."];
                }
            }

            if (invalid.Count > 0)
            {
                return Problems.Validation(invalid);
            }
        }

        if (link is null)
        {
            link = new AgentRepositoryLink { UserId = workspace.UserId, Identity = identity };
            db.AgentRepositoryLinks.Add(link);
        }

        link.Name = request.Repository.Name.Trim();
        link.ProjectId = projectId;
        if (rules is not null)
        {
            link.BranchRules = rules;
        }

        link.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Entries the person said not to suggest a split for, newest first.</summary>
    private static async Task<Ok<List<Guid>>> ListDismissalsAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var entryIds = await db.SplitSuggestionDismissals.AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(AgentActivityLimits.DismissalsListed)
            .Select(d => d.EntryId)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(entryIds);
    }

    /// <summary>Stops suggesting a split for one of the caller's entries, on all their devices ("Keep").</summary>
    private static async Task<Results<NoContent, NotFound>> DismissAsync(
        Guid entryId,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await db.TimeEntries.AnyAsync(e => e.Id == entryId && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (!await db.SplitSuggestionDismissals.AnyAsync(d => d.UserId == workspace.UserId && d.EntryId == entryId, cancellationToken))
        {
            db.SplitSuggestionDismissals.Add(new SplitSuggestionDismissal { UserId = workspace.UserId, EntryId = entryId, CreatedAt = timeProvider.GetUtcNow() });
            // Two devices saying it at once is still one dismissal.
            await db.TrySaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Splits agent activity suggests for the caller's own entries that start within [from, to): entries on one
    /// project whose time went to several. None while sync is off, and none for entries the caller kept as they are.
    /// </summary>
    private static async Task<Results<Ok<List<SplitSuggestionResponse>>, ValidationProblem>> SuggestAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        SplitSuggestions suggestions,
        CancellationToken cancellationToken)
    {
        if (to <= from || to - from > MaxSuggestionRange)
        {
            return Problems.Validation("to", $"The range must end after it starts and span at most {MaxSuggestionRange.TotalDays:0} days.");
        }

        return TypedResults.Ok(await suggestions.ForRangeAsync(from, to, cancellationToken));
    }

    /// <summary>
    /// Agent attention on the caller's days within [from, to) that none of their entries covers, nor a proposal waiting
    /// for their review, for the calendar to offer as entries. None while sync is off. Only ever the caller's own.
    /// </summary>
    private static async Task<Results<Ok<List<UntrackedTimeResponse>>, ValidationProblem>> UntrackedAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        SplitSuggestions suggestions,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (to <= from || to - from > MaxSuggestionRange)
        {
            return Problems.Validation("to", $"The range must end after it starts and span at most {MaxSuggestionRange.TotalDays:0} days.");
        }

        var proposals = (await AgentProposalEndpoints.ListAsync(workspace, db, references, rules, timeProvider, cancellationToken)).Value!;
        var pending = proposals.Pending.Select(p => p.Saves).OfType<TimeEntryRequest>()
            .Where(r => r.Start is not null && r.Stop is not null)
            .Select(r => new TimeRange(r.Start!.Value.ToUnixTimeMilliseconds(), r.Stop!.Value.ToUnixTimeMilliseconds()));
        return TypedResults.Ok(await suggestions.UntrackedAsync(from, to, pending, cancellationToken));
    }

    /// <summary>
    /// The projects of this workspace agents worked in during the last 15 minutes, by the caller's own activity and
    /// repository links, the most recent first. None while sync is off.
    /// </summary>
    private static async Task<Ok<List<AgentsNowResponse>>> NowAsync(SplitSuggestions suggestions, CancellationToken cancellationToken) =>
        TypedResults.Ok(await suggestions.NowAsync(cancellationToken));
}
