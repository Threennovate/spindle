using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Reports;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Billing;

/// <param name="Currency">The currency of all amounts, e.g. <c>EUR</c>.</param>
public sealed record ProfitabilityResponse(string Currency, IReadOnlyList<ProjectProfitability> Projects);

/// <summary>
/// What a project's time cost against what the client pays for it, over all its time: its agreements, and the time
/// outside them, billed by the hour.
/// </summary>
/// <param name="Revenue">
/// What the client pays: each fixed price in full (done or not), retainers' fees so far with any time beyond their
/// hours that is billed, and the time outside agreements at its rates.
/// </param>
/// <param name="LaborCost">What all the time tracked cost, at the members' labor costs of each day.</param>
public sealed record ProjectProfitability(
    Guid ProjectId,
    string Name,
    string Color,
    Guid? ClientId,
    bool IsArchived,
    long TrackedSeconds,
    decimal Revenue,
    decimal LaborCost,
    decimal Profit,
    IReadOnlyList<AgreementProfitability> Agreements,
    OutsideAgreements Outside);

/// <param name="Revenue">A fixed price in full, done or not; a retainer's fees so far, with its overage where billed.</param>
/// <param name="OverageAmount">What a retainer's time beyond its hours comes to, where that is billed.</param>
/// <param name="Periods">A retainer's periods so far, oldest first.</param>
public sealed record AgreementProfitability(
    AgreementResponse Agreement,
    long TrackedSeconds,
    decimal Revenue,
    decimal LaborCost,
    decimal Profit,
    decimal OverageAmount,
    IReadOnlyList<RetainerPeriodResponse> Periods);

/// <param name="IncludedSeconds">The time included, or null for a retainer without a limit.</param>
/// <param name="OverSeconds">The time beyond what is included.</param>
/// <param name="Fee">What the period is paid.</param>
/// <param name="OverageAmount">What the time beyond the hours comes to, where that is billed.</param>
public sealed record RetainerPeriodResponse(
    DateOnly Start,
    DateOnly End,
    long Seconds,
    long? IncludedSeconds,
    long OverSeconds,
    decimal? Fee,
    decimal OverageAmount,
    decimal LaborCost);

/// <summary>The project's time outside its agreements, billed by the hour as reports bill it.</summary>
public sealed record OutsideAgreements(long TrackedSeconds, decimal Amount, decimal LaborCost);

/// <summary>How profitable projects are, against the labor cost of the time spent on them. For admins only.</summary>
internal static class ProfitabilityEndpoints
{
    public static IEndpointRouteBuilder MapProfitabilityEndpoints(this IEndpointRouteBuilder workspace)
    {
        workspace.MapGet("/profitability", GetAsync).WithTags("Agreements").WithName("getProfitability").RequireWorkspaceAdmin();
        return workspace;
    }

