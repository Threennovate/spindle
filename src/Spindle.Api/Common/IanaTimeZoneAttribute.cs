using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Spindle.Api.Common;

/// <summary>
/// Accepts IANA time zone IDs such as <c>Europe/Zagreb</c>. Checked against NodaTime's bundled TZDB,
/// so the result does not depend on the time zone data of the server's operating system.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
internal sealed class IanaTimeZoneAttribute() : ValidationAttribute("The {0} field must be an IANA time zone, e.g. Europe/Zagreb.")
{
    public override bool IsValid(object? value) =>
        value is null || (value is string id && DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) is not null);
}
