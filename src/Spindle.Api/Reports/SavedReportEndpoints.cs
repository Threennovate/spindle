using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Reports;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Reports;

/// <param name="Parameters">The report's period, filters and grouping: JSON text of an object, as the app that saved it wrote it.</param>
public sealed record SavedReportResponse(Guid Id, string Name, ReportKind Kind, string Parameters, DateTimeOffset UpdatedAt);

public sealed record SavedReportRequest
{
    [Required]
    [StringLength(SavedReport.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    /// <summary>Only read when the report is first saved; a saved report keeps its kind.</summary>
    public ReportKind Kind { get; init; }

    /// <summary>The report's settings: JSON text of an object, e.g. <c>{"period":"lastMonth"}</c>.</summary>
    [Required]
    [StringLength(SavedReport.ParametersMaxLength)]
    public string Parameters { get; init; } = string.Empty;
}

/// <summary>Each person's own saved reports in a workspace.</summary>
internal static class SavedReportEndpoints
{
    private const string NameTaken = "You already have a saved report with this name.";

    public static IEndpointRouteBuilder MapSavedReportEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/reports/saved").WithTags("Reports");

        group.MapGet("", ListAsync).WithName("listSavedReports");
        group.MapPost("", CreateAsync).WithName("createSavedReport");
        group.MapPut("/{reportId:guid}", UpdateAsync).WithName("updateSavedReport");
        group.MapDelete("/{reportId:guid}", DeleteAsync).WithName("deleteSavedReport");

        return workspace;
    }

    private static async Task<Ok<List<SavedReportResponse>>> ListAsync(CurrentWorkspace workspace, AppDbContext db, CancellationToken cancellationToken)
    {
        var reports = await db.SavedReports.AsNoTracking()
            .Where(r => r.WorkspaceId == workspace.Id && r.UserId == workspace.UserId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(reports.Select(ToResponse).ToList());
    }

    private static async Task<Results<Created<SavedReportResponse>, ValidationProblem>> CreateAsync(
        SavedReportRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (InvalidParameters(request) is { } invalid)
        {
            return invalid;
        }

        var report = SavedReport.Create(workspace.Id, workspace.UserId, request.Name, request.Kind, request.Parameters);
        db.SavedReports.Add(report);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Created((string?)null, ToResponse(report));
    }

    private static async Task<Results<Ok<SavedReportResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid reportId,
        SavedReportRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var report = await FindAsync(db, workspace, reportId, cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        if (InvalidParameters(request) is { } invalid)
        {
            return invalid;
        }

        report.Update(request.Name, request.Parameters);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Ok(ToResponse(report));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid reportId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var report = await FindAsync(db, workspace, reportId, cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        db.SavedReports.Remove(report);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? InvalidParameters(SavedReportRequest request)
    {
        try
        {
            using var parameters = JsonDocument.Parse(request.Parameters);
            if (parameters.RootElement.ValueKind == JsonValueKind.Object)
            {
                return null;
            }
        }
        catch (JsonException)
        {
        }

        return Problems.Validation("parameters", "Send the report's settings as the JSON text of an object.");
    }

    private static Task<SavedReport?> FindAsync(AppDbContext db, CurrentWorkspace workspace, Guid reportId, CancellationToken cancellationToken) =>
        db.SavedReports.SingleOrDefaultAsync(r => r.Id == reportId && r.WorkspaceId == workspace.Id && r.UserId == workspace.UserId, cancellationToken);

    private static SavedReportResponse ToResponse(SavedReport report) =>
        new(report.Id, report.Name, report.Kind, report.Parameters, report.UpdatedAt);
}
