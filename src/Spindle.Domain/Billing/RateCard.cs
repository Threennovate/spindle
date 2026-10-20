namespace Spindle.Domain.Billing;

/// <summary>
/// A workspace's billable rates and labor costs over time. The rate for time on a day is the first one set, on that
/// day, of: the person's rate on the project, the project's rate, the person's rate in the workspace, and the
/// workspace's default rate.
/// </summary>
public sealed class RateCard
{
    private readonly Dictionary<RateScope, RatePeriod[]> history;

    public RateCard(IEnumerable<RatePeriod> periods)
    {
        history = periods
            .GroupBy(p => p.Scope)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.EffectiveFrom ?? DateOnly.MinValue).ToArray());
    }

    public static RateCard Empty { get; } = new([]);

    /// <summary>The scope's own rate on the day, if it has one then.</summary>
    public decimal? At(RateScope scope, DateOnly day)
    {
        if (!history.TryGetValue(scope, out var periods))
        {
            return null;
        }

        decimal? amount = null;
        foreach (var period in periods)
        {
            if ((period.EffectiveFrom ?? DateOnly.MinValue) > day)
            {
                break;
            }

            amount = period.Amount;
        }

        return amount;
    }

    /// <summary>The scope's rate as it was last set: what applies from its newest period on.</summary>
    public decimal? Current(RateScope scope) =>
        history.TryGetValue(scope, out var periods) ? periods[^1].Amount : null;

    /// <summary>The scope's periods, oldest first.</summary>
    public IReadOnlyList<RatePeriod> History(RateScope scope) =>
        history.TryGetValue(scope, out var periods) ? periods : [];

    public decimal? RateFor(Guid userId, Guid? projectId, DateOnly day)
    {
        if (projectId is { } id)
        {
            if (At(RateScope.ProjectMember(id, userId), day) is { } personOnProject)
            {
                return personOnProject;
            }

            if (At(RateScope.Project(id), day) is { } project)
            {
                return project;
            }
        }

        return At(RateScope.Member(userId), day) ?? At(RateScope.WorkspaceDefault, day);
    }

    public decimal? LaborCostFor(Guid userId, DateOnly day) => At(RateScope.LaborCost(userId), day);

    /// <summary>Money for a duration at an hourly rate, to the cent, so that amounts add up the same in every total.</summary>
    public static decimal Amount(decimal perHour, long seconds) =>
        decimal.Round(perHour * seconds / 3600m, 2, MidpointRounding.AwayFromZero);
}
