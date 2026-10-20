using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Workspaces;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.TimeEntries;

/// <summary>The workspace's rules for time entries: locked days and required details.</summary>
internal sealed class TimeEntryRules(AppDbContext db, WorkspacePolicy policy, CurrentWorkspace current)
{
    private DateTimeZone? zone;

    /// <summary>
    /// Members cannot add, change or remove time on days before the workspace's lock date, counted in their own time
    /// zone so the lock falls on the same calendar day everywhere. Admins can.
    /// </summary>
    public async Task<string?> LockedAsync(DateTimeOffset start, CancellationToken cancellationToken)
    {
        if (policy.IsAdmin || (await policy.SettingsAsync(cancellationToken)).LockedBefore is null)
        {
            return null;
        }

        return await LockedAsync(Instant.FromDateTimeOffset(start).InZone(await ZoneAsync(cancellationToken)).Date.ToDateOnly(), cancellationToken);
    }

    /// <summary>Whether a day of the caller's calendar is locked for them, e.g. for billed time.</summary>
    public async Task<string?> LockedAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (policy.IsAdmin || (await policy.SettingsAsync(cancellationToken)).LockedBefore is not { } lockedBefore)
        {
            return null;
        }

        return day < lockedBefore
            ? $"Time before {lockedBefore.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} is locked. Ask an admin to change it."
            : null;
    }

    /// <summary>The details a finished entry must have in this workspace, keyed by field.</summary>
    public async Task<Dictionary<string, string[]>> MissingDetailsAsync(TimeEntryDetails details, CancellationToken cancellationToken)
    {
        var settings = await policy.SettingsAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();

        // Parts with descriptions of their own each need one, since each project's reports show only its own.
        if (settings.RequireDescription && details.Shares is { Count: > 1 } described && TimeShare.AreDescribed(described))
        {
            foreach (var (share, i) in described.Select((share, i) => (share, i)).Where(p => string.IsNullOrWhiteSpace(p.share.Description)))
            {
                errors[$"shares[{i}].description"] = ["A description is required in this workspace."];
            }
        }
        else if (settings.RequireDescription && string.IsNullOrWhiteSpace(details.Description))
        {
            errors["description"] = ["A description is required in this workspace."];
        }

        // Each part of a split entry needs what a whole entry would.
        var parts = details.Shares is { Count: > 1 } shares
            ? shares.Select((share, i) => (Prefix: $"shares[{i}].", share.ProjectId, share.TaskId))
            : [(Prefix: string.Empty, details.ProjectId, details.TaskId)];
        foreach (var (prefix, projectId, taskId) in parts)
        {
            if (settings.RequireProject && projectId is null)
            {
                errors[$"{prefix}projectId"] = ["A project is required in this workspace."];
            }

            if (settings.RequireTask && taskId is null && projectId is { } id
                && await db.Tasks.AnyAsync(t => t.ProjectId == id && !t.IsDone, cancellationToken))
            {
                errors[$"{prefix}taskId"] = ["A task is required on this project."];
            }
        }

        if (settings.RequireTag && details.TagIds.Count == 0)
        {
            errors["tagIds"] = ["At least one tag is required in this workspace."];
        }

        return errors;
    }

    public static TimeEntryDetails DetailsOf(TimeEntry entry) =>
        new(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, entry.IsBillable, entry.Shares);

    private async Task<DateTimeZone> ZoneAsync(CancellationToken cancellationToken)
    {
        if (zone is null)
        {
            var id = await db.Users.Where(u => u.Id == current.UserId).Select(u => u.TimeZone).SingleAsync(cancellationToken);
            zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? DateTimeZone.Utc;
        }

        return zone;
    }
}
