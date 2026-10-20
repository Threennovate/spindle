using Spindle.Domain.Common;

namespace Spindle.Domain.Billing;

/// <summary>What kind of deal an agreement is.</summary>
public enum AgreementKind
{
    /// <summary>A price for a piece of work, e.g. building a website. It is done on a day.</summary>
    FixedPrice,

    /// <summary>A fee each month or year, for hours included or for whatever is needed, e.g. maintenance.</summary>
    Retainer,
}

/// <summary>How often a retainer's fee is paid and its hours start again.</summary>
public enum AgreementPeriod
{
    Month,
    Year,
}

/// <summary>One month or year of a retainer, from its first day to its last, both included.</summary>
public readonly record struct RetainerPeriod(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly day) => day >= Start && day <= End;
}

/// <summary>
/// What was agreed with the client for part of a project, in place of billing its time by the hour: a fixed price for
/// a piece of work, or a retainer. Time counts towards an agreement when it is tracked on one of its tasks, so the
/// apps offer an agreement wherever they offer tasks. Profit is what the client pays less the labor cost of that time.
/// </summary>
public sealed class Agreement : Entity
{
    public const int NameMaxLength = 100;

    public const decimal MaxAmount = 100_000_000m;

    public const decimal MaxHours = 100_000m;

    private Agreement()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ProjectId { get; private set; }

    public string Name { get; private set; }

    public AgreementKind Kind { get; private set; }

    /// <summary>The price of a fixed-price agreement, or a retainer's fee each period. Required for a fixed price.</summary>
    public decimal? Amount { get; private set; }

    /// <summary>
    /// The hours quoted for a fixed price, or included each period of a retainer. A retainer with hours but no fee is
    /// paid for those hours at the project's rate.
    /// </summary>
    public decimal? Hours { get; private set; }

    /// <summary>How often a retainer's fee is paid; null for a fixed price.</summary>
    public AgreementPeriod? Period { get; private set; }

    /// <summary>The day it starts. A retainer's periods count from it, e.g. from the 1st of each month.</summary>
    public DateOnly StartsOn { get; private set; }

    /// <summary>The day a fixed price was done, or a retainer's last day; null while it goes on.</summary>
    public DateOnly? EndsOn { get; private set; }

    /// <summary>Whether a retainer's time beyond the hours included is billed on top, at the hourly rates.</summary>
    public bool BillsOverage { get; private set; }

    public static Agreement Create(Guid workspaceId, Guid projectId, AgreementDetails details)
    {
        var agreement = new Agreement { WorkspaceId = Guard.Id(workspaceId), ProjectId = Guard.Id(projectId) };
        agreement.Update(details);
        return agreement;
    }

    public void Update(AgreementDetails details)
    {
        var retainer = details.Kind == AgreementKind.Retainer;
        var amount = CheckAmount(details.Amount);
        var hours = CheckHours(details.Hours);

        if (!retainer && amount is null)
        {
            throw new DomainValidationException("amount", "Enter the price agreed.");
        }

        if (retainer && details.Period is null)
        {
            throw new DomainValidationException("period", "Choose whether the fee is monthly or yearly.");
        }

        if (retainer && amount is null && hours is null)
        {
            throw new DomainValidationException("amount", "Enter the fee, the hours included, or both.");
        }

        if (details.EndsOn < details.StartsOn)
        {
            throw new DomainValidationException("endsOn", retainer ? "It ends on or after the day it starts." : "It is done on or after the day it starts.");
        }

        Name = Guard.Name(details.Name, NameMaxLength, "name");
        Kind = details.Kind;
        Amount = amount;
        Hours = hours;
        Period = retainer ? details.Period : null;
        StartsOn = details.StartsOn;
        EndsOn = details.EndsOn;
        BillsOverage = retainer && details.BillsOverage;
    }

    /// <summary>Whether it is over: a fixed price once it is done, a retainer after its last day.</summary>
    public bool IsOver(DateOnly today) => Kind == AgreementKind.FixedPrice ? EndsOn is not null : EndsOn < today;

    /// <summary>The time included in each period of a retainer, in seconds, if it has a limit.</summary>
    public long? IncludedSeconds => Kind == AgreementKind.Retainer && Hours is { } hours ? (long)decimal.Round(hours * 3600) : null;

    /// <summary>
    /// A retainer's periods that overlap the days from <paramref name="from"/> to <paramref name="to"/>, oldest first.
    /// Each starts on the same day of the month (or year) as the retainer; one that ends early ends on its last day.
    /// </summary>
    public IEnumerable<RetainerPeriod> Periods(DateOnly from, DateOnly to)
    {
        if (Period is not { } period)
        {
            yield break;
        }

        var last = EndsOn is { } end && end < to ? end : to;
        for (var k = Math.Max(0, PeriodsBefore(from) - 1); ; k++)
        {
            var start = PeriodStart(period, k);
            if (start > last)
            {
                yield break;
            }

            var next = PeriodStart(period, k + 1).AddDays(-1);
            var current = new RetainerPeriod(start, EndsOn is { } ends && ends < next ? ends : next);
            if (current.End >= from)
            {
                yield return current;
            }
        }
    }

    /// <summary>The retainer's period that the day falls in, if it falls in one.</summary>
    public RetainerPeriod? PeriodOf(DateOnly day)
    {
        foreach (var period in Periods(day, day))
        {
            return period;
        }

        return null;
    }

    /// <summary>
    /// What a retainer's period starting on the day is paid: its fee, or else its hours at the project's rate then
    /// (the workspace's default rate if the project has none).
    /// </summary>
    public decimal? Fee(DateOnly periodStart, RateCard rates)
    {
        if (Kind != AgreementKind.Retainer)
        {
            return null;
        }

        if (Amount is { } amount)
        {
            return amount;
        }

        var rate = rates.At(RateScope.Project(ProjectId), periodStart) ?? rates.At(RateScope.WorkspaceDefault, periodStart);
        return rate is { } perHour && IncludedSeconds is { } seconds ? RateCard.Amount(perHour, seconds) : null;
    }

    private DateOnly PeriodStart(AgreementPeriod period, int k) =>
        period == AgreementPeriod.Year ? StartsOn.AddYears(k) : StartsOn.AddMonths(k);

    /// <summary>How many whole periods there are between the start and the day, roughly; a lower bound to start counting from.</summary>
    private int PeriodsBefore(DateOnly day)
    {
        if (day <= StartsOn)
        {
            return 0;
        }

        var months = (day.Year - StartsOn.Year) * 12 + day.Month - StartsOn.Month;
        return Period == AgreementPeriod.Year ? months / 12 : months;
    }

    private static decimal? CheckAmount(decimal? amount)
    {
        if (amount is { } value && (value < 0 || value > MaxAmount || decimal.Round(value, 2) != value))
        {
            throw new DomainValidationException("amount", "Enter an amount from 0 with at most two decimals.");
        }

        return amount;
    }

    private static decimal? CheckHours(decimal? hours)
    {
        if (hours is { } value && (value <= 0 || value > MaxHours || decimal.Round(value, 2) != value))
        {
            throw new DomainValidationException("hours", "Enter the hours as a number more than 0, e.g. 5 or 7.5.");
        }

        return hours;
    }
}

public sealed record AgreementDetails(
    string Name,
    AgreementKind Kind,
    decimal? Amount,
    decimal? Hours,
    AgreementPeriod? Period,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    bool BillsOverage);
