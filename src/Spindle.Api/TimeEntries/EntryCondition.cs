using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Spindle.Domain.TimeEntries;

namespace Spindle.Api.TimeEntries;

/// <summary>
/// The versions a write may change, from its <c>If-Match</c> header: the entry's <c>version</c> as the client last saw
/// it. A write without one (or with <c>*</c>) goes ahead as it always has, for apps that predate it.
/// </summary>
/// <remarks>
/// The version is PostgreSQL's <c>xmin</c>, which EF Core also puts in the UPDATE's WHERE clause, so a change saved
/// between reading the entry and writing it fails the write too (<see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>).
/// </remarks>
internal sealed class EntryCondition
{
    public const string ProblemType = "urn:spindle:problem:entry-changed";

    private readonly HashSet<string> _versions;

    private EntryCondition(HashSet<string> versions) => _versions = versions;

    /// <summary>The condition, or null when the request has none.</summary>
    public static EntryCondition? Parse(string? ifMatch)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return null;
        }

        // Entity tags as in RFC 9110 ("12345", W/"12345"), or the bare version; anything else matches no version.
        var versions = ifMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag)
            .Select(tag => tag.Trim('"'))
            .ToHashSet(StringComparer.Ordinal);
        return versions.Contains("*") ? null : new EntryCondition(versions);
    }

    public bool Allows(TimeEntry entry) => _versions.Contains(entry.Version.ToString(CultureInfo.InvariantCulture));

    /// <summary>The 412 answer, with the entry as it is now.</summary>
    public static ProblemHttpResult Changed(TimeEntry current) => TypedResults.Problem(
        statusCode: StatusCodes.Status412PreconditionFailed,
        type: ProblemType,
        title: "This entry changed since you saw it.",
        detail: "It was changed on another device or by someone else. Choose which version to keep.",
        extensions: new Dictionary<string, object?> { ["current"] = TimeEntryResponse.From(current) });
}

internal static class EntryConditionExtensions
{
    /// <summary>Documents the 412 a write with a stale <c>If-Match</c> gets.</summary>
    public static RouteHandlerBuilder ProducesEntryChanged(this RouteHandlerBuilder builder) =>
        builder.Produces<TimeEntryChangedProblem>(StatusCodes.Status412PreconditionFailed, "application/problem+json");
}
