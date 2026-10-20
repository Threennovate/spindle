using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.Workspaces;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.TimeEntries;

internal static class TimeEntryEndpoints
{
    // Device clocks drift; a timer that starts a few seconds "in the future" is still a timer started now.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxListRange = TimeSpan.FromDays(366);

    public static IEndpointRouteBuilder MapTimeEntryEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/time-entries").WithTags("Time entries");

        group.MapGet("", ListAsync).WithName("listTimeEntries");
        group.MapGet("/suggestions", SuggestAsync).WithName("suggestTimeEntries");
        group.MapGet("/descriptions", SuggestDescriptionsAsync).WithName("suggestDescriptions");
        group.MapPost("", (CreateTimeEntryRequest request, CurrentWorkspace workspace, AppDbContext db, TimeEntryReferences references,
                TimeEntryRules rules, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            // The apps always stop a timer elsewhere; only agents call CreateAsync without, never over HTTP.
            CreateAsync(request, workspace, db, references, rules, timeProvider, cancellationToken))
            .WithName("createTimeEntry").ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{entryId:guid}", GetAsync).WithName("getTimeEntry");
        group.MapPut("/{entryId:guid}", UpdateAsync).WithName("updateTimeEntry").ProducesEntryChanged();
        group.MapPost("/{entryId:guid}/stop", StopAsync).WithName("stopTimeEntry").AllowWhenReadOnly().ProducesEntryChanged();
        group.MapDelete("/{entryId:guid}", DeleteAsync).WithName("deleteTimeEntry").ProducesEntryChanged();
        // Bulk changes take no condition: people make them online, on the entries in front of them, and they change only
        // the fields named (tags are added and removed), so they don't put back an old copy of a whole entry.
        group.MapPost("/bulk-update", BulkUpdateAsync).WithName("bulkUpdateTimeEntries");
        group.MapPost("/bulk-delete", BulkDeleteAsync).WithName("bulkDeleteTimeEntries");

        return workspace;
    }

    /// <summary>An exact personal entry, including one moved to another day by a different device.</summary>
    private static async Task<Results<Ok<TimeEntryResponse>, NotFound>> GetAsync(
        Guid entryId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var entry = await FindOwnAsync(db, workspace, entryId, cancellationToken);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(TimeEntryResponse.From(entry));
    }

    /// <summary>The caller's running entry in any workspace; a user has at most one.</summary>
    public static IEndpointRouteBuilder MapCurrentTimeEntryEndpoint(this IEndpointRouteBuilder api)
    {
        api.MapGet("/me/time-entries/current", GetCurrentAsync)
            .WithName("getCurrentTimeEntry")
            .WithTags("Time entries")
            .RequireAuthorization();
        return api;
    }

