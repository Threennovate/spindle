using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Spindle.Api.Common;
using Spindle.Api.Reports.Export;
using Spindle.Api.Subscriptions;
using Spindle.Api.Workspaces;
using Spindle.Domain.Projects;
using Spindle.Domain.Reports;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Reports;

/// <summary>A timesheet link as admins manage it.</summary>
/// <param name="Url">The link to send.</param>
/// <param name="CreatedBy">Who made it.</param>
/// <param name="LastOpenedAt">When someone last opened the link, if ever.</param>
public sealed record SharedReportResponse(
    Guid Id,
    string Title,
    string Url,
    SharedPeriod Period,
    DateOnly? From,
    DateOnly? To,
    Guid[] UserIds,
    Guid[] GroupIds,
    Guid[] ProjectIds,
    Guid[] ClientIds,
    Guid[] TaskIds,
    Guid[] TagIds,
    string? Description,
    RoundingMode Rounding,
    int RoundingMinutes,
    bool ShowPeople,
    bool ShowAmounts,
    HourFormat HourFormat,
    SharedSender Sender,
    string? SenderName,
    bool ShowToday,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastOpenedAt);

/// <summary>
/// A timesheet link: the report filter it counts, without its days, a period, and what it shows. It always counts
/// billable time only. Days are counted in the time zone of whoever saves it.
/// </summary>
public sealed record SharedReportRequest
{
    /// <summary>What the reader sees as the timesheet's name, e.g. the client's.</summary>
    [Required]
    [StringLength(SharedReport.TitleMaxLength)]
    public string Title { get; init; } = string.Empty;

    public SharedPeriod Period { get; init; } = SharedPeriod.ThisMonth;

    /// <summary>The first day of a fixed period.</summary>
    public DateOnly? From { get; init; }

    /// <summary>The last day of a fixed period, included.</summary>
    public DateOnly? To { get; init; }

    [MaxLength(1000)]
    public Guid[] UserIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] GroupIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] ProjectIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] ClientIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] TaskIds { get; init; } = [];

    [MaxLength(1000)]
    public Guid[] TagIds { get; init; } = [];

    /// <summary>Entries whose description contains this.</summary>
    [StringLength(200)]
    public string? Description { get; init; }

    public RoundingMode Rounding { get; init; }

    public int RoundingMinutes { get; init; } = 15;

    /// <summary>Whether each row names who did the work.</summary>
    public bool ShowPeople { get; init; }

    /// <summary>Whether amounts are shown, at the rates of each day.</summary>
    public bool ShowAmounts { get; init; }

    public HourFormat HourFormat { get; init; }

    /// <summary>Whose name the timesheet is from.</summary>
    public SharedSender Sender { get; init; }

    /// <summary>The name the timesheet is from, when <see cref="Sender"/> is custom.</summary>
    [StringLength(SharedReport.SenderNameMaxLength)]
    public string? SenderName { get; init; }

    /// <summary>Whether today's time counts while today is in the period; otherwise the timesheet counts up to yesterday.</summary>
    public bool ShowToday { get; init; }
}

/// <summary>Timesheet links: admins make them from a report, and anyone with one reads it without an account.</summary>
internal static class SharedReportEndpoints
{
    public const string RateLimit = "shared-reports";

    /// <summary>The problem's <c>type</c> when the link's organization is read-only, so the page can say so plainly.</summary>
    public const string UnavailableType = "urn:spindle:problem:shared-report-unavailable";

    public static IEndpointRouteBuilder MapSharedReportEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/shared-reports").WithTags("Reports").RequireWorkspaceAdmin();

        group.MapGet("", ListAsync).WithName("listSharedReports");
        group.MapPost("", CreateAsync).WithName("createSharedReport");
        group.MapPut("/{reportId:guid}", UpdateAsync).WithName("updateSharedReport");
        group.MapPost("/{reportId:guid}/new-address", NewAddressAsync).WithName("newSharedReportAddress");
        group.MapDelete("/{reportId:guid}", DeleteAsync).WithName("deleteSharedReport").AllowWhenReadOnly();

