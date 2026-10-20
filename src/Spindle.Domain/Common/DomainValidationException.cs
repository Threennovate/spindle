namespace Spindle.Domain.Common;

/// <summary>
/// A business rule was broken by input from the caller. <see cref="Field"/> is the JSON name of the offending
/// field (e.g. <c>stop</c>), so the API can return the message next to that field.
/// </summary>
public sealed class DomainValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
