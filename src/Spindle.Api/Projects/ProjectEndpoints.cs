using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Billing;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Projects;

/// <param name="HourlyRate">The rate as last set. Shown to admins only.</param>
/// <param name="SeparateBilledTime">
/// Whether billed time is worked out apart from the time tracked: <see cref="BillingMode"/> other than as tracked.
/// Kept for apps that predate the billing mode.
/// </param>
/// <param name="DailyHours">For projects billed by the full day: how long a full day is, as last set.</param>
/// <param name="CanManage">
/// Whether the caller may change the project, its tasks and members, and enter its billed time: admins and project managers.
/// </param>
public sealed record ProjectResponse(
    Guid Id,
    string Name,
    Guid? ClientId,
    string Color,
    bool IsPrivate,
    bool IsBillable,
    bool SeparateBilledTime,
    long? EstimateSeconds,
    DateTimeOffset? ArchivedAt,
    long TrackedSeconds,
    IReadOnlyList<TaskResponse> Tasks,
    decimal? HourlyRate,
    bool CanManage,
    BillingMode BillingMode,
    decimal? DailyHours);

/// <param name="AgreementId">The agreement with the client that time on the task counts towards, if any.</param>
public sealed record TaskResponse(Guid Id, Guid ProjectId, string Name, long? EstimateSeconds, bool IsDone, long TrackedSeconds, Guid? AgreementId);

