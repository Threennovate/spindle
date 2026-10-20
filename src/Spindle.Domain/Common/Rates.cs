namespace Spindle.Domain.Common;

/// <summary>Money per hour: billable rates and labor costs.</summary>
public static class Rates
{
    public const decimal Max = 1_000_000m;

    public static decimal? Check(decimal? rate, string field)
    {
        if (rate is { } value && (value < 0 || value > Max || decimal.Round(value, 2) != value))
        {
            throw new DomainValidationException(field, "Enter an amount per hour from 0 with at most two decimals.");
        }

        return rate;
    }
}
