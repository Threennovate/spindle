using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Spindle.Domain.Common;

internal static partial class Guard
{
    public static string Name(string value, int maxLength, [CallerArgumentExpression(nameof(value))] string? field = null)
    {
        ArgumentNullException.ThrowIfNull(value, field);
        field ??= "name";

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException(field, "Name is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(field, $"Name must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static Guid Id(Guid value, [CallerArgumentExpression(nameof(value))] string? field = null)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(field ?? "id", "ID must not be empty.");
        }

        return value;
    }

    public static string Color(string value, [CallerArgumentExpression(nameof(value))] string? field = null)
    {
        if (value is null || !HexColor().IsMatch(value))
        {
            throw new DomainValidationException(field ?? "color", "Color must be a hex color such as #6D28D9.");
        }

        return value.ToUpperInvariant();
    }

    public static TimeSpan? Estimate(TimeSpan? value, [CallerArgumentExpression(nameof(value))] string? field = null)
    {
        if (value is { } estimate && (estimate <= TimeSpan.Zero || estimate > TimeSpan.FromHours(100_000)))
        {
            throw new DomainValidationException(field ?? "estimate", "Estimate must be positive.");
        }

        return value;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}
