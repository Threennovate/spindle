namespace Spindle.Windows.Tests;

/// <summary>
/// Now, to the microsecond, as PostgreSQL keeps it and the tracker sends it. Windows clocks also have tenths of a
/// microsecond, which no write sends back, so a check comparing times made from <c>DateTimeOffset.UtcNow</c> would
/// fail on Windows only.
/// </summary>
internal static class TestClock
{
    public static DateTimeOffset Now => new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
}