public sealed record ProjectRequest
{
    [Required]
    [StringLength(Project.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    public Guid? ClientId { get; init; }

    /// <summary>Hex color such as <c>#6D28D9</c>.</summary>
    [Required]
    [RegularExpression("^#[0-9A-Fa-f]{6}$")]
    public string Color { get; init; } = string.Empty;

    public bool IsPrivate { get; init; }

    public bool IsBillable { get; init; }

    [Range(1, 360_000_000)]
    public long? EstimateSeconds { get; init; }

    /// <summary>What time on the project is billed at. Only admins can set it; for others it stays as it is.</summary>
    [Range(0, 1_000_000)]
    public decimal? HourlyRate { get; init; }

    /// <summary>
    /// The first day a changed rate applies to; earlier time keeps the rate it had. Left out, the current rate is
    /// corrected for all the time it covers.
    /// </summary>
    public DateOnly? HourlyRateFrom { get; init; }

    /// <summary>
    /// For apps that predate <see cref="BillingMode"/>: true bills day by day (keeping a full-day mode), false as
    /// tracked. Ignored when the billing mode is given; left out with it, the mode stays as it is.
    /// </summary>
    public bool? SeparateBilledTime { get; init; }

    /// <summary>How billed time is worked out. Left out, the mode and daily hours stay as they are.</summary>
    public BillingMode? BillingMode { get; init; }

    /// <summary>With the billing mode: how long a full day is, in hours, e.g. 8 or 7.5. Required to bill by the full day.</summary>
    [Range(0, 24)]
    public decimal? DailyHours { get; init; }

    /// <summary>The first day changed daily hours apply to; earlier days keep theirs. Left out, they are corrected.</summary>
    public DateOnly? DailyHoursFrom { get; init; }
}

public sealed record TaskRequest
{
    [Required]
    [StringLength(ProjectTask.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Range(1, 360_000_000)]
    public long? EstimateSeconds { get; init; }

    public bool IsDone { get; init; }
}

internal static class ProjectEndpoints
{
    private const string ProjectNameTaken = "A project with this name already exists for this client.";
    private const string TaskNameTaken = "A task with this name already exists in this project.";
    private const string CannotManage = "Only admins and the project's managers can change it.";

    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder workspace)
    {
        var projects = workspace.MapGroup("/projects").WithTags("Projects");

        projects.MapGet("", ListAsync).WithName("listProjects");
        projects.MapGet("/{projectId:guid}", GetAsync).WithName("getProject");
        projects.MapPost("", CreateAsync).WithName("createProject");
        projects.MapPut("/{projectId:guid}", UpdateAsync).WithName("updateProject");
        projects.MapPost("/{projectId:guid}/archive", ArchiveAsync).WithName("archiveProject");
        projects.MapPost("/{projectId:guid}/restore", RestoreAsync).WithName("restoreProject");
        projects.MapDelete("/{projectId:guid}", DeleteAsync).WithName("deleteProject");

        var tasks = projects.MapGroup("/{projectId:guid}/tasks").WithTags("Projects");
        tasks.MapPost("", CreateTaskAsync).WithName("createTask");
        tasks.MapPut("/{taskId:guid}", UpdateTaskAsync).WithName("updateTask");
        tasks.MapDelete("/{taskId:guid}", DeleteTaskAsync).WithName("deleteTask");

        return workspace;
    }

    private static async Task<Ok<List<ProjectResponse>>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken,
        bool includeArchived = false)
    {
        var projects = await policy.VisibleProjects().AsNoTracking()
            .Where(p => includeArchived || p.ArchivedAt == null)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(await ToResponsesAsync(db, policy, rates, workspace.Id, projects, cancellationToken));
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound>> GetAsync(
        Guid projectId,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var project = await policy.VisibleProjects().AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);

        return project is null
            ? TypedResults.NotFound()
            : TypedResults.Ok((await ToResponsesAsync(db, policy, rates, workspace.Id, [project], cancellationToken))[0]);
    }

    private static async Task<Results<Created<ProjectResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ProjectRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        if (!await policy.CanCreateProjectsAsync(cancellationToken))
        {
            return WorkspacePolicy.Forbidden("Only admins can create projects in this workspace.");
        }

        if (await ValidateClientAsync(db, workspace.Id, request.ClientId, currentClientId: null, cancellationToken) is { } clientError)
        {
            return clientError;
        }

        if (MissingDailyHours(request) is { } missing)
        {
            return missing;
        }

        var project = Project.Create(workspace.Id, ToDetails(request, ModeFor(request, BillingMode.AsTracked)));
        db.Projects.Add(project);
        // Whoever creates a project manages it, so a member keeps access to a private project they created.
        db.ProjectMembers.Add(ProjectMember.Create(workspace.Id, project.Id, workspace.UserId, isManager: true));
        await SetDailyHoursAsync(project, request, rates, cancellationToken);
        if (policy.IsAdmin)
        {
            await rates.SetAsync(RateScope.Project(project.Id), request.HourlyRate, request.HourlyRateFrom, "hourlyRate", "hourlyRateFrom", cancellationToken);
        }

        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", ProjectNameTaken);
        }

        return TypedResults.Created((string?)null, (await ToResponsesAsync(db, policy, rates, workspace.Id, [project], cancellationToken))[0]);
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid projectId,
        ProjectRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, policy, projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        if (await ValidateClientAsync(db, workspace.Id, request.ClientId, project.ClientId, cancellationToken) is { } clientError)
        {
            return clientError;
        }

        if (MissingDailyHours(request) is { } missing)
        {
            return missing;
        }

