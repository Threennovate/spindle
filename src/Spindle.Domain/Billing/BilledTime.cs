using Spindle.Domain.Common;
using Spindle.Domain.Projects;

namespace Spindle.Domain.Billing;

/// <summary>What is billed for someone's day on a project when nothing was entered for it.</summary>
public static class BilledDays
{
    public static bool IsWorkingDay(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// On projects billed by the full day, a working day with any time tracked on the project is a full day of the
    /// daily hours that apply then. Otherwise, and without daily hours, the billable time tracked.
    /// </summary>
    /// <param name="trackedAny">Whether they tracked any time on the project that day, billable or not.</param>
    public static long Default(BillingMode mode, decimal? dailyHours, DateOnly day, bool trackedAny, long trackedBillableSeconds) =>
        mode == BillingMode.FullDay && dailyHours is { } hours && trackedAny && IsWorkingDay(day)
            ? (long)decimal.Round(hours * 3600)
            : trackedBillableSeconds;
}

/// <summary>
/// A member's day on a project whose billed time is entered separately, when it is not simply the billable time they
/// tracked: how much of it is billed, e.g. to include calls that nobody tracked, and what the client is told was done
/// that day, in place of the descriptions of the day's entries. Either may be left out. It counts only while the
/// project has billed time entered separately.
/// </summary>
public sealed class BilledTime : Entity
{
    public const long MaxSeconds = 24 * 3600;
    public const int DescriptionMaxLength = 2000;

    private BilledTime()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The day, in the calendar of whoever entered it.</summary>
    public DateOnly Day { get; private set; }

    /// <summary>The time billed on the day; null where the day is billed as it would be without an entry.</summary>
    public long? Seconds { get; private set; }

    /// <summary>What was done that day, as the client sees it; null where the day's entries describe it.</summary>
    public string? Description { get; private set; }

    /// <summary>Whether nothing is entered for the day any more, so the row can go.</summary>
    public bool IsEmpty => Seconds is null && Description is null;

    public static BilledTime Create(Guid workspaceId, Guid projectId, Guid userId, DateOnly day) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        ProjectId = Guard.Id(projectId),
        UserId = Guard.Id(userId),
        Day = day,
    };

    public void Change(long? seconds) => Seconds = seconds is { } value ? Check(value) : null;

    /// <summary>Sets what the client is told was done; blank text leaves it to the day's entries again.</summary>
    public void Describe(string? description)
    {
        var text = description?.Trim();
        Description = string.IsNullOrEmpty(text)
            ? null
            : text.Length <= DescriptionMaxLength
                ? text
                : throw new DomainValidationException("description", $"Keep it to {DescriptionMaxLength} characters.");
    }

    private static long Check(long seconds) =>
        seconds is < 0 or > MaxSeconds
            ? throw new DomainValidationException("seconds", "Enter from 0 to 24 hours.")
            : seconds;
}
