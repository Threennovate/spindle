using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Common;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Billing;

/// <param name="Amount">A fixed price, or a retainer's fee each period.</param>
/// <param name="Hours">The hours quoted for a fixed price, or included in each period of a retainer.</param>
/// <param name="EndsOn">The day a fixed price was done, or a retainer's last day.</param>
/// <param name="TaskIds">Its tasks: the time tracked on them counts towards it.</param>
/// <param name="IsOver">Whether it is over: a fixed price done, a retainer after its last day. Its tasks are closed then.</param>
public sealed record AgreementResponse(
    Guid Id,
    Guid ProjectId,
    string Name,
    AgreementKind Kind,
    decimal? Amount,
    decimal? Hours,
    AgreementPeriod? Period,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    bool BillsOverage,
    IReadOnlyList<Guid> TaskIds,
    bool IsOver);

public sealed record AgreementRequest
{
    [Required]
    [StringLength(Agreement.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public AgreementKind? Kind { get; init; }

    /// <summary>The price of a fixed price, required; a retainer's fee each period.</summary>
    [Range(0, 100_000_000)]
    public decimal? Amount { get; init; }

    /// <summary>The hours quoted for a fixed price, or included in each period of a retainer, e.g. 5 or 7.5.</summary>
    [Range(0, 100_000)]
    public decimal? Hours { get; init; }

    /// <summary>For a retainer: how often its fee is paid and its hours start again.</summary>
    public AgreementPeriod? Period { get; init; }

    [Required]
    public DateOnly? StartsOn { get; init; }

    /// <summary>The day a fixed price was done (today at the latest), or a retainer's last day. Null while it goes on.</summary>
    public DateOnly? EndsOn { get; init; }

    /// <summary>For a retainer: whether time beyond the hours included is billed on top, at the hourly rates.</summary>
    public bool BillsOverage { get; init; }

    /// <summary>Its tasks. A task of another agreement moves to this one. Left out, they stay as they are.</summary>
    [MaxLength(1000)]
    public Guid[]? TaskIds { get; init; }

    /// <summary>
    /// When creating it: also gives it a task of its own name, so time can be tracked on it straight away. A task of
    /// that name already on the project is taken instead.
    /// </summary>
    public bool CreateTask { get; init; } = true;
}

public sealed record AgreementTimeRequest
{
    /// <summary>The first day, in the caller's calendar.</summary>
    [Required]
    public DateOnly? From { get; init; }

    /// <summary>The last day, included.</summary>
    [Required]
    public DateOnly? To { get; init; }

    /// <summary>Which of the agreement's tasks the time goes to; left out, the one named like it, or else its first.</summary>
    public Guid? TaskId { get; init; }
}

/// <param name="Count">How many entries moved.</param>
public sealed record AgreementTimeResponse(int Count);

/// <summary>
/// What was agreed with clients for parts of projects: fixed prices and retainers. Money, so for admins only. Time
/// counts towards an agreement when it is tracked on one of its tasks.
/// </summary>
internal static class AgreementEndpoints
{
    private const string NameTaken = "An agreement with this name already exists on this project.";

    public static IEndpointRouteBuilder MapAgreementEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/projects/{projectId:guid}/agreements").WithTags("Agreements").RequireWorkspaceAdmin();

        group.MapPost("", CreateAsync).WithName("createAgreement");
        group.MapPut("/{agreementId:guid}", UpdateAsync).WithName("updateAgreement");
        group.MapDelete("/{agreementId:guid}", DeleteAsync).WithName("deleteAgreement");
        group.MapPost("/{agreementId:guid}/time", MoveTimeAsync).WithName("moveTimeToAgreement");

        return workspace;
    }

    private static async Task<Results<Created<AgreementResponse>, NotFound, ValidationProblem>> CreateAsync(
        Guid projectId,
        AgreementRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.WorkspaceId == workspace.Id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var today = await rates.TodayAsync(cancellationToken);
        if (CheckDone(request, today) is { } notYet)
        {
            return notYet;
        }

        var agreement = Agreement.Create(workspace.Id, projectId, ToDetails(request));
        db.Agreements.Add(agreement);

        var tasks = await db.Tasks.Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        if (AssignTasks(agreement, request.TaskIds, tasks) is { } wrongTask)
        {
            return wrongTask;
        }

        if (request.CreateTask)
        {
            var own = tasks.FirstOrDefault(t => string.Equals(t.Name, agreement.Name, StringComparison.OrdinalIgnoreCase));
            if (own is null)
            {
                own = ProjectTask.Create(workspace.Id, projectId, agreement.Name, estimate: null);
                db.Tasks.Add(own);
                tasks.Add(own);
            }

            own.AssignTo(agreement.Id);
        }

        CloseOrReopen(agreement, wasOver: false, today, tasks);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Created((string?)null, ToResponse(agreement, tasks, today));
    }

    private static async Task<Results<Ok<AgreementResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid projectId,
        Guid agreementId,
        AgreementRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var agreement = await FindAsync(db, workspace, projectId, agreementId, cancellationToken);
        if (agreement is null)
        {
            return TypedResults.NotFound();
        }

        var today = await rates.TodayAsync(cancellationToken);
        if (CheckDone(request, today) is { } notYet)
        {
            return notYet;
        }

        var wasOver = agreement.IsOver(today);
        agreement.Update(ToDetails(request));

        var tasks = await db.Tasks.Where(t => t.ProjectId == projectId).ToListAsync(cancellationToken);
        if (AssignTasks(agreement, request.TaskIds, tasks) is { } wrongTask)
        {
            return wrongTask;
        }

        CloseOrReopen(agreement, wasOver, today, tasks);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Ok(ToResponse(agreement, tasks, today));
    }