        return workspace;
    }

    /// <summary>Reading a timesheet link, outside any workspace.</summary>
    public static IEndpointRouteBuilder MapPublicSharedReportEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/shared/{token}").WithTags("Shared reports").AllowAnonymous()
            .RequireRateLimiting(RateLimit)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("", ReadAsync).WithName("readSharedReport")
            .Produces<SharedTimesheet>()
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();
        group.MapGet("/export", ExportAsync).WithName("exportSharedReport")
            .Produces<byte[]>(StatusCodes.Status200OK, "text/csv", XlsxExport.ContentType, PdfExport.ContentType)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        return api;
    }

    private static async Task<Ok<List<SharedReportResponse>>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        IOptions<AppOptions> app,
        CancellationToken cancellationToken)
    {
        var reports = await (
            from report in db.SharedReports.AsNoTracking()
            join user in db.Users on report.CreatedByUserId equals user.Id
            where report.WorkspaceId == workspace.Id
            orderby report.Title
            select new { report, user.FullName })
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(reports.Select(r => ToResponse(r.report, r.FullName, app.Value)).ToList());
    }

    private static async Task<Created<SharedReportResponse>> CreateAsync(
        SharedReportRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        IOptions<AppOptions> app,
        CancellationToken cancellationToken)
    {
        var report = SharedReport.Create(workspace.Id, workspace.UserId, NewToken(), await SettingsAsync(request, workspace, db, cancellationToken));
        db.SharedReports.Add(report);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created((string?)null, await ResponseAsync(report, db, app.Value, cancellationToken));
    }

    private static async Task<Results<Ok<SharedReportResponse>, NotFound>> UpdateAsync(
        Guid reportId,
        SharedReportRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        IOptions<AppOptions> app,
        CancellationToken cancellationToken)
    {
        var report = await db.SharedReports.SingleOrDefaultAsync(r => r.Id == reportId && r.WorkspaceId == workspace.Id, cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        report.Update(await SettingsAsync(request, workspace, db, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(report, db, app.Value, cancellationToken));
    }

    /// <summary>Gives the link a new address. The old one stops working at once.</summary>
    private static async Task<Results<Ok<SharedReportResponse>, NotFound>> NewAddressAsync(
        Guid reportId,
        CurrentWorkspace workspace,
        AppDbContext db,
        IOptions<AppOptions> app,
        CancellationToken cancellationToken)
    {
        var report = await db.SharedReports.SingleOrDefaultAsync(r => r.Id == reportId && r.WorkspaceId == workspace.Id, cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        report.NewToken(NewToken());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ResponseAsync(report, db, app.Value, cancellationToken));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid reportId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var report = await db.SharedReports.SingleOrDefaultAsync(r => r.Id == reportId && r.WorkspaceId == workspace.Id, cancellationToken);
        if (report is null)
        {
            return TypedResults.NotFound();
        }

        db.SharedReports.Remove(report);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The timesheet behind a link, for its own period or, with a negative <paramref name="step"/>, an earlier one of
    /// the same length.
    /// </summary>
    private static async Task<IResult> ReadAsync(
        string token,
        AppDbContext db,
        ReportLoader loader,
        OrganizationSubscriptions subscriptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        [FromQuery] int step = 0)
    {
        var (timesheet, problem) = await TimesheetAsync(token, step, db, loader, subscriptions, timeProvider, cancellationToken);
        return timesheet is null ? problem! : TypedResults.Ok(timesheet);
    }

    private static async Task<IResult> ExportAsync(
        string token,
        [FromQuery] string format,
        AppDbContext db,
        ReportLoader loader,
        OrganizationSubscriptions subscriptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        [FromQuery] int step = 0)
    {
        if (!Enum.TryParse<ExportFormat>(format, ignoreCase: true, out var fileFormat) || !Enum.IsDefined(fileFormat))
        {
            return Problems.Validation("format", "Export as csv, xlsx or pdf.");
        }

        var (timesheet, problem) = await TimesheetAsync(token, step, db, loader, subscriptions, timeProvider, cancellationToken);
        if (timesheet is null)
        {
            return problem!;
        }

        var table = SharedTimesheets.Table(timesheet);
        return fileFormat switch
        {
            ExportFormat.Xlsx => TypedResults.File(XlsxExport.Write(table), XlsxExport.ContentType, $"{table.FileName}.xlsx"),
            ExportFormat.Pdf => TypedResults.File(PdfExport.Write(table), PdfExport.ContentType, $"{table.FileName}.pdf"),
            _ => TypedResults.File(CsvExport.Write(table), CsvExport.ContentType, $"{table.FileName}.csv"),
        };
    }

    private static async Task<(SharedTimesheet? Timesheet, IResult? Problem)> TimesheetAsync(
        string token,
        int step,
        AppDbContext db,
        ReportLoader loader,
        OrganizationSubscriptions subscriptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // An unknown, replaced or removed link is the same 404.
        var report = await db.SharedReports.AsNoTracking().SingleOrDefaultAsync(r => r.Token == token, cancellationToken);
        if (report is null)
        {
            return (null, TypedResults.NotFound());
        }

        // A read-only organization's links rest until it pays; the reader isn't told why.
        if (subscriptions.Enabled)
        {
            var organizationId = await db.Workspaces.Where(w => w.Id == report.WorkspaceId).Select(w => w.OrganizationId).SingleAsync(cancellationToken);
            var standing = (await subscriptions.StandingsAsync([organizationId], cancellationToken))[organizationId];
            if (standing.Access != OrganizationAccess.Full)
            {
                return (null, TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: UnavailableType,
                    title: "This timesheet isn't available right now."));
            }
        }

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(report.TimeZone) ?? DateTimeZone.Utc;
        var today = Instant.FromDateTimeOffset(timeProvider.GetUtcNow()).InZone(zone).Date.ToDateOnly();
        if (report.Days(today, step) is not { } days)
        {
            return (null, Problems.Validation("step", report.IsRelative ? "Choose this period or an earlier one." : "This timesheet covers fixed days."));
        }

        var (from, to) = days;

        var filter = report.Filter;
        var reportFilter = new ReportFilter
        {
            From = from,
            To = to,
            UserIds = filter.UserIds,
            GroupIds = filter.GroupIds,
            ProjectIds = filter.ProjectIds,
            ClientIds = filter.ClientIds,
            TaskIds = filter.TaskIds,
            TagIds = filter.TagIds,
            Billable = true,
            Description = filter.Description,
            Rounding = filter.Rounding,
            RoundingMinutes = filter.RoundingMinutes,
        };
        // Counted as an admin would, since the admin who made it chose what it shows.
        var (data, problem) = await loader.LoadAsync(
            reportFilter, SharedReport.MaxDays, new ReportScope(report.WorkspaceId, null, true, zone, report.WeekStart), cancellationToken);
        if (data is null)
        {
            return (null, problem);
        }

        // Days described by hand count while their project's billed time is entered separately. A day's text covers
        // all of that person's work on the project, so a link narrowed to some tasks, tags or descriptions shows only
        // the descriptions of the entries it counts, as it leaves out the day's billed time too.
        var narrowed = filter.TaskIds.Length > 0 || filter.TagIds.Length > 0 || filter.Description is not null;
        var described = narrowed
            ? new Dictionary<(Guid, Guid, DateOnly), string>()
            : await db.BilledTimes.AsNoTracking()
                .Where(b => b.WorkspaceId == report.WorkspaceId && b.Day >= from && b.Day <= to && b.Description != null
                    && db.Projects.Any(p => p.Id == b.ProjectId && p.BillingMode != BillingMode.AsTracked))
                .ToDictionaryAsync(b => (b.ProjectId, b.UserId, b.Day), b => b.Description!, cancellationToken);

        var now = timeProvider.GetUtcNow();
        await db.SharedReports.Where(r => r.Id == report.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastOpenedAt, now), cancellationToken);

        return (SharedTimesheets.Build(report, data, described, step, today), null);
    }

    private static async Task<SharedReportSettings> SettingsAsync(
        SharedReportRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var maker = await db.Users.Where(u => u.Id == workspace.UserId).Select(u => new { u.TimeZone, u.WeekStart }).SingleAsync(cancellationToken);
        return new SharedReportSettings(
            request.Title,
            request.Period,
            request.From,
            request.To,
            DateTimeZoneProviders.Tzdb.GetZoneOrNull(maker.TimeZone) is null ? "UTC" : maker.TimeZone,
            (DayOfWeek)maker.WeekStart,
            new SharedReportFilter(
                request.UserIds,
                request.GroupIds,
                request.ProjectIds,
                request.ClientIds,
                request.TaskIds,
                request.TagIds,
                request.Description,
                request.Rounding,
                request.RoundingMinutes),
            request.ShowPeople,
            request.ShowAmounts,
            request.HourFormat,
            request.Sender,
            request.SenderName,
            request.ShowToday);
    }

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static async Task<SharedReportResponse> ResponseAsync(SharedReport report, AppDbContext db, AppOptions app, CancellationToken cancellationToken)
    {
        var maker = await db.Users.Where(u => u.Id == report.CreatedByUserId).Select(u => u.FullName).SingleAsync(cancellationToken);
        return ToResponse(report, maker, app);
    }

    private static SharedReportResponse ToResponse(SharedReport report, string createdBy, AppOptions app) => new(
        report.Id,
        report.Title,
        app.Link($"/share/{report.Token}"),
        report.Period,
        report.From,
        report.To,
        report.UserIds,
        report.GroupIds,
        report.ProjectIds,
        report.ClientIds,
        report.TaskIds,
        report.TagIds,
        report.Description,
        report.Rounding,
        report.RoundingMinutes,
        report.ShowPeople,
        report.ShowAmounts,
        report.HourFormat,
        report.Sender,
        report.SenderName,
        report.ShowToday,
        createdBy,
        report.CreatedAt,
        report.LastOpenedAt);
}
