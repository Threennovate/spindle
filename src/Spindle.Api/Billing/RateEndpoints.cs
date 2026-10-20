using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Billing;

/// <param name="EffectiveFrom">The first day the rate applies to; null for all time before the next period.</param>
/// <param name="Amount">Money per hour; null when there is no rate of this kind from that day on.</param>
public sealed record RatePeriodResponse(Guid Id, DateOnly? EffectiveFrom, decimal? Amount);

/// <summary>
/// The history of each rate, for admins. Rates are set where they belong (workspace settings, members, projects);
/// here their periods are listed and mistakes removed.
/// </summary>
internal static class RateEndpoints
{
    public static IEndpointRouteBuilder MapRateEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/rates").WithTags("Rates").RequireWorkspaceAdmin();

        group.MapGet("", ListAsync).WithName("listRatePeriods");
        group.MapDelete("/{rateId:guid}", DeleteAsync).WithName("deleteRatePeriod");

        return workspace;
    }

    /// <summary>
    /// A rate's periods, newest first. Leave out both ids for the workspace's default rate; give a user for their
    /// rate or labor cost (<c>?kind=laborCost</c>), a project for its rate, or both for the person's rate on the project.
    /// </summary>
    private static async Task<Results<Ok<List<RatePeriodResponse>>, ValidationProblem>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken,
        [FromQuery] string? kind = null,
        Guid? projectId = null,
        Guid? userId = null)
    {
        // In any case, like enums in JSON.
        var rateKind = RateKind.Billable;
        if (kind is not null && (!Enum.TryParse(kind, ignoreCase: true, out rateKind) || !Enum.IsDefined(rateKind) || char.IsDigit(kind[0])))
        {
            return Problems.Validation("kind", "Ask for billable or laborCost rates.");
        }

        var periods = await db.Rates.AsNoTracking()
            .Where(r => r.WorkspaceId == workspace.Id && r.Kind == rateKind && r.ProjectId == projectId && r.UserId == userId)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(periods
            .OrderByDescending(r => r.EffectiveFrom ?? DateOnly.MinValue)
            .Select(r => new RatePeriodResponse(r.Id, r.EffectiveFrom, r.Amount))
            .ToList());
    }

    /// <summary>Removes a period, e.g. one started on the wrong day. The period before it then runs on.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid rateId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var rate = await db.Rates.SingleOrDefaultAsync(r => r.Id == rateId && r.WorkspaceId == workspace.Id, cancellationToken);
        if (rate is null)
        {
            return TypedResults.NotFound();
        }

        db.Rates.Remove(rate);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