    /// <summary>Deletes the agreement. Its tasks and their time stay on the project, outside any agreement.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid projectId,
        Guid agreementId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var agreement = await FindAsync(db, workspace, projectId, agreementId, cancellationToken);
        if (agreement is null)
        {
            return TypedResults.NotFound();
        }

        foreach (var task in await db.Tasks.Where(t => t.AgreementId == agreementId).ToListAsync(cancellationToken))
        {
            task.AssignTo(null);
        }

        db.Agreements.Remove(agreement);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Moves the project's finished time without a task, on the days given, to one of the agreement's tasks: for time
    /// tracked before the agreement was entered.
    /// </summary>
    private static async Task<Results<Ok<AgreementTimeResponse>, NotFound, ValidationProblem>> MoveTimeAsync(
        Guid projectId,
        Guid agreementId,
        AgreementTimeRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var agreement = await FindAsync(db, workspace, projectId, agreementId, cancellationToken);
        if (agreement is null)
        {
            return TypedResults.NotFound();
        }

        if (request.To < request.From)
        {
            return Problems.Validation("to", "Choose a last day on or after the first one.");
        }

        var tasks = await db.Tasks.Where(t => t.AgreementId == agreementId).OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var task = request.TaskId is { } taskId
            ? tasks.FirstOrDefault(t => t.Id == taskId)
            : tasks.FirstOrDefault(t => string.Equals(t.Name, agreement.Name, StringComparison.OrdinalIgnoreCase)) ?? tasks.FirstOrDefault();
        if (task is null)
        {
            return Problems.Validation("taskId", request.TaskId is null ? "Give the agreement a task first." : "The task is not the agreement's.");
        }

        var zoneId = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) ?? DateTimeZone.Utc;
        var start = LocalDate.FromDateOnly(request.From!.Value).AtStartOfDayInZone(zone).ToDateTimeOffset();
        var end = LocalDate.FromDateOnly(request.To!.Value).PlusDays(1).AtStartOfDayInZone(zone).ToDateTimeOffset();

        var entries = await db.TimeEntries
            // A split entry is left as its parts are; giving its largest part a task here would change only that part.
            .Where(e => e.WorkspaceId == workspace.Id && e.ProjectId == projectId && e.TaskId == null && !e.IsSplit && e.Stop != null && e.Start >= start && e.Start < end)
            .ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            entry.Update(TimeEntryRules.DetailsOf(entry) with { TaskId = task.Id }, entry.Start, entry.Stop);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new AgreementTimeResponse(entries.Count));
    }

    private static Task<Agreement?> FindAsync(AppDbContext db, CurrentWorkspace workspace, Guid projectId, Guid agreementId, CancellationToken cancellationToken) =>
        db.Agreements.SingleOrDefaultAsync(a => a.Id == agreementId && a.ProjectId == projectId && a.WorkspaceId == workspace.Id, cancellationToken);

    /// <summary>A fixed price is marked done on a day that has come.</summary>
    private static ValidationProblem? CheckDone(AgreementRequest request, DateOnly today) =>
        request.Kind == AgreementKind.FixedPrice && request.EndsOn > today
            ? Problems.Validation("endsOn", "Mark it done today at the latest.")
            : null;

    /// <summary>Gives the agreement the tasks asked for, and takes away the others it had.</summary>
    private static ValidationProblem? AssignTasks(Agreement agreement, Guid[]? taskIds, List<ProjectTask> tasks)
    {
        if (taskIds is null)
        {
            return null;
        }

        var wanted = taskIds.ToHashSet();
        if (wanted.Any(id => tasks.All(t => t.Id != id)))
        {
            return Problems.Validation("taskIds", "Choose tasks of the agreement's project.");
        }

        foreach (var task in tasks)
        {
            if (wanted.Contains(task.Id))
            {
                task.AssignTo(agreement.Id);
            }
            else if (task.AgreementId == agreement.Id)
            {
                task.AssignTo(null);
            }
        }

        return null;
    }

    /// <summary>
    /// An agreement that is over closes its tasks, so they are no longer offered for new time; one that goes on again
    /// reopens them.
    /// </summary>
    private static void CloseOrReopen(Agreement agreement, bool wasOver, DateOnly today, List<ProjectTask> tasks)
    {
        var isOver = agreement.IsOver(today);
        if (isOver == wasOver && !isOver)
        {
            return;
        }

        foreach (var task in tasks.Where(t => t.AgreementId == agreement.Id))
        {
            task.MarkDone(isOver);
        }
    }

    private static AgreementDetails ToDetails(AgreementRequest request) => new(
        request.Name,
        request.Kind!.Value,
        request.Amount,
        request.Hours,
        request.Period,
        request.StartsOn!.Value,
        request.EndsOn,
        request.BillsOverage);

    public static AgreementResponse ToResponse(Agreement agreement, IEnumerable<ProjectTask> tasks, DateOnly today) =>
        ToResponse(agreement, tasks.Where(t => t.AgreementId == agreement.Id).OrderBy(t => t.Name).Select(t => t.Id).ToList(), today);

    public static AgreementResponse ToResponse(Agreement agreement, IReadOnlyList<Guid> taskIds, DateOnly today) => new(
        agreement.Id,
        agreement.ProjectId,
        agreement.Name,
        agreement.Kind,
        agreement.Amount,
        agreement.Hours,
        agreement.Period,
        agreement.StartsOn,
        agreement.EndsOn,
        agreement.BillsOverage,
        taskIds,
        agreement.IsOver(today));
}
