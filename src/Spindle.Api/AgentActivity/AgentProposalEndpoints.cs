using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Common;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.AgentActivity;

/// <summary>
/// Entries and changes the person's agents propose, over MCP on their computer or the server's, waiting for the person
/// to add, edit or discard them in any app. They are the person's own: only ever their own entries, never shown to
/// admins or in reports.
/// </summary>
internal static class AgentProposalEndpoints
{
    // Device clocks drift, as when starting a timer.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapAgentProposalEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/agent-proposals").WithTags("Agent proposals");

        group.MapGet("", ListAsync).WithName("listAgentProposals");
        group.MapPost("/entries", ProposeEntriesAsync).WithName("proposeEntries");
        group.MapPost("/changes", ProposeChangesAsync).WithName("proposeChanges");
        group.MapPost("/withdraw", WithdrawAsync).WithName("withdrawAgentProposals");
        group.MapPost("/{proposalId:guid}/apply", ApplyAsync).WithName("applyAgentProposal").ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{proposalId:guid}/resolve", ResolveAsync).WithName("resolveAgentProposal").ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{proposalId:guid}/reopen", ReopenAsync).WithName("reopenAgentProposal").ProducesProblem(StatusCodes.Status409Conflict);

        return workspace;
    }

    /// <summary>What waits, each with what saving it would store and what stands in its way, and what became of the rest lately.</summary>
    internal static async Task<Ok<AgentProposalsResponse>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await ForgetOldAsync(db, workspace, now, cancellationToken);
        var proposals = await db.AgentProposals.AsNoTracking()
            .Where(p => p.UserId == workspace.UserId && p.WorkspaceId == workspace.Id)
            .OrderBy(p => p.ProposedAt)
            .ToListAsync(cancellationToken);
        var waiting = proposals.Where(p => p.IsWaiting(now)).ToList();
        var zone = await ZoneAsync(db, workspace, cancellationToken);

        var entryIds = waiting.Select(p => p.EntryId).OfType<Guid>().ToArray();
        var changed = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && entryIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var pending = new List<AgentProposalResponse>();
        var saving = waiting.ToDictionary(p => p.Id, p => p.EntryId is { } id && !changed.ContainsKey(id)
            ? null
            : AgentProposalRules.Request(p, p.EntryId is { } entryId ? changed[entryId] : null));
        var ranges = saving.Values.OfType<CreateTimeEntryRequest>().Select(r => (Start: r.Start!.Value, Stop: r.Stop ?? now)).ToList();
        var nearby = new List<TimeEntry>();
        if (ranges.Count > 0)
        {
            var (from, to) = (ranges.Min(r => r.Start), ranges.Max(r => r.Stop));
            nearby = await db.TimeEntries.AsNoTracking()
                .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && e.Start < to && (e.Stop == null || e.Stop > from))
                .ToListAsync(cancellationToken);
        }

        foreach (var proposal in waiting)
        {
            var current = proposal.EntryId is { } id ? changed.GetValueOrDefault(id) : null;
            if (saving[proposal.Id] is not { } request)
            {
                pending.Add(AgentProposalResponse.From(proposal, null, ["The entry was deleted."]));
                continue;
            }

            var blocking = new List<string>();
            var (details, errors) = await references.ResolveAsync(workspace.Id, request, current, cancellationToken);
            blocking.AddRange(errors.Values.SelectMany(messages => messages));
            var locked = (current is null ? null : await rules.LockedAsync(current.Start, cancellationToken))
                ?? await rules.LockedAsync(request.Start!.Value, cancellationToken);
            if (locked is not null)
            {
                blocking.Add(locked);
            }

            if (details is not null && request.Stop is not null)
            {
                blocking.AddRange((await rules.MissingDetailsAsync(details, cancellationToken)).Values.SelectMany(messages => messages));
            }

            if (request.Stop > now + ClockTolerance)
            {
                blocking.Add("It ends in the future.");
            }

            var warnings = new List<string>();
            var (start, stop) = (request.Start!.Value, request.Stop ?? now);
            foreach (var other in nearby.Where(e => e.Id != current?.Id && e.Start < stop && (e.Stop ?? now) > start))
            {
                var range = $"{Clock(other.Start, zone)}–{(other.Stop is { } end ? Clock(end, zone) : "now")}";
                warnings.Add(string.IsNullOrWhiteSpace(other.Description) ? $"Overlaps an entry, {range}." : $"Overlaps “{other.Description}”, {range}.");
            }

            var others = waiting.Where(p => p.Id != proposal.Id && saving[p.Id] is { } r && r.Start < stop && (r.Stop ?? now) > start).ToList();
            if (others.Count > 0)
            {
                warnings.Add(others.Count == 1 ? "Overlaps another proposal." : $"Overlaps {others.Count} other proposals.");
            }

            if (current is not null && proposal.Original is { } original && !original.SameAs(current))
            {
                warnings.Add("The entry changed after this was proposed.");
            }

            pending.Add(AgentProposalResponse.From(proposal, request with { Id = null }, blocking.Distinct().ToList(), warnings));
        }

        var outcomes = proposals
            .Select(p => p.IsExpired(now)
                ? new ProposalOutcomeResponse(p.Id, "expired", null, p.ProposedAt + AgentProposal.Lifetime)
                : p.Status is AgentProposalStatus.Added or AgentProposalStatus.Changed or AgentProposalStatus.Discarded
                    ? new ProposalOutcomeResponse(p.Id, p.Status.ToString().ToLowerInvariant(), p.ResultEntryId, p.ResolvedAt!.Value)
                    : null)
            .OfType<ProposalOutcomeResponse>()
            .Where(o => now - o.At <= AgentProposal.Lifetime)
            .OrderByDescending(o => o.At)
            .ToList();

        // Those proposed in one call share their time, so they go by when they start.
        var ordered = pending.OrderBy(p => p.ProposedAt).ThenBy(p => p.Saves?.Start ?? p.Original?.Start).ToList();
        return TypedResults.Ok(new AgentProposalsResponse(ordered, outcomes));
    }

    internal static async Task<Ok<ProposalPlanResponse>> ProposeEntriesAsync(
        ProposeEntriesRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var context = await ContextAsync(db, workspace, policy, timeProvider, request.Client, [], cancellationToken);
        // The same time already tracked: entries around the times asked for, read once.
        var times = request.Entries
            .SelectMany(e => new[] { AgentProposalRules.Date(e.Start, context.Zone), AgentProposalRules.Date(e.Stop, context.Zone) })
            .OfType<DateTimeOffset>()
            .ToList();
        if (times.Count > 0)
        {
            var (from, to) = (times.Min(), times.Max());
            context = context with
            {
                Entries = await db.TimeEntries.AsNoTracking()
                    .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && e.Start >= from && e.Start <= to)
                    .ToListAsync(cancellationToken),
            };
        }

        var plan = AgentProposalRules.PlanEntries(request.Entries, context);
        db.AgentProposals.AddRange(plan.Proposals);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Answer(plan));
    }

    internal static async Task<Ok<ProposalPlanResponse>> ProposeChangesAsync(
        ProposeChangesRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ids = request.Changes.Select(c => Guid.TryParse(c.Id, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && ids.Contains(e.Id))
            .ToListAsync(cancellationToken);
        var context = await ContextAsync(db, workspace, policy, timeProvider, request.Client, entries, cancellationToken);

        var plan = AgentProposalRules.PlanChanges(request.Changes, context);
        // A newer change for an entry replaces the one waiting.
        var replaced = plan.Proposals.Select(p => p.EntryId).ToHashSet();
        foreach (var old in context.Pending.Where(p => p.EntryId is not null && replaced.Contains(p.EntryId)))
        {
            old.Resolve(AgentProposalStatus.Withdrawn, null, context.Now);
        }

        db.AgentProposals.AddRange(plan.Proposals);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(Answer(plan));
    }

    /// <summary>Takes back proposals still waiting, for the agent that made them.</summary>
    internal static async Task<Ok<WithdrawAgentProposalsResponse>> WithdrawAsync(
        WithdrawAgentProposalsRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var proposals = await db.AgentProposals
            .Where(p => p.UserId == workspace.UserId && p.WorkspaceId == workspace.Id && request.Ids.Contains(p.Id) && p.Status == AgentProposalStatus.Pending)
            .ToListAsync(cancellationToken);
        var taken = proposals.Where(p => p.IsWaiting(now)).ToList();
        foreach (var proposal in taken)
        {
            proposal.Resolve(AgentProposalStatus.Withdrawn, null, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new WithdrawAgentProposalsResponse([.. taken.Select(p => p.Id)]));
    }

    /// <summary>Saves the proposal as it is, through the same checks as saving the entry in any app.</summary>
    private static async Task<Results<Ok<AppliedAgentProposalResponse>, NotFound, ValidationProblem, ProblemHttpResult>> ApplyAsync(
        Guid proposalId,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var proposal = await FindOwnAsync(db, workspace, proposalId, cancellationToken);
        if (proposal is null)
        {
            return TypedResults.NotFound();
        }

        if (!proposal.IsWaiting(now))
        {
            return NotWaiting();
        }

        // Marked first, so that saving the entry saves the answer with it, or neither.
        if (proposal.EntryId is not { } entryId)
        {
            var request = AgentProposalRules.Request(proposal, null) with { Id = Guid.NewGuid() };
            proposal.Resolve(AgentProposalStatus.Added, request.Id, now);
            var created = await TimeEntryEndpoints.CreateAsync(request, workspace, db, references, rules, timeProvider, cancellationToken);
            return created.Result switch
            {
                Created<TimeEntryResponse> saved => TypedResults.Ok(new AppliedAgentProposalResponse(proposal.Id, saved.Value!)),
                Ok<TimeEntryResponse> saved => TypedResults.Ok(new AppliedAgentProposalResponse(proposal.Id, saved.Value!)),
                ValidationProblem problem => problem,
                ProblemHttpResult problem => problem,
                _ => throw new InvalidOperationException("Unexpected result."),
            };
        }

        var current = await db.TimeEntries.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == entryId && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId, cancellationToken);
        if (current is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The entry was deleted.");
        }

        proposal.Resolve(AgentProposalStatus.Changed, entryId, now);
        var updated = await TimeEntryEndpoints.UpdateAsync(entryId, AgentProposalRules.Request(proposal, current), workspace, db, references, rules, timeProvider, cancellationToken);
        return updated.Result switch
        {
            Ok<TimeEntryResponse> saved => TypedResults.Ok(new AppliedAgentProposalResponse(proposal.Id, saved.Value!)),
            ValidationProblem problem => problem,
            NotFound => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The entry was deleted."),
            _ => throw new InvalidOperationException("Unexpected result."),
        };
    }

    /// <summary>Records what the person did with a proposal outside <c>apply</c>: saved it after editing, or discarded it.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> ResolveAsync(
        Guid proposalId,
        ResolveAgentProposalRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var proposal = await FindOwnAsync(db, workspace, proposalId, cancellationToken);
        if (proposal is null)
        {
            return TypedResults.NotFound();
        }

        if (!proposal.IsWaiting(now))
        {
            return NotWaiting();
        }

        if (request.Outcome == "discarded")
        {
            proposal.Resolve(AgentProposalStatus.Discarded, null, now);
        }
        else
        {
            var entryId = request.EntryId ?? proposal.EntryId;
            if (entryId is null || !await db.TimeEntries.AnyAsync(e => e.Id == entryId && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId, cancellationToken))
            {
                return Problems.Validation("entryId", "Name the entry that was saved.");
            }

            proposal.Resolve(proposal.IsNew ? AgentProposalStatus.Added : AgentProposalStatus.Changed, entryId, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>Puts a proposal back to wait, when the person takes back what saving or discarding it did.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ReopenAsync(
        Guid proposalId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var proposal = await FindOwnAsync(db, workspace, proposalId, cancellationToken);
        if (proposal is null)
        {
            return TypedResults.NotFound();
        }

        if (proposal.Status is not (AgentProposalStatus.Added or AgentProposalStatus.Changed or AgentProposalStatus.Discarded))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The proposal was not saved or discarded.");
        }

        proposal.Reopen();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ProposalPlanResponse Answer(AgentProposalRules.Plan plan) => new(
        [.. plan.Proposals.Select(p => AgentProposalResponse.From(p))],
        [.. plan.Refused.Select(r => new ProposalRefusalResponse(r.Index, r.Id, r.Reason))]);

    private static ProblemHttpResult NotWaiting() =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "The proposal is no longer waiting.");

    private static Task<AgentProposal?> FindOwnAsync(AppDbContext db, CurrentWorkspace workspace, Guid proposalId, CancellationToken cancellationToken) =>
        db.AgentProposals.SingleOrDefaultAsync(p => p.Id == proposalId && p.WorkspaceId == workspace.Id && p.UserId == workspace.UserId, cancellationToken);

    private static async Task<AgentProposalRules.Context> ContextAsync(
        AppDbContext db,
        CurrentWorkspace workspace,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        string? client,
        IReadOnlyList<TimeEntry> entries,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await ForgetOldAsync(db, workspace, now, cancellationToken);
        var zone = await ZoneAsync(db, workspace, cancellationToken);
        var projects = await policy.VisibleProjects().AsNoTracking().ToListAsync(cancellationToken);
        var tasks = await db.Tasks.AsNoTracking().Where(t => t.WorkspaceId == workspace.Id).ToListAsync(cancellationToken);
        var pending = (await db.AgentProposals
                .Where(p => p.UserId == workspace.UserId && p.WorkspaceId == workspace.Id && p.Status == AgentProposalStatus.Pending)
                .ToListAsync(cancellationToken))
            .Where(p => p.IsWaiting(now))
            .ToList();
        // Members can't add or change time before the lock date, in their own calendar; admins can.
        var lockedBefore = policy.IsAdmin ? null : (await policy.SettingsAsync(cancellationToken)).LockedBefore;
        string? Locked(DateTimeOffset start) =>
            lockedBefore is { } before && Instant.FromDateTimeOffset(start).InZone(zone).Date.ToDateOnly() < before ? "locked" : null;
        var byId = entries.ToDictionary(e => e.Id);

        return new AgentProposalRules.Context(
            workspace.Id,
            workspace.UserId,
            zone,
            projects,
            tasks,
            id => byId.GetValueOrDefault(id),
            entries,
            pending,
            Locked,
            now,
            string.IsNullOrWhiteSpace(client) ? null : client.Trim());
    }

    /// <summary>Rows go a week after they were dealt with or expired; nothing shows them any more.</summary>
    private static Task ForgetOldAsync(AppDbContext db, CurrentWorkspace workspace, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dealt = now - AgentProposal.Lifetime;
        var expired = now - (AgentProposal.Lifetime + AgentProposal.Lifetime);
        return db.AgentProposals
            .Where(p => p.UserId == workspace.UserId
                && ((p.Status != AgentProposalStatus.Pending && p.ResolvedAt < dealt) || (p.Status == AgentProposalStatus.Pending && p.ProposedAt < expired)))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<DateTimeZone> ZoneAsync(AppDbContext db, CurrentWorkspace workspace, CancellationToken cancellationToken)
    {
        var id = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
        return DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? DateTimeZone.Utc;
    }

    private static string Clock(DateTimeOffset instant, DateTimeZone zone) =>
        Instant.FromDateTimeOffset(instant).InZone(zone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