    /// <summary>
    /// Every project with time or agreements, over all their time until today, or the one project asked for. Archived
    /// projects are included.
    /// </summary>
    private static async Task<Results<Ok<ProfitabilityResponse>, NotFound, ValidationProblem>> GetAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        RateBook rateBook,
        ReportLoader loader,
        CancellationToken cancellationToken,
        Guid? projectId = null)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.WorkspaceId == workspace.Id && (projectId == null || p.Id == projectId))
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);
        if (projectId is not null && projects.Count == 0)
        {
            return TypedResults.NotFound();
        }

        var today = await rateBook.TodayAsync(cancellationToken);
        var agreements = await db.Agreements.AsNoTracking()
            .Where(a => a.WorkspaceId == workspace.Id && (projectId == null || a.ProjectId == projectId))
            .ToListAsync(cancellationToken);
        var firstStart = await db.TimeEntries
            .Where(e => e.WorkspaceId == workspace.Id && e.ProjectId != null && (projectId == null || e.ProjectId == projectId))
            .MinAsync(e => (DateTimeOffset?)e.Start, cancellationToken);

        // From the first day anything happened, a day early for time zones, until today.
        var from = today;
        if (firstStart is { } first && DateOnly.FromDateTime(first.UtcDateTime).AddDays(-1) is var day && day < from)
        {
            from = day;
        }

        foreach (var agreement in agreements.Where(a => a.StartsOn < from))
        {
            from = agreement.StartsOn;
        }

        var filter = new ReportFilter { From = from, To = today, ProjectIds = projectId is { } id ? [id] : [] };
        var (data, problem) = await loader.LoadAsync(filter, int.MaxValue, cancellationToken);
        if (data is null)
        {
            return problem!;
        }

        var rates = await rateBook.CardAsync(cancellationToken);
        var taskIds = data.Agreements.TaskAgreements.ToLookup(t => t.Value, t => t.Key);
        var rowsByProject = data.Entries.Where(e => e.ProjectId is not null).ToLookup(e => e.ProjectId!.Value);
        var agreementsByProject = agreements.ToLookup(a => a.ProjectId);

        var result = projects
            .Where(p => projectId is not null || rowsByProject[p.Id].Any() || agreementsByProject[p.Id].Any())
            .Select(p =>
            {
                var rows = rowsByProject[p.Id].ToList();
                var items = agreementsByProject[p.Id]
                    .OrderBy(a => a.StartsOn)
                    .ThenBy(a => a.Name, StringComparer.InvariantCultureIgnoreCase)
                    .Select(a => Of(a, rows, data.Ledger, rates, taskIds[a.Id].ToList(), today))
                    .ToList();
                var outsideRows = rows.Where(r => r.AgreementId is null).ToList();
                var outside = new OutsideAgreements(
                    outsideRows.Sum(r => r.Seconds),
                    outsideRows.Sum(r => r.Amount ?? 0),
                    outsideRows.Sum(r => r.LaborCost ?? 0));
                var revenue = items.Sum(a => a.Revenue) + outside.Amount;
                var cost = rows.Sum(r => r.LaborCost ?? 0);
                return new ProjectProfitability(
                    p.Id,
                    p.Name,
                    p.Color,
                    p.ClientId,
                    p.ArchivedAt is not null,
                    rows.Sum(r => r.Seconds),
                    revenue,
                    cost,
                    revenue - cost,
                    items,
                    outside);
            })
            .ToList();

        return TypedResults.Ok(new ProfitabilityResponse(data.Currency, result));
    }

    private static AgreementProfitability Of(
        Agreement agreement,
        IReadOnlyCollection<ReportEntry> projectRows,
        AgreementLedger ledger,
        RateCard rates,
        IReadOnlyList<Guid> taskIds,
        DateOnly today)
    {
        var time = projectRows.Where(r => r.AgreementId == agreement.Id && r.Kind == ReportEntryKind.Time).ToList();
        var paid = projectRows.Where(r => r.AgreementId == agreement.Id && r.Kind == ReportEntryKind.Agreement).Sum(r => r.Amount ?? 0);
        var overage = time.Sum(r => r.Amount ?? 0);
        var cost = time.Sum(r => r.LaborCost ?? 0);
        var revenue = agreement.Kind == AgreementKind.FixedPrice ? agreement.Amount ?? 0 : paid + overage;
        var periods = ledger.Usage(agreement.Id)
            .Select(usage =>
            {
                var inPeriod = time.Where(r => usage.Period.Contains(r.Day)).ToList();
                return new RetainerPeriodResponse(
                    usage.Period.Start,
                    usage.Period.End,
                    usage.Seconds,
                    usage.IncludedSeconds,
                    usage.OverSeconds,
                    agreement.Fee(usage.Period.Start, rates),
                    inPeriod.Sum(r => r.Amount ?? 0),
                    inPeriod.Sum(r => r.LaborCost ?? 0));
            })
            .ToList();

        return new AgreementProfitability(
            AgreementEndpoints.ToResponse(agreement, taskIds, today),
            time.Sum(r => r.Seconds),
            revenue,
            cost,
            revenue - cost,
            overage,
            periods);
    }
}
