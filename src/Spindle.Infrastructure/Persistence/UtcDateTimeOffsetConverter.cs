using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Spindle.Infrastructure.Persistence;

/// <summary>PostgreSQL stores instants in UTC at microsecond precision.</summary>
public sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => PostgresTimestamp.Normalize(value),
    value => value);

public static class PostgresTimestamp
{
    public static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
}
