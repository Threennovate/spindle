using Spindle.Domain.Billing;
using Spindle.Domain.Reports;

namespace Spindle.Tests.Domain;

public sealed class ReportMathTests
{
    [Theory]
    [InlineData(RoundingMode.None, 15, 7 * 60 + 29, 7 * 60 + 29)]
    [InlineData(RoundingMode.Nearest, 15, 7 * 60 + 29, 0)]
    [InlineData(RoundingMode.Nearest, 15, 7 * 60 + 30, 15 * 60)]
    [InlineData(RoundingMode.Up, 15, 1, 15 * 60)]
    [InlineData(RoundingMode.Up, 15, 15 * 60, 15 * 60)]
    [InlineData(RoundingMode.Down, 15, 29 * 60 + 59, 15 * 60)]
    [InlineData(RoundingMode.Up, 6, 61 * 60, 66 * 60)]
    [InlineData(RoundingMode.Up, 60, 0, 0)]
    public void Durations_round_per_entry(RoundingMode mode, int minutes, long seconds, long expected) =>
        Assert.Equal(expected, DurationRounding.Apply(seconds, mode, minutes));

    [Fact]
    public void The_most_specific_rate_wins()
    {
        var ana = Guid.NewGuid();
        var ivo = Guid.NewGuid();
        var marko = Guid.NewGuid();
        var website = Guid.NewGuid();
        var internalWork = Guid.NewGuid();
        var day = new DateOnly(2026, 9, 14);
        var rates = new RateCard(
        [
            new(RateScope.WorkspaceDefault, null, 50m),
            new(RateScope.Member(ana), null, 70m),
            new(RateScope.LaborCost(ana), null, 30m),
            new(RateScope.LaborCost(ivo), null, 25m),
            new(RateScope.Project(website), null, 100m),
            new(RateScope.Project(internalWork), null, null),
            new(RateScope.ProjectMember(website, ana), null, 120m),
            new(RateScope.ProjectMember(website, ivo), null, null),
        ]);

        Assert.Equal(120m, rates.RateFor(ana, website, day));
        Assert.Equal(100m, rates.RateFor(ivo, website, day));
        Assert.Equal(70m, rates.RateFor(ana, internalWork, day));
        Assert.Equal(50m, rates.RateFor(ivo, internalWork, day));
        Assert.Equal(50m, rates.RateFor(marko, null, day));
        Assert.Equal(30m, rates.LaborCostFor(ana, day));
        Assert.Null(rates.LaborCostFor(marko, day));
        Assert.Null(RateCard.Empty.RateFor(ana, website, day));
    }

    [Fact]
    public void Each_day_takes_the_rate_that_applied_then()
    {
        var ana = Guid.NewGuid();
        var website = Guid.NewGuid();
        var rates = new RateCard(
        [
            new(RateScope.WorkspaceDefault, null, 50m),
            new(RateScope.Project(website), new DateOnly(2026, 10, 1), 110m),
            new(RateScope.Project(website), null, 100m),
            // Ana's rate on the project ran in September only.
            new(RateScope.ProjectMember(website, ana), new DateOnly(2026, 9, 1), 120m),
            new(RateScope.ProjectMember(website, ana), new DateOnly(2026, 10, 1), null),
            new(RateScope.LaborCost(ana), new DateOnly(2026, 9, 15), 35m),
            new(RateScope.LaborCost(ana), null, 30m),
        ]);

        Assert.Equal(100m, rates.RateFor(ana, website, new DateOnly(2026, 8, 31)));
        Assert.Equal(120m, rates.RateFor(ana, website, new DateOnly(2026, 9, 30)));
        Assert.Equal(110m, rates.RateFor(ana, website, new DateOnly(2026, 10, 1)));
        Assert.Equal(50m, rates.RateFor(ana, null, new DateOnly(2026, 10, 1)));
        Assert.Equal((30m, 35m), (rates.LaborCostFor(ana, new DateOnly(2026, 9, 14))!.Value, rates.LaborCostFor(ana, new DateOnly(2026, 9, 15))!.Value));
        Assert.Equal((110m, (decimal?)null), (rates.Current(RateScope.Project(website))!.Value, rates.Current(RateScope.ProjectMember(website, ana))));
        Assert.Equal([null, new DateOnly(2026, 10, 1)], rates.History(RateScope.Project(website)).Select(p => p.EffectiveFrom));
    }

    [Fact]
    public void A_rate_that_starts_later_does_not_cover_the_time_before()
    {
        var ana = Guid.NewGuid();
        var rates = new RateCard([new(RateScope.Member(ana), new DateOnly(2026, 9, 1), 70m), new(RateScope.WorkspaceDefault, null, 50m)]);

        Assert.Equal(50m, rates.RateFor(ana, null, new DateOnly(2026, 8, 31)));
        Assert.Equal(70m, rates.RateFor(ana, null, new DateOnly(2026, 9, 1)));
    }

    [Theory]
    [InlineData(60, 3600, 60)]
    [InlineData(60, 1800, 30)]
    [InlineData(100, 20, 0.56)]
    [InlineData(72.5, 5400, 108.75)]
    [InlineData(1, 18, 0.01)]
    public void Amounts_are_to_the_cent(decimal perHour, long seconds, decimal expected) =>
        Assert.Equal(expected, RateCard.Amount(perHour, seconds));
}