        project.Update(ToDetails(request, ModeFor(request, project.BillingMode)));
        await SetDailyHoursAsync(project, request, rates, cancellationToken);
        // Rates are admin business: for anyone else the project keeps the rate it had.
        if (policy.IsAdmin)
        {
            await rates.SetAsync(RateScope.Project(project.Id), request.HourlyRate, request.HourlyRateFrom, "hourlyRate", "hourlyRateFrom", cancellationToken);
        }

        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", ProjectNameTaken);
        }

        return TypedResults.Ok((await ToResponsesAsync(db, policy, rates, workspace.Id, [project], cancellationToken))[0]);
    }

    private static Task<Results<Ok<ProjectResponse>, NotFound, ProblemHttpResult>> ArchiveAsync(
        Guid projectId,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeAsync(db, policy, rates, workspace.Id, projectId, project => project.Archive(timeProvider.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<ProjectResponse>, NotFound, ProblemHttpResult>> RestoreAsync(
        Guid projectId,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken) =>
        ChangeAsync(db, policy, rates, workspace.Id, projectId, project => project.Restore(), cancellationToken);

    /// <summary>Deletes the project, its tasks and agreements. The tracked time stays, without a project.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid projectId,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(db, policy, projectId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        var deleted = await db.InTransactionAsync(async ct =>
        {
            var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == projectId && p.WorkspaceId == workspace.Id, ct);
            if (project is null)
            {
                return false;
            }

            // Set-based: a project can have many entries. UpdatedAt is set here because this bypasses the interceptor.
            var now = timeProvider.GetUtcNow();
            await db.TimeEntries
                .Where(e => e.ProjectId == projectId && !e.IsSplit)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(e => e.ProjectId, (Guid?)null).SetProperty(e => e.TaskId, (Guid?)null).SetProperty(e => e.UpdatedAt, now),
                    ct);

            // Split entries lose that part's project one by one: the parts are in each entry's own row.
            foreach (var entry in (await db.TimeEntries.Where(e => e.WorkspaceId == workspace.Id && e.IsSplit).ToListAsync(ct))
                .Where(e => e.Shares.Any(s => s.ProjectId == projectId)))
            {
                entry.RemoveProject(projectId);
            }

            db.Tasks.RemoveRange(await db.Tasks.Where(t => t.ProjectId == projectId).ToListAsync(ct));
            db.Agreements.RemoveRange(await db.Agreements.Where(a => a.ProjectId == projectId).ToListAsync(ct));
            db.ProjectMembers.RemoveRange(await db.ProjectMembers.Where(m => m.ProjectId == projectId).ToListAsync(ct));
            db.Rates.RemoveRange(await db.Rates.Where(r => r.ProjectId == projectId).ToListAsync(ct));
            db.BilledTimes.RemoveRange(await db.BilledTimes.Where(b => b.ProjectId == projectId).ToListAsync(ct));
            db.Projects.Remove(project);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Created<TaskResponse>, NotFound, ValidationProblem, ProblemHttpResult>> CreateTaskAsync(
        Guid projectId,
        TaskRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(db, policy, projectId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        var task = ProjectTask.Create(workspace.Id, projectId, request.Name, ToEstimate(request.EstimateSeconds));
        if (request.IsDone)
        {
            task.Update(task.Name, task.Estimate, isDone: true);
        }

        db.Tasks.Add(task);
        return await db.TrySaveChangesAsync(cancellationToken)
            ? TypedResults.Created((string?)null, ToResponse(task, trackedSeconds: 0))
            : Problems.Validation("name", TaskNameTaken);
    }

    private static async Task<Results<Ok<TaskResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateTaskAsync(
        Guid projectId,
        Guid taskId,
        TaskRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(db, policy, projectId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        var task = await db.Tasks.SingleOrDefaultAsync(
            t => t.Id == taskId && t.ProjectId == projectId && t.WorkspaceId == workspace.Id,
            cancellationToken);
        if (task is null)
        {
            return TypedResults.NotFound();
        }

        task.Update(request.Name, ToEstimate(request.EstimateSeconds), request.IsDone);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", TaskNameTaken);
        }

        var tracked = await TrackedSecondsByTaskAsync(db, workspace.Id, await SplitPartsAsync(db, workspace.Id, cancellationToken), cancellationToken);
        return TypedResults.Ok(ToResponse(task, tracked.GetValueOrDefault(task.Id)));
    }

    /// <summary>Deletes the task. Its time entries stay on the project, without a task.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteTaskAsync(
        Guid projectId,
        Guid taskId,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(db, policy, projectId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        var deleted = await db.InTransactionAsync(async ct =>
        {
            var task = await db.Tasks.SingleOrDefaultAsync(
                t => t.Id == taskId && t.ProjectId == projectId && t.WorkspaceId == workspace.Id,
                ct);
            if (task is null)
            {
                return false;
            }

            var now = timeProvider.GetUtcNow();
            await db.TimeEntries
                .Where(e => e.TaskId == taskId && !e.IsSplit)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.TaskId, (Guid?)null).SetProperty(e => e.UpdatedAt, now), ct);

            foreach (var entry in (await db.TimeEntries.Where(e => e.WorkspaceId == workspace.Id && e.IsSplit).ToListAsync(ct))
                .Where(e => e.Shares.Any(s => s.TaskId == taskId)))
            {
                entry.RemoveTask(taskId);
            }

            db.Tasks.Remove(task);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<ProjectResponse>, NotFound, ProblemHttpResult>> ChangeAsync(
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        Guid workspaceId,
        Guid projectId,
        Action<Project> change,
        CancellationToken cancellationToken)
    {
        var project = await FindAsync(db, policy, projectId, cancellationToken);
        if (project is null)
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        change(project);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await ToResponsesAsync(db, policy, rates, workspaceId, [project], cancellationToken))[0]);
    }

    /// <summary>A new client must exist and be active; a project may keep a client that was archived since.</summary>
    private static async Task<ValidationProblem?> ValidateClientAsync(
        AppDbContext db,
        Guid workspaceId,
        Guid? clientId,
        Guid? currentClientId,
        CancellationToken cancellationToken)
    {
        if (clientId is null || clientId == currentClientId)
        {
            return null;
        }

        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId && c.WorkspaceId == workspaceId)
            .Select(c => new { c.ArchivedAt })
            .SingleOrDefaultAsync(cancellationToken);

        return client switch
        {
            null => Problems.Validation("clientId", "The client does not exist."),
            { ArchivedAt: not null } => Problems.Validation("clientId", "The client is archived."),
            _ => null,
        };
    }

    /// <summary>The project, if the caller can see it.</summary>
    private static Task<Project?> FindAsync(AppDbContext db, WorkspacePolicy policy, Guid projectId, CancellationToken cancellationToken) =>
        policy.VisibleProjects().SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);

    private static async Task<List<ProjectResponse>> ToResponsesAsync(
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        Guid workspaceId,
        IReadOnlyCollection<Project> projects,
        CancellationToken cancellationToken)
    {
        var managed = await policy.ManagedProjectIdsAsync(cancellationToken);
        // Daily hours are for everyone; rates in it are shown to admins only.
        var card = await rates.CardAsync(cancellationToken);
        var projectIds = projects.Select(p => p.Id).ToList();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => projectIds.Contains(t.ProjectId))
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        var trackedByProject = await db.TimeEntries
            .Where(e => e.WorkspaceId == workspaceId && e.ProjectId != null && e.Stop != null && !e.IsSplit && projectIds.Contains(e.ProjectId.Value))
            .GroupBy(e => e.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, Seconds = g.Sum(e => (long)Math.Floor((e.Stop!.Value - e.Start).TotalSeconds)) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Seconds, cancellationToken);
        // Split entries are few; their parts are added up here rather than in the database.
        var splitParts = await SplitPartsAsync(db, workspaceId, cancellationToken);
        foreach (var (part, seconds) in splitParts)
        {
            if (part.ProjectId is { } projectId)
            {
                trackedByProject[projectId] = trackedByProject.GetValueOrDefault(projectId) + seconds;
            }
        }

        var trackedByTask = await TrackedSecondsByTaskAsync(db, workspaceId, splitParts, cancellationToken);

        var tasksByProject = tasks.ToLookup(t => t.ProjectId);

        return projects
            .Select(p => new ProjectResponse(
                p.Id,
                p.Name,
                p.ClientId,
                p.Color,
                p.IsPrivate,
                p.IsBillable,
                p.SeparateBilledTime,
                ToSeconds(p.Estimate),
                p.ArchivedAt,
                trackedByProject.GetValueOrDefault(p.Id),
                tasksByProject[p.Id].Select(t => ToResponse(t, trackedByTask.GetValueOrDefault(t.Id))).ToList(),
                policy.IsAdmin ? card.Current(RateScope.Project(p.Id)) : null,
                policy.IsAdmin || managed.Contains(p.Id),
                p.BillingMode,
                card.Current(RateScope.DailyHours(p.Id))))
            .ToList();
    }

    private static async Task<List<(TimeShare Part, long Seconds)>> SplitPartsAsync(AppDbContext db, Guid workspaceId, CancellationToken cancellationToken) =>
        (await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.IsSplit && e.Stop != null)
            .Select(e => new { e.Shares, e.Start, Stop = e.Stop!.Value })
            .ToListAsync(cancellationToken))
        .SelectMany(e => TimeShare.Divide(e.Shares, (long)Math.Floor((e.Stop - e.Start).TotalSeconds)))
        .ToList();

    /// <summary>Time tracked on each task, with the parts of split entries.</summary>
    private static async Task<Dictionary<Guid, long>> TrackedSecondsByTaskAsync(
        AppDbContext db,
        Guid workspaceId,
        List<(TimeShare Part, long Seconds)> splitParts,
        CancellationToken cancellationToken)
    {
        var tracked = await db.TimeEntries
            .Where(e => e.WorkspaceId == workspaceId && e.TaskId != null && e.Stop != null && !e.IsSplit)
            .GroupBy(e => e.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, Seconds = g.Sum(e => (long)Math.Floor((e.Stop!.Value - e.Start).TotalSeconds)) })
            .ToDictionaryAsync(x => x.TaskId, x => x.Seconds, cancellationToken);
        foreach (var (part, seconds) in splitParts)
        {
            if (part.TaskId is { } taskId)
            {
                tracked[taskId] = tracked.GetValueOrDefault(taskId) + seconds;
            }
        }

        return tracked;
    }

    private static TaskResponse ToResponse(ProjectTask task, long trackedSeconds) =>
        new(task.Id, task.ProjectId, task.Name, ToSeconds(task.Estimate), task.IsDone, trackedSeconds, task.AgreementId);

    private static ProjectDetails ToDetails(ProjectRequest request, BillingMode billingMode) => new(
        request.Name,
        request.ClientId,
        request.Color,
        request.IsPrivate,
        request.IsBillable,
        ToEstimate(request.EstimateSeconds),
        billingMode);

    /// <summary>The billing mode asked for; apps that predate it only say whether billed time is entered separately.</summary>
    private static BillingMode ModeFor(ProjectRequest request, BillingMode current) =>
        request.BillingMode ?? request.SeparateBilledTime switch
        {
            true => current == BillingMode.AsTracked ? BillingMode.PerDay : current,
            false => BillingMode.AsTracked,
            null => current,
        };

    private static ValidationProblem? MissingDailyHours(ProjectRequest request) =>
        request.BillingMode == BillingMode.FullDay && request.DailyHours is null
            ? Problems.Validation("dailyHours", "Enter how many hours a full day is, e.g. 8.")
            : null;

    /// <summary>
    /// Daily hours are set with the full-day mode only, by whoever manages the project. Other modes leave them be,
    /// so switching back to full days finds them as they were.
    /// </summary>
    private static Task SetDailyHoursAsync(Project project, ProjectRequest request, RateBook rates, CancellationToken cancellationToken) =>
        request.BillingMode == BillingMode.FullDay
            ? rates.SetAsync(RateScope.DailyHours(project.Id), request.DailyHours, request.DailyHoursFrom, "dailyHours", "dailyHoursFrom", cancellationToken)
            : Task.CompletedTask;

    private static TimeSpan? ToEstimate(long? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static long? ToSeconds(TimeSpan? duration) => duration is { } value ? (long)value.TotalSeconds : null;
}
