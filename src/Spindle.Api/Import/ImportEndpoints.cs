using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Clients;
using Spindle.Domain.Projects;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Import;

/// <summary>Whose time a person's rows in the file become; no member leaves them out.</summary>
public sealed record ImportAssignment
{
    [Required]
    [StringLength(300)]
    public string Key { get; init; } = string.Empty;

    public Guid? UserId { get; init; }
}

public sealed record ImportRequest
{
    public const int MaxLength = 20_000_000;

    /// <summary>The CSV file's text.</summary>
    [Required]
    [StringLength(MaxLength)]
    public string Content { get; init; } = string.Empty;

    /// <summary>The time zone the file's times are in; the importer's own if left out.</summary>
    [IanaTimeZone]
    public string? TimeZone { get; init; }

    public DateOrder DateOrder { get; init; } = DateOrder.Auto;

    [MaxLength(1000)]
    public ImportAssignment[] Assignments { get; init; } = [];
}

/// <param name="Rows">Entries in the file.</param>
/// <param name="Placed">
/// Entries the file gives only a day and hours for, as Harvest does unless an account tracks start and end times.
/// Each person's on a day are placed one after another from 9:00, in the file's order.
/// </param>
/// <param name="Ready">Entries the import would add.</param>
/// <param name="Duplicates">Entries in the workspace already, or twice in the file.</param>
/// <param name="Skipped">Entries of people not assigned to a member.</param>
/// <param name="Problems">The first hundred rows that could not be read.</param>
public sealed record ImportPreview(
    string Format,
    DateOrder DateOrder,
    bool DateOrderGuessed,
    int Rows,
    int Placed,
    int Ready,
    int Duplicates,
    int Skipped,
    DateOnly? From,
    DateOnly? To,
    long Seconds,
    IReadOnlyList<ImportPerson> People,
    IReadOnlyList<string> NewClients,
    IReadOnlyList<ImportNewProject> NewProjects,
    IReadOnlyList<string> NewTasks,
    IReadOnlyList<string> NewTags,
    IReadOnlyList<ImportProblem> Problems,
    int ProblemCount);

public sealed record ImportResult(int Created, int Duplicates, int Skipped, int Problems, int NewClients, int NewProjects, int NewTasks, int NewTags);

/// <summary>Brings time over from Toggl Track, Clockify, Harvest or another Spindle workspace. For admins.</summary>
internal static class ImportEndpoints
{
    private const long MaxBodyBytes = 64 * 1024 * 1024;

    private static readonly string[] Colors =
        ["#0B83D9", "#9E5BD9", "#D94182", "#E36A00", "#BF7000", "#2DA608", "#06A893", "#C9806B", "#465BB3", "#990099", "#C7AF14", "#566614", "#D92B2B", "#525266", "#991102"];

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/imports").WithTags("Import").RequireWorkspaceAdmin().WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));

        group.MapPost("/preview", PreviewAsync).WithName("previewImport");
        group.MapPost("", ImportAsync).WithName("importTimeEntries");

