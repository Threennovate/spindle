using Spindle.Domain.Common;

namespace Spindle.Domain.Billing;

public enum RateKind
{
    /// <summary>What an hour is billed at.</summary>
    Billable,

    /// <summary>What an hour of a member's time costs.</summary>
    LaborCost,

    /// <summary>
    /// Not money but hours: how long a full working day billed on a project is, for projects billed by the day.
    /// Kept as periods like rates, so a change from a day on leaves earlier days as they were billed.
    /// </summary>
    DailyHours,
}

/// <summary>
/// Whose rate it is: the workspace's default, a member's, a project's, or a member's on a project. Labor costs
/// belong to members only, daily hours to projects only.
/// </summary>
public readonly record struct RateScope(RateKind Kind, Guid? ProjectId, Guid? UserId)
{
    public static RateScope WorkspaceDefault => new(RateKind.Billable, null, null);

    public static RateScope Member(Guid userId) => new(RateKind.Billable, null, userId);

    public static RateScope Project(Guid projectId) => new(RateKind.Billable, projectId, null);

    public static RateScope ProjectMember(Guid projectId, Guid userId) => new(RateKind.Billable, projectId, userId);

    public static RateScope LaborCost(Guid userId) => new(RateKind.LaborCost, null, userId);

    public static RateScope DailyHours(Guid projectId) => new(RateKind.DailyHours, projectId, null);
}

/// <summary>A rate from a day on, until the next one of the same scope starts.</summary>
/// <param name="EffectiveFrom">The first day it applies to, or null for all time before the next one.</param>
/// <param name="Amount">Money per hour, or null for no rate of this scope, so the next less specific one applies.</param>
public readonly record struct RatePeriod(RateScope Scope, DateOnly? EffectiveFrom, decimal? Amount);

/// <summary>
/// One period of a rate. A scope's periods together are its history: changing a rate from a day on starts a new
/// period, so time before that day keeps the rate it had.
/// </summary>
public sealed class Rate : Entity
{
    private Rate()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public RateKind Kind { get; private set; }

    public Guid? ProjectId { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>The first day the rate applies to, in the viewer's calendar; null for all time before the next period.</summary>
    public DateOnly? EffectiveFrom { get; private set; }

    /// <summary>Money per hour; null when the scope has no rate from this day on.</summary>
    public decimal? Amount { get; private set; }

    public RateScope Scope => new(Kind, ProjectId, UserId);

    public RatePeriod Period => new(Scope, EffectiveFrom, Amount);

    public static Rate Create(Guid workspaceId, RateScope scope, DateOnly? effectiveFrom, decimal? amount, string field)
    {
        if (scope.Kind == RateKind.LaborCost && (scope.UserId is null || scope.ProjectId is not null))
        {
            throw new ArgumentException("Labor costs belong to members.", nameof(scope));
        }

        if (scope.Kind == RateKind.DailyHours && (scope.ProjectId is null || scope.UserId is not null))
        {
            throw new ArgumentException("Daily hours belong to projects.", nameof(scope));
        }

        return new Rate
        {
            WorkspaceId = Guard.Id(workspaceId),
            Kind = scope.Kind,
            ProjectId = scope.ProjectId,
            UserId = scope.UserId,
            EffectiveFrom = effectiveFrom,
            Amount = Check(scope.Kind, amount, field),
        };
    }

    public void Change(decimal? amount, string field) => Amount = Check(Kind, amount, field);

    /// <summary>Money per hour, or for daily hours, a day of more than nothing and at most 24 hours.</summary>
    public static decimal? Check(RateKind kind, decimal? amount, string field)
    {
        if (kind == RateKind.DailyHours && amount is { } hours && (hours <= 0 || hours > 24 || decimal.Round(hours, 2) != hours))
        {
            throw new DomainValidationException(field, "Enter the hours of a working day, more than 0 and at most 24.");
        }

        return Rates.Check(amount, field);
    }
}
