using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Spindle.Api.Common;
using Spindle.Api.Reports.Export;
using Spindle.Api.Subscriptions;

namespace Spindle.Api.Reports;

/// <summary>File formats of report exports; passed as <c>?format=csv</c>, <c>xlsx</c> or <c>pdf</c>.</summary>
public enum ExportFormat
{
    Csv,
    Xlsx,
    Pdf,
}

internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/reports").WithTags("Reports");

        // POST, because a report's filter does not fit comfortably in a URL. Nothing is changed.
        group.MapPost("/summary", SummaryAsync).WithName("summaryReport").AllowWhenReadOnly();
        group.MapPost("/detailed", DetailedAsync).WithName("detailedReport").AllowWhenReadOnly();
        group.MapPost("/weekly", WeeklyAsync).WithName("weeklyReport").AllowWhenReadOnly();

        // The same reports as files. Every entry is included, not a page of them. The document names each file
        // type, so generated clients accept the one that comes back.
        string[] fileTypes = ["text/csv", XlsxExport.ContentType, PdfExport.ContentType];
        group.MapPost("/summary/export", ExportSummaryAsync).WithName("exportSummaryReport").AllowWhenReadOnly().Produces<byte[]>(StatusCodes.Status200OK, fileTypes[0], fileTypes[1..]);
        group.MapPost("/detailed/export", ExportDetailedAsync).WithName("exportDetailedReport").AllowWhenReadOnly().Produces<byte[]>(StatusCodes.Status200OK, fileTypes[0], fileTypes[1..]);
        group.MapPost("/weekly/export", ExportWeeklyAsync).WithName("exportWeeklyReport").AllowWhenReadOnly().Produces<byte[]>(StatusCodes.Status200OK, fileTypes[0], fileTypes[1..]);

        return workspace;
    }

    /// <summary>Time added up by group and sub-group, with a chart of it over the days.</summary>
    private static async Task<Results<Ok<SummaryReport>, ValidationProblem>> SummaryAsync(
        SummaryReportRequest request,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        var (data, problem) = await loader.LoadAsync(request, ReportFilter.MaxDays, cancellationToken);
        return data is null ? problem! : TypedResults.Ok(ReportBuilder.Summary(data, request.GroupBy, request.SubGroupBy));
    }

    /// <summary>The entries themselves, a page at a time.</summary>
    private static async Task<Results<Ok<DetailedReport>, ValidationProblem>> DetailedAsync(
        DetailedReportRequest request,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        var (data, problem) = await loader.LoadAsync(request, ReportFilter.MaxDays, cancellationToken);
        return data is null ? problem! : TypedResults.Ok(ReportBuilder.Detailed(data, request));
    }

    /// <summary>Time per day, for up to a month; usually a week.</summary>
    private static async Task<Results<Ok<WeeklyReport>, ValidationProblem>> WeeklyAsync(
        WeeklyReportRequest request,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        var (data, problem) = await loader.LoadAsync(request, WeeklyReportRequest.MaxWeeklyDays, cancellationToken);
        return data is null ? problem! : TypedResults.Ok(ReportBuilder.Weekly(data, request.GroupBy));
    }

    private static async Task<Results<FileContentHttpResult, ValidationProblem>> ExportSummaryAsync(
        SummaryReportRequest request,
        [FromQuery] string format,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        if (ParseFormat(format) is not { } fileFormat)
        {
            return InvalidFormat;
        }

        var (data, problem) = await loader.LoadAsync(request, ReportFilter.MaxDays, cancellationToken);
        if (data is null)
        {
            return problem!;
        }

        var report = ReportBuilder.Summary(data, request.GroupBy, request.SubGroupBy);
        return File(ReportTables.Summary(data, report, request.GroupBy, request.SubGroupBy), fileFormat);
    }

    private static async Task<Results<FileContentHttpResult, ValidationProblem>> ExportDetailedAsync(
        DetailedReportRequest request,
        [FromQuery] string format,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        if (ParseFormat(format) is not { } fileFormat)
        {
            return InvalidFormat;
        }

        var (data, problem) = await loader.LoadAsync(request, ReportFilter.MaxDays, cancellationToken);
        if (data is null)
        {
            return problem!;
        }

        var entries = ReportBuilder.Sorted(data, request).Select(entry => ReportBuilder.Describe(data, entry));
        return File(ReportTables.Detailed(data, entries), fileFormat);
    }

    private static async Task<Results<FileContentHttpResult, ValidationProblem>> ExportWeeklyAsync(
        WeeklyReportRequest request,
        [FromQuery] string format,
        ReportLoader loader,
        CancellationToken cancellationToken)
    {
        if (ParseFormat(format) is not { } fileFormat)
        {
            return InvalidFormat;
        }

        var (data, problem) = await loader.LoadAsync(request, WeeklyReportRequest.MaxWeeklyDays, cancellationToken);
        if (data is null)
        {
            return problem!;
        }

        return File(ReportTables.Weekly(data, ReportBuilder.Weekly(data, request.GroupBy), request.GroupBy), fileFormat);
    }

    private static ValidationProblem InvalidFormat => Problems.Validation("format", "Export as csv, xlsx or pdf.");

    /// <summary>The file format in the query, e.g. <c>?format=xlsx</c>, in any case, like enums in JSON.</summary>
    private static ExportFormat? ParseFormat(string format) =>
        Enum.TryParse<ExportFormat>(format, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static FileContentHttpResult File(ReportTable table, ExportFormat format) => format switch
    {
        ExportFormat.Xlsx => TypedResults.File(XlsxExport.Write(table), XlsxExport.ContentType, $"{table.FileName}.xlsx"),
        ExportFormat.Pdf => TypedResults.File(PdfExport.Write(table), PdfExport.ContentType, $"{table.FileName}.pdf"),
        _ => TypedResults.File(CsvExport.Write(table), CsvExport.ContentType, $"{table.FileName}.csv"),
    };
}