        return workspace;
    }

    /// <summary>What importing the file would do. Nothing changes yet.</summary>
    private static async Task<Ok<ImportPreview>> PreviewAsync(
        ImportRequest request,
        ImportPlanner planner,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var (plan, zone) = await PlanAsync(request, planner, workspace, db, cancellationToken);
        var days = plan.Entries.Select(e => Instant.FromDateTimeOffset(e.Start).InZone(zone).Date.ToDateOnly()).ToList();

        return TypedResults.Ok(new ImportPreview(
            plan.File.Format,
            plan.File.DateOrder,
            plan.File.DateOrderGuessed,
            plan.File.Rows.Count + plan.File.Problems.Count(p => p.Line > 1),
            plan.File.Rows.Count(r => r.Placed),
            plan.Entries.Count,
            plan.Duplicates,
            plan.Skipped,
            days.Count > 0 ? days.Min() : null,
            days.Count > 0 ? days.Max() : null,
            plan.Entries.Sum(e => (long)(e.Stop - e.Start).TotalSeconds),
            plan.People,
            plan.NewClients(),
            plan.NewProjects(),
            plan.NewTasks(),
            plan.NewTags(),
            plan.File.Problems.Take(100).ToList(),
            plan.File.Problems.Count));
    }

    /// <summary>Imports the file: all of it that can be, in one go, or nothing if something fails on the way.</summary>
    private static async Task<Results<Ok<ImportResult>, ValidationProblem>> ImportAsync(
        ImportRequest request,
        ImportPlanner planner,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var (plan, _) = await PlanAsync(request, planner, workspace, db, cancellationToken);
        if (plan.Entries.Count == 0 && plan.File.Rows.Count == 0)
        {
            return Problems.Validation("content", plan.File.Problems.FirstOrDefault()?.Message ?? "The file has no time entries.");
        }

        var newClients = plan.NewClients();
        var newProjects = plan.NewProjects();
        var newTasks = plan.NewTasks();
        var newTags = plan.NewTags();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var clients = plan.Clients.ToDictionary(c => c.Key, c => c.Value.Id);
            foreach (var name in newClients)
            {
                var client = Client.Create(workspace.Id, name);
                db.Clients.Add(client);
                clients[ImportPlan.Key(name)] = client.Id;
            }

            Guid? ClientId(string? name) => name is null ? null : clients[ImportPlan.Key(name)];

            var projects = plan.Projects.ToDictionary(p => p.Key, p => p.Value.Id);
            var colorIndex = plan.ExistingProjectCount;
            foreach (var project in newProjects)
            {
                // A new project is billable if most of its time in the file is.
                var rows = plan.Entries.Where(e => e.Row.Project is { } p && ImportPlan.Key(p) == ImportPlan.Key(project.Name) && SameClient(e.Row.Client, project.Client)).ToList();
                var billable = rows.Count(r => r.Row.Billable) * 2 > rows.Count;
                var created = Project.Create(workspace.Id, new ProjectDetails(project.Name, ClientId(project.Client), Colors[colorIndex++ % Colors.Length], false, billable, null));
                db.Projects.Add(created);
                projects[(ClientId(project.Client), ImportPlan.Key(project.Name))] = created.Id;
            }

            Guid? ProjectId(ImportRow row) => row.Project is null ? null : projects[(ClientId(row.Client), ImportPlan.Key(row.Project))];

            var tasks = plan.Tasks.ToDictionary(t => t.Key, t => t.Value.Id);
            foreach (var row in plan.Entries.Select(e => e.Row).Where(r => r.Task is not null))
            {
                var key = (ProjectId(row)!.Value, ImportPlan.Key(row.Task!));
                if (!tasks.ContainsKey(key))
                {
                    var task = ProjectTask.Create(workspace.Id, key.Item1, row.Task!, null);
                    db.Tasks.Add(task);
                    tasks[key] = task.Id;
                }
            }

            var tags = plan.Tags.ToDictionary(t => t.Key, t => t.Value.Id);
            foreach (var name in newTags)
            {
                var tag = Tag.Create(workspace.Id, name);
                db.Tags.Add(tag);
                tags[ImportPlan.Key(name)] = tag.Id;
            }

            await db.SaveChangesAsync(ct);

            // Saved in batches, so a large file does not pile up in memory.
            foreach (var batch in plan.Entries.Chunk(1000))
            {
                foreach (var entry in batch)
                {
                    var row = entry.Row;
                    var projectId = ProjectId(row);
                    var details = new TimeEntryDetails(
                        row.Description,
                        projectId,
                        row.Task is null ? null : tasks[(projectId!.Value, ImportPlan.Key(row.Task))],
                        row.Tags.Select(t => tags[ImportPlan.Key(t)]).ToList(),
                        row.Billable);
                    db.TimeEntries.Add(TimeEntry.Create(null, workspace.Id, entry.UserId, details, entry.Start, entry.Stop));
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            await transaction.CommitAsync(ct);
        }, cancellationToken);

        return TypedResults.Ok(new ImportResult(
            plan.Entries.Count,
            plan.Duplicates,
            plan.Skipped,
            plan.File.Problems.Count,
            newClients.Count,
            newProjects.Count,
            newTasks.Count,
            newTags.Count));
    }

    private static bool SameClient(string? a, string? b) =>
        (a is null && b is null) || (a is not null && b is not null && ImportPlan.Key(a) == ImportPlan.Key(b));

    private static async Task<(ImportPlan Plan, DateTimeZone Zone)> PlanAsync(
        ImportRequest request,
        ImportPlanner planner,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var zoneId = request.TimeZone ?? await db.Users.Where(u => u.Id == workspace.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) ?? DateTimeZone.Utc;
        var file = ImportFile.Parse(request.Content, request.DateOrder);
        var assignments = request.Assignments.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.Last().UserId);
        return (await planner.PlanAsync(file, zone, assignments, cancellationToken), zone);
    }
}
