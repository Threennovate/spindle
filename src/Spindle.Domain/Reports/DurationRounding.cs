namespace Spindle.Domain.Reports;

public enum RoundingMode
{
    None,
    Nearest,
    Up,
    Down,
}

/// <summary>Rounds each entry's duration in reports, e.g. up to whole quarter hours for billing.</summary>
public static class DurationRounding
{
    public static readonly int[] AllowedMinutes = [1, 5, 6, 10, 12, 15, 30, 60];

    public static long Apply(long seconds, RoundingMode mode, int minutes)
    {
        if (mode == RoundingMode.None)
        {
            return seconds;
        }

        var step = minutes * 60L;
        var steps = (decimal)seconds / step;
        var rounded = mode switch
        {
            RoundingMode.Up => decimal.Ceiling(steps),
            RoundingMode.Down => decimal.Floor(steps),
            _ => decimal.Round(steps, MidpointRounding.AwayFromZero),
        };
        return (long)rounded * step;
    }
}
