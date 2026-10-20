using Spindle.Domain.Billing;
using Spindle.Domain.Common;

namespace Spindle.Tests.Domain;

public sealed class AgreementTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();

    [Fact]
    public void Monthly_periods_start_on_the_same_day_each_month_or_the_last_day_of_a_shorter_one()
    {
        var retainer = Retainer(new DateOnly(2026, 1, 31));

        Assert.Equal(
            [
                new RetainerPeriod(new(2026, 1, 31), new(2026, 2, 27)),
                new RetainerPeriod(new(2026, 2, 28), new(2026, 3, 30)),
                new RetainerPeriod(new(2026, 3, 31), new(2026, 4, 29)),
            ],
            retainer.Periods(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 15)));
    }

    [Fact]
    public void A_retainer_that_ends_early_ends_its_last_period_with_it()
    {
        var retainer = Retainer(new DateOnly(2026, 9, 16), endsOn: new DateOnly(2026, 11, 20));

        Assert.Equal(
            [
                new RetainerPeriod(new(2026, 9, 16), new(2026, 10, 15)),
                new RetainerPeriod(new(2026, 10, 16), new(2026, 11, 15)),
                new RetainerPeriod(new(2026, 11, 16), new(2026, 11, 20)),
            ],
            retainer.Periods(new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(new RetainerPeriod(new(2026, 10, 16), new(2026, 11, 15)), retainer.PeriodOf(new DateOnly(2026, 10, 20)));
        Assert.Null(retainer.PeriodOf(new DateOnly(2026, 11, 21)));
        Assert.Null(retainer.PeriodOf(new DateOnly(2026, 9, 15)));
        Assert.False(retainer.IsOver(new DateOnly(2026, 11, 20)));
        Assert.True(retainer.IsOver(new DateOnly(2026, 11, 21)));
    }

    [Fact]
    public void Yearly_periods_run_from_one_anniversary_to_the_day_before_the_next()
    {
        var retainer = Retainer(new DateOnly(2025, 3, 1), period: AgreementPeriod.Year);

        Assert.Equal(
            [new RetainerPeriod(new(2026, 3, 1), new(2027, 2, 28))],
            retainer.Periods(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void A_fixed_price_needs_a_price_and_a_retainer_a_fee_or_hours()
    {
        Assert.Equal("amount", Invalid(new AgreementDetails("Website", AgreementKind.FixedPrice, null, 40m, null, new(2026, 6, 1), null, false)));
        Assert.Equal("period", Invalid(new AgreementDetails("Upkeep", AgreementKind.Retainer, 300m, null, null, new(2026, 6, 1), null, false)));
        Assert.Equal("amount", Invalid(new AgreementDetails("Upkeep", AgreementKind.Retainer, null, null, AgreementPeriod.Month, new(2026, 6, 1), null, false)));
        Assert.Equal("endsOn", Invalid(new AgreementDetails("Website", AgreementKind.FixedPrice, 2400m, null, null, new(2026, 6, 1), new(2026, 5, 31), false)));
        Assert.Equal("hours", Invalid(new AgreementDetails("Upkeep", AgreementKind.Retainer, null, 0m, AgreementPeriod.Month, new(2026, 6, 1), null, false)));
        Assert.Equal("amount", Invalid(new AgreementDetails("Website", AgreementKind.FixedPrice, 10.005m, null, null, new(2026, 6, 1), null, false)));

        // A fixed price drops what only retainers have.
        var website = Agreement.Create(Workspace, Project, new AgreementDetails("Website", AgreementKind.FixedPrice, 2400m, 40m, AgreementPeriod.Month, new(2026, 6, 1), null, true));
        Assert.Equal((null, false, false), (website.Period, website.BillsOverage, website.IsOver(new(2030, 1, 1))));
        Assert.Empty(website.Periods(new(2026, 1, 1), new(2030, 1, 1)));
    }

    [Fact]
    public void A_retainer_with_hours_and_no_fee_is_paid_for_its_hours_at_the_project_rate()
    {
        var retainer = Agreement.Create(Workspace, Project, new AgreementDetails("Upkeep", AgreementKind.Retainer, null, 5m, AgreementPeriod.Month, new(2026, 9, 1), null, false));
        var rates = new RateCard([new(RateScope.WorkspaceDefault, null, 40m), new(RateScope.Project(Project), new DateOnly(2026, 10, 1), 60m)]);

        Assert.Equal(200m, retainer.Fee(new DateOnly(2026, 9, 1), rates));
        Assert.Equal(300m, retainer.Fee(new DateOnly(2026, 10, 1), rates));
        Assert.Null(retainer.Fee(new DateOnly(2026, 9, 1), RateCard.Empty));
        Assert.Equal(300m, Retainer(new DateOnly(2026, 9, 1)).Fee(new DateOnly(2026, 9, 1), RateCard.Empty));
    }

    [Fact]
    public void Hours_are_used_up_in_the_order_time_was_tracked_and_start_again_each_period()
    {
        var retainer = Retainer(new DateOnly(2026, 9, 1), hours: 5m);
        var first = Time(retainer, new DateTime(2026, 9, 2, 9, 0, 0), 3);
        var crossing = Time(retainer, new DateTime(2026, 9, 3, 9, 0, 0), 3);
        var over = Time(retainer, new DateTime(2026, 9, 4, 9, 0, 0), 1);
        var nextMonth = Time(retainer, new DateTime(2026, 10, 1, 9, 0, 0), 2);

        var ledger = AgreementLedger.Build([retainer], [over, nextMonth, crossing, first], new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31));

        Assert.Equal([0L, 3600, 3600, 0], new[] { first, crossing, over, nextMonth }.Select(t => ledger.OverSeconds(t.EntryId)));
        Assert.Equal(
            [(7 * 3600L, 2 * 3600L), (2 * 3600L, 0L)],
            ledger.Usage(retainer.Id).Select(u => (u.Seconds, u.OverSeconds)));
        Assert.All(ledger.Usage(retainer.Id), u => Assert.Equal(5 * 3600L, u.IncludedSeconds));
    }

    [Fact]
    public void A_retainer_without_hours_has_no_limit_to_go_over()
    {
        var retainer = Retainer(new DateOnly(2026, 9, 1));
        var time = Time(retainer, new DateTime(2026, 9, 2, 9, 0, 0), 40);

        var ledger = AgreementLedger.Build([retainer], [time], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Equal(0, ledger.OverSeconds(time.EntryId));
        Assert.Equal((40 * 3600L, (long?)null, 0L), ledger.Usage(retainer.Id).Select(u => (u.Seconds, u.IncludedSeconds, u.OverSeconds)).Single());
    }

    private static Agreement Retainer(DateOnly startsOn, DateOnly? endsOn = null, AgreementPeriod period = AgreementPeriod.Month, decimal? hours = null) =>
        Agreement.Create(Workspace, Project, new AgreementDetails("Upkeep", AgreementKind.Retainer, 300m, hours, period, startsOn, endsOn, true));

    private static AgreementTime Time(Agreement agreement, DateTime start, int hours) =>
        new(Guid.NewGuid(), agreement.Id, new DateTimeOffset(start, TimeSpan.Zero), DateOnly.FromDateTime(start), hours * 3600L);

    private static string Invalid(AgreementDetails details) =>
        Assert.Throws<DomainValidationException>(() => Agreement.Create(Workspace, Project, details)).Field;
}
