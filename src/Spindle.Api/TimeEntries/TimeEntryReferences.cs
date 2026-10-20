using Microsoft.EntityFrameworkCore;
using Spindle.Api.Workspaces;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.TimeEntries;

internal sealed record ReferenceCheck(Dictionary<string, string[]> Errors, bool? ProjectBillable);

/// <summary>
/// Checks that the project, task and tags of a time entry belong to its workspace. New choices must be active;
/// an entry may keep a project that was archived, or a task marked done, after it was tracked.
/// </summary>
internal sealed class TimeEntryReferences(AppDbContext db, WorkspacePolicy policy)
{
    public async Task<(TimeEntryDetails? Details, Dictionary<string, string[]> Errors)> ResolveAsync(
        Guid workspaceId,
        TimeEntryRequest request,
        TimeEntry? existing,
        CancellationToken cancellationToken)
    {
        var (details, errors) = await ResolveReferencesAsync(workspaceId, request, existing, cancellationToken);
        return (details is null ? null : WithPartDescriptions(request, details, existing), errors);
    }

    /// <summary>
    /// Whether the parts of a split have descriptions of their own. Apps that predate them leave the question out and
    /// send the entry's description as they were shown it, so the parts keep theirs, by project and task, until that
    /// description is changed; a new part then starts without one.
    /// </summary>
    private static TimeEntryDetails WithPartDescriptions(TimeEntryRequest request, TimeEntryDetails details, TimeEntry? existing)
    {
        if (details.Shares is not { Count: > 1 } shares)
        {
            return details;
        }

        if (request.DescribeParts is { } describe)
        {
            return details with { Shares = [.. shares.Select(s => s with { Description = describe ? s.Description ?? string.Empty : null })] };
        }

        var kept = existing is { IsSplit: true } && TimeShare.AreDescribed(existing.Shares)
            && (request.Description?.Trim() ?? string.Empty) == existing.Description
                ? existing.Shares.ToDictionary(s => (s.ProjectId, s.TaskId), s => s.Description)
                : null;
        return details with
        {
            Shares = [.. shares.Select(s => s with { Description = kept is null ? null : kept.GetValueOrDefault((s.ProjectId, s.TaskId)) ?? string.Empty })],
        };
    }

    private async Task<(TimeEntryDetails? Details, Dictionary<string, string[]> Errors)> ResolveReferencesAsync(
        Guid workspaceId,
        TimeEntryRequest request,
        TimeEntry? existing,
        CancellationToken cancellationToken)
    {
        var tagIds = request.TagIds?.Distinct().ToArray() ?? [];

        // Apps that predate splits send one project. While it is the one they were shown, the split stays.
        if (request.Shares is null && existing is { IsSplit: true }
            && request.ProjectId == existing.ProjectId && request.TaskId == existing.TaskId)
        {
            var kept = await CheckAsync(workspaceId, null, null, tagIds, existing, cancellationToken);
            return kept.Errors.Count > 0
                ? (null, kept.Errors)
                : (new TimeEntryDetails(request.Description, null, null, tagIds, false, existing.Shares), kept.Errors);
        }

        if (request.Shares is { Length: > 1 } requested)
        {
            return await ResolveSharesAsync(workspaceId, request, requested, tagIds, existing, cancellationToken);
        }

        var single = request.Shares is [var only]
            ? (only.ProjectId, only.TaskId, IsBillable: only.IsBillable ?? request.IsBillable)
            : (request.ProjectId, request.TaskId, request.IsBillable);
        var check = await CheckAsync(workspaceId, single.ProjectId, single.TaskId, tagIds, existing, cancellationToken);
        if (check.Errors.Count > 0)
        {
            return (null, check.Errors);
        }

        var isBillable = single.IsBillable ?? check.ProjectBillable ?? false;
        return (new TimeEntryDetails(request.Description, single.ProjectId, single.TaskId, tagIds, isBillable), check.Errors);
    }

    /// <summary>Checks each part as it would be checked alone; errors name the part, e.g. <c>shares[1].projectId</c>.</summary>
    private async Task<(TimeEntryDetails? Details, Dictionary<string, string[]> Errors)> ResolveSharesAsync(
        Guid workspaceId,
        TimeEntryRequest request,
        TimeShareRequest[] requested,
        Guid[] tagIds,
        TimeEntry? existing,
        CancellationToken cancellationToken)
    {
        var errors = (await CheckAsync(workspaceId, null, null, tagIds, existing, cancellationToken)).Errors;
        var shares = new List<TimeShare>();
        for (var i = 0; i < requested.Length; i++)
        {
            var part = requested[i];
            var check = await CheckAsync(workspaceId, part.ProjectId, part.TaskId, [], existing, cancellationToken);
            foreach (var (field, messages) in check.Errors)
            {
                errors[$"shares[{i}].{field}"] = messages;
            }

            shares.Add(new TimeShare(part.ProjectId, part.TaskId, part.IsBillable ?? check.ProjectBillable ?? false, part.Percent, part.Description));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new TimeEntryDetails(request.Description, null, null, tagIds, false, shares), errors);
    }

    public async Task<ReferenceCheck> CheckAsync(
        Guid workspaceId,
        Guid? projectId,
        Guid? taskId,
        IReadOnlyCollection<Guid> tagIds,
        TimeEntry? existing,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        bool? projectBillable = null;
        if (projectId is { } id)
        {
            // A private project the caller is not on does not exist for them.
            var project = await policy.VisibleProjects().AsNoTracking()
                .Where(p => p.Id == id && p.WorkspaceId == workspaceId)
                .Select(p => new { p.ArchivedAt, p.IsBillable })
                .SingleOrDefaultAsync(cancellationToken);

            if (project is null)
            {
                errors["projectId"] = ["The project does not exist."];
            }
            else if (project.ArchivedAt is not null && existing?.Parts.Any(p => p.ProjectId == id) != true)
            {
                errors["projectId"] = ["The project is archived."];
            }

            projectBillable = project?.IsBillable;
        }

        if (taskId is { } task)
        {
            var found = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == task && t.WorkspaceId == workspaceId)
                .Select(t => new { t.ProjectId, t.IsDone })
                .SingleOrDefaultAsync(cancellationToken);

            if (found is null || found.ProjectId != projectId)
            {
                errors["taskId"] = ["The task does not belong to the project."];
            }
            else if (found.IsDone && existing?.Parts.Any(p => p.TaskId == task) != true)
            {
                errors["taskId"] = ["The task is done."];
            }
        }

        if (tagIds.Count > 0)
        {
            var ids = tagIds.ToArray();
            var found = await db.Tags.CountAsync(t => t.WorkspaceId == workspaceId && ids.Contains(t.Id), cancellationToken);
            if (found != ids.Length)
            {
                errors["tagIds"] = ["One or more tags do not exist."];
            }
        }

        return new ReferenceCheck(errors, projectBillable);
    }
}