    /// <summary>The caller's own entries that start within [from, to), newest first.</summary>
    private static async Task<Results<Ok<List<TimeEntryResponse>>, ValidationProblem>> ListAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (to <= from || to - from > MaxListRange)
        {
            return Problems.Validation("to", "The range must end after it starts and span at most a year.");
        }

        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && e.Start >= from && e.Start < to)
            .OrderByDescending(e => e.Start)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(entries.Select(TimeEntryResponse.From).ToList());
    }

    private static async Task<Results<Ok<TimeEntryResponse>, NoContent>> GetCurrentAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var running = await db.TimeEntries.AsNoTracking()
            .SingleOrDefaultAsync(e => e.UserId == userId && e.Stop == null, cancellationToken);

        return running is null ? TypedResults.NoContent() : TypedResults.Ok(TimeEntryResponse.From(running));
    }

    internal static async Task<Results<Created<TimeEntryResponse>, Ok<TimeEntryResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateTimeEntryRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        bool stopsTimerElsewhere = true)
    {
        if (request.Id is { } id)
        {
            var stored = await db.TimeEntries.AsNoTracking()
                .IgnoreQueryFilters([QueryFilters.SoftDelete])
                .SingleOrDefaultAsync(e => e.Id == id, cancellationToken);

            if (stored is not null)
            {
                // The same create sent again, e.g. after a lost response: answer as the first time did.
                return stored.UserId == workspace.UserId && stored.WorkspaceId == workspace.Id && stored.DeletedAt is null
                    ? TypedResults.Ok(TimeEntryResponse.From(stored))
                    : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "A time entry with this ID already exists.");
            }
        }

        var (details, errors) = await references.ResolveAsync(workspace.Id, request, existing: null, cancellationToken);
        if (details is null)
        {
            return Problems.Validation(errors);
        }

        var now = timeProvider.GetUtcNow();
        var start = request.Start!.Value;
        if (await rules.LockedAsync(start, cancellationToken) is { } locked)
        {
            return Problems.Validation("start", locked);
        }

        if (request.Stop is not null && await rules.MissingDetailsAsync(details, cancellationToken) is { Count: > 0 } missing)
        {
            return Problems.Validation(missing);
        }

        if (request.Stop is null)
        {
            if (start > now + ClockTolerance)
            {
                return Problems.Validation("start", "A timer cannot start in the future.");
            }

            // Starting a timer stops the one that is running, where the new one starts.
            var running = await db.TimeEntries.SingleOrDefaultAsync(e => e.UserId == workspace.UserId && e.Stop == null, cancellationToken);
            if (!stopsTimerElsewhere && running is not null && running.WorkspaceId != workspace.Id)
            {
                // Agents reach one workspace, so they don't stop a timer in another; the unique index keeps this check atomic.
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The timer runs in another workspace, which this agent can't reach. The person stops it in Spindle first.");
            }

            running?.StopAt(Max(running.Start, Min(start, now)));
        }

        var entry = TimeEntry.Create(request.Id, workspace.Id, workspace.UserId, details, start, request.Stop);
        db.TimeEntries.Add(entry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation("UX_TimeEntries_OneRunningPerUser"))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Another timer was started at the same moment.",
                detail: "Refresh to see the running timer, then try again.");
        }

        return TypedResults.Created((string?)null, TimeEntryResponse.From(entry));
    }

    /// <summary>Saves the entry's full state; with <c>If-Match</c>, only if it still has the version the client saw.</summary>
    internal static async Task<Results<Ok<TimeEntryResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid entryId,
        TimeEntryRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        var condition = EntryCondition.Parse(ifMatch);
        var entry = await FindOwnAsync(db, workspace, entryId, cancellationToken);
        if (entry is null)
        {
            return TypedResults.NotFound();
        }

        if (condition is not null && !condition.Allows(entry))
        {
            return EntryCondition.Changed(entry);
        }

        var (details, errors) = await references.ResolveAsync(workspace.Id, request, entry, cancellationToken);
        if (details is null)
        {
            return Problems.Validation(errors);
        }

        if (request.Stop is null && request.Start > timeProvider.GetUtcNow() + ClockTolerance)
        {
            return Problems.Validation("start", "A running timer cannot start in the future.");
        }

        // Neither where the entry was nor where it is moved to may be on a locked day.
        var locked = await rules.LockedAsync(entry.Start, cancellationToken) ?? await rules.LockedAsync(request.Start!.Value, cancellationToken);
        if (locked is not null)
        {
            return Problems.Validation("start", locked);
        }

        if (request.Stop is not null && await rules.MissingDetailsAsync(details, cancellationToken) is { Count: > 0 } missing)
        {
            return Problems.Validation(missing);
        }

        entry.Update(details, request.Start!.Value, request.Stop);
        if (await TrySaveAsync(db, entry, condition, cancellationToken) is { } changed)
        {
            return changed;
        }

        return TypedResults.Ok(TimeEntryResponse.From(entry));
    }

    /// <summary>
    /// Stops the timer. Stopping an entry that already stopped returns it unchanged, so retries are safe; with
    /// <c>If-Match</c>, only when it stopped at the time asked for.
    /// </summary>
    internal static async Task<Results<Ok<TimeEntryResponse>, NotFound, ValidationProblem, ProblemHttpResult>> StopAsync(
        Guid entryId,
        StopTimeEntryRequest? request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryRules rules,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        var condition = EntryCondition.Parse(ifMatch);
        var entry = await FindOwnAsync(db, workspace, entryId, cancellationToken);
        if (entry is null)
        {
            return TypedResults.NotFound();
        }

        if (condition is not null && !condition.Allows(entry))
        {
            // The same stop sent again after a lost answer finds the entry stopped where it asked.
            return !entry.IsRunning && request?.Stop is { } asked && SameInstant(asked, entry.Stop!.Value)
                ? TypedResults.Ok(TimeEntryResponse.From(entry))
                : EntryCondition.Changed(entry);
        }

        // A finished entry must have the workspace's required details, so the timer cannot stop without them.
        if (entry.IsRunning && await rules.MissingDetailsAsync(TimeEntryRules.DetailsOf(entry), cancellationToken) is { Count: > 0 } missing)
        {
            return Problems.Validation(missing);
        }

        if (entry.IsRunning)
        {
            entry.StopAt(request?.Stop ?? timeProvider.GetUtcNow());
            if (await TrySaveAsync(db, entry, condition, cancellationToken) is { } changed)
            {
                return changed;
            }
        }

        return TypedResults.Ok(TimeEntryResponse.From(entry));
    }

    /// <summary>Deletes the entry; with <c>If-Match</c>, only if it still has the version the client saw.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> DeleteAsync(
        Guid entryId,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryRules rules,
        CancellationToken cancellationToken,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        var condition = EntryCondition.Parse(ifMatch);
        var entry = await FindOwnAsync(db, workspace, entryId, cancellationToken);
        if (entry is null)
        {
            return TypedResults.NotFound();
        }

        if (condition is not null && !condition.Allows(entry))
        {
            return EntryCondition.Changed(entry);
        }

        if (await rules.LockedAsync(entry.Start, cancellationToken) is { } locked)
        {
            return Problems.Validation("start", locked);
        }

        db.TimeEntries.Remove(entry);
        if (await TrySaveAsync(db, entry, condition, cancellationToken) is { } changed)
        {
            return changed;
        }

        return TypedResults.NoContent();
    }

    /// <summary>Applies the same changes to several of the caller's entries, all or none.</summary>
    internal static async Task<Results<Ok<List<TimeEntryResponse>>, ValidationProblem>> BulkUpdateAsync(
        BulkUpdateTimeEntriesRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryReferences references,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        var entries = await FindOwnAsync(db, workspace, request.Ids, cancellationToken);
        if (entries is null)
        {
            return Problems.Validation("ids", "Some of the time entries do not exist.");
        }

        var addTagIds = request.AddTagIds?.Distinct().ToArray() ?? [];
        var removeTagIds = request.RemoveTagIds?.ToHashSet() ?? [];
        var check = await references.CheckAsync(
            workspace.Id,
            request.Project?.ProjectId,
            request.Project?.TaskId,
            addTagIds,
            existing: null,
            cancellationToken);
        if (check.Errors.Count > 0)
        {
            return Problems.Validation(check.Errors);
        }

        foreach (var entry in entries)
        {
            if (await rules.LockedAsync(entry.Start, cancellationToken) is { } locked)
            {
                return Problems.Validation("ids", locked);
            }

            // A new project ends a split; billable applies to every part of one, and a new description to all of them.
            var details = new TimeEntryDetails(
                request.Description ?? entry.Description,
                request.Project is null ? entry.ProjectId : request.Project.ProjectId,
                request.Project is null ? entry.TaskId : request.Project.TaskId,
                entry.TagIds.Union(addTagIds).Where(id => !removeTagIds.Contains(id)).ToArray(),
                request.IsBillable ?? entry.IsBillable,
                request.Project is null
                    ? entry.Shares.Select(share => share with
                    {
                        IsBillable = request.IsBillable ?? share.IsBillable,
                        Description = request.Description is null ? share.Description : null,
                    }).ToArray()
                    : null);
            if (!entry.IsRunning && await rules.MissingDetailsAsync(details, cancellationToken) is { Count: > 0 } missing)
            {
                return Problems.Validation(missing);
            }

            entry.Update(details, entry.Start, entry.Stop);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(entries.OrderByDescending(e => e.Start).Select(TimeEntryResponse.From).ToList());
    }

    private static async Task<Results<NoContent, ValidationProblem>> BulkDeleteAsync(
        BulkDeleteTimeEntriesRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeEntryRules rules,
        CancellationToken cancellationToken)
    {
        var entries = await FindOwnAsync(db, workspace, request.Ids, cancellationToken);
        if (entries is null)
        {
            return Problems.Validation("ids", "Some of the time entries do not exist.");
        }

        foreach (var entry in entries)
        {
            if (await rules.LockedAsync(entry.Start, cancellationToken) is { } locked)
            {
                return Problems.Validation("ids", locked);
            }
        }

        db.TimeEntries.RemoveRange(entries);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The caller's entries with these IDs, or null if any of them is missing or not theirs.</summary>
    private static async Task<List<TimeEntry>?> FindOwnAsync(AppDbContext db, CurrentWorkspace workspace, Guid[] ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        var entries = await db.TimeEntries
            .Where(e => distinct.Contains(e.Id) && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId)
            .ToListAsync(cancellationToken);
        return entries.Count == distinct.Length ? entries : null;
    }

    /// <summary>
    /// Recent distinct entries (description, project, task) whose description or project name contains the search,
    /// ranked as <see cref="DescriptionRanking"/> says. Entries whose project is archived or task is done are left
    /// out, as they cannot be started.
    /// </summary>
    private static async Task<Ok<List<TimeEntrySuggestion>>> SuggestAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        string? search = null,
        int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var activeProjects = await policy.VisibleProjects().AsNoTracking()
            .Where(p => p.ArchivedAt == null)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);
        var doneTaskIds = (await db.Tasks.AsNoTracking()
            .Where(t => t.WorkspaceId == workspace.Id && t.IsDone)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        var query = db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId && (e.Description != "" || e.ProjectId != null));

        if (term is not null)
        {
            var matchingProjectIds = activeProjects
                .Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .ToList();
            query = query.Where(e => EF.Functions.ILike(e.Description, LikePattern.Contains(term), "\\") || (e.ProjectId != null && matchingProjectIds.Contains(e.ProjectId.Value)));
        }

        var recent = await query
            .OrderByDescending(e => e.Start)
            .Take(500)
            .Select(e => new { e.Description, e.ProjectId, e.TaskId, e.TagIds, e.IsBillable, e.Shares, e.Start })
            .ToListAsync(cancellationToken);

        var activeProjectIds = activeProjects.Select(p => p.Id).ToHashSet();
        var startable = recent.Where(e => TimeShare.PartsOf(e.Shares, e.ProjectId, e.TaskId, e.IsBillable).All(p =>
            (p.ProjectId is null || activeProjectIds.Contains(p.ProjectId.Value)) && (p.TaskId is null || !doneTaskIds.Contains(p.TaskId.Value))));
        var suggestions = DescriptionRanking.Rank(
                startable,
                e => (e.Description, e.ProjectId, e.TaskId, Split: string.Join(',', e.Shares.Select(s => $"{s.ProjectId}/{s.TaskId}/{s.Percent}"))),
                e => e.Description,
                e => e.Start,
                term,
                timeProvider.GetUtcNow())
            .Take(limit)
            .Select(e => new TimeEntrySuggestion(e.Description, e.ProjectId, e.TaskId, e.TagIds, e.IsBillable, TimeShareResponse.From(e.Shares)))
            .ToList();

        return TypedResults.Ok(suggestions);
    }

    /// <summary>
    /// Descriptions the caller wrote before that contain the search, to complete the one being typed: their entries'
    /// and their split parts' own, from their recent entries, ranked as <see cref="DescriptionRanking"/> says, with
    /// those written on the given project first.
    /// </summary>
    private static async Task<Ok<List<string>>> SuggestDescriptionsAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        string? search = null,
        Guid? projectId = null,
        int limit = 8)
    {
        limit = Math.Clamp(limit, 1, 50);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        var recent = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId)
            .OrderByDescending(e => e.Start)
            .Take(1000)
            .Select(e => new { e.Description, e.ProjectId, e.TaskId, e.IsBillable, e.Shares, e.Start })
            .ToListAsync(cancellationToken);

        // A split described per project gives each part's description its own project; otherwise the entry's
        // description belongs to all its projects.
        var uses = recent.SelectMany(e => TimeShare.AreDescribed(e.Shares)
            ? e.Shares.Select(s => (Text: s.DescriptionOr(e.Description).Trim(), Projects: new[] { s.ProjectId }, e.Start))
            : [(Text: e.Description.Trim(), Projects: TimeShare.PartsOf(e.Shares, e.ProjectId, e.TaskId, e.IsBillable).Select(p => p.ProjectId).ToArray(), e.Start)])
            .Where(u => u.Text != "" && (term is null || u.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));

        var descriptions = DescriptionRanking.Rank(
                uses,
                u => u.Text.ToLowerInvariant(),
                u => u.Text,
                u => u.Start,
                term,
                timeProvider.GetUtcNow(),
                projectId is null ? null : u => u.Projects.Contains(projectId))
            .Select(u => u.Text)
            .Where(text => !string.Equals(text, term, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        return TypedResults.Ok(descriptions);
    }

    private static Task<TimeEntry?> FindOwnAsync(AppDbContext db, CurrentWorkspace workspace, Guid entryId, CancellationToken cancellationToken) =>
        db.TimeEntries.SingleOrDefaultAsync(
            e => e.Id == entryId && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId,
            cancellationToken);

    /// <summary>
    /// Saves a write to one entry. With a condition, a change saved elsewhere after the entry was read fails it as a
    /// stale version would, with the entry as that change left it (or 404 if it deleted it). Without one, as before.
    /// </summary>
    private static async Task<ProblemHttpResult?> TrySaveAsync(AppDbContext db, TimeEntry entry, EntryCondition? condition, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException) when (condition is not null)
        {
            db.ChangeTracker.Clear();
            var current = await db.TimeEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == entry.Id, cancellationToken);
            return current is null
                ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "The entry was deleted.")
                : EntryCondition.Changed(current);
        }
    }

    /// <summary>Equal as PostgreSQL stores them, to the microsecond.</summary>
    private static bool SameInstant(DateTimeOffset a, DateTimeOffset b) => a.UtcTicks / 10 == b.UtcTicks / 10;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
