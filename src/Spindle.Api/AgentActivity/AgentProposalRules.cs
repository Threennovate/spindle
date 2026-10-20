using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.TimeZones;
using Spindle.Api.TimeEntries;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Projects;
using Spindle.Domain.TimeEntries;

namespace Spindle.Api.AgentActivity;

/// <summary>
/// The rules for proposals: reading what an agent sends and refusing what can never be saved, with reasons in words
/// for the agent. They were the rules of the apps' former local MCP, moved to the server so
/// every app and the server's MCP share them. What stands in the way of one later is <see cref="AgentProposalIssues"/>.
/// </summary>
internal static partial class AgentProposalRules
{
    /// <summary>The most proposals one call makes.</summary>
    public const int Limit = 50;

    /// <summary>The longest entry an agent may propose.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(16);

    /// <summary>What the rules go by when they read proposals.</summary>
    /// <param name="Entry">The person's own entry in the workspace with this ID.</param>
    /// <param name="Entries">The person's entries around the times proposed, to tell the same time proposed twice.</param>
    /// <param name="Locked">Why time starting then can't be added or changed, if it can't.</param>
    public sealed record Context(
        Guid WorkspaceId,
        Guid UserId,
        DateTimeZone Zone,
        IReadOnlyList<Project> Projects,
        IReadOnlyList<ProjectTask> Tasks,
        Func<Guid, TimeEntry?> Entry,
        IReadOnlyList<TimeEntry> Entries,
        IReadOnlyList<AgentProposal> Pending,
        Func<DateTimeOffset, string?> Locked,
        DateTimeOffset Now,
        string? Client)
    {
        public Project? Project(Guid id) => Projects.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Something asked for that wasn't proposed, and why; <c>Index</c> is its place in the list.</summary>
    public sealed record Refusal(int Index, string? Id, string Reason);

    public sealed class Plan
    {
        public List<AgentProposal> Proposals { get; } = [];

        public List<Refusal> Refused { get; } = [];
    }

    private sealed class Problem(string message) : Exception(message);

    private static Problem TimeProblem(string field) =>
        new($"{field} must be a time such as 2026-10-12T14:30, in the person's time zone, or with an offset such as 2026-10-12T14:30:00+02:00.");

    /// <summary>Proposals for the new entries asked for.</summary>
    public static Plan PlanEntries(IReadOnlyList<ProposedEntryRequest> entries, Context context)
    {
        var plan = new Plan();
        for (var index = 0; index < entries.Count; index++)
        {
            var item = entries[index];
            try
            {
                var start = Date(item.Start, context.Zone) ?? throw TimeProblem("start");
                var stop = Date(item.Stop, context.Zone) ?? throw TimeProblem("stop");
                CheckRange(start, stop, context);
                CheckDescribed(item.Description, item.Split);
                var work = Work(item.ProjectId, item.TaskId, item.Split, context);
                var description = Description(item.Description) ?? string.Empty;
                var twin = context.Entries.Any(e => e.Start == start && e.Stop == stop && e.ProjectId == work.ProjectId)
                    || context.Pending.Concat(plan.Proposals).Any(p => p.IsNew && p.Fields.Start == start && p.Fields.Stop == stop);
                if (twin)
                {
                    throw new Problem("The same time is already tracked or proposed.");
                }

                plan.Proposals.Add(New(context, null, null, new ProposedFields(start, stop, description, work), item.Reason));
            }
            catch (Problem problem)
            {
                plan.Refused.Add(new(index, null, problem.Message));
            }
        }

        return plan;
    }

    /// <summary>Proposals for the changes asked for. One for an entry that already has a change waiting replaces it.</summary>
    public static Plan PlanChanges(IReadOnlyList<ProposedChangeRequest> changes, Context context)
    {
        var plan = new Plan();
        var seen = new HashSet<string>();
        for (var index = 0; index < changes.Count; index++)
        {
            var item = changes[index];
            try
            {
                if (!seen.Add(item.Id))
                {
                    throw new Problem("The entry was asked for twice; only the first counts.");
                }

                var entry = (Guid.TryParse(item.Id, out var entryId) ? context.Entry(entryId) : null)
                    ?? throw new Problem("There is no such entry among the person's entries. List entries first.");
                if (context.Locked(entry.Start) is not null)
                {
                    throw new Problem("The workspace no longer allows changing entries from that day.");
                }

                var fields = new ProposedFields();
                if (item.Start is { } startText)
                {
                    var start = Date(startText, context.Zone) ?? throw TimeProblem("start");
                    if (start != entry.Start)
                    {
                        fields = fields with { Start = start };
                    }
                }

                if (item.Stop is { } stopText)
                {
                    if (entry.Stop is null)
                    {
                        throw new Problem("The entry is running; stop it with stop_timer.");
                    }

                    var stop = Date(stopText, context.Zone) ?? throw TimeProblem("stop");
                    if (stop != entry.Stop)
                    {
                        fields = fields with { Stop = stop };
                    }
                }

                var from = fields.Start ?? entry.Start;
                if ((fields.Stop ?? entry.Stop) is { } until)
                {
                    CheckRange(from, until, context);
                }
                else if (from > context.Now)
                {
                    throw new Problem("A running entry can't start in the future.");
                }

                CheckDescribed(item.Description, item.Split);
                var describesParts = entry.IsSplit && TimeShare.AreDescribed(entry.Shares);
                // One description for a split described per project makes it one for all parts, even with the same text.
                if (Description(item.Description) is { } description && (description != entry.Description || (describesParts && item.Split is null)))
                {
                    fields = fields with { Description = description };
                }

                if (item.ProjectId is not null || item.TaskId is not null || item.Split is not null)
                {
                    // A task alone stays on the entry's project.
                    var projectId = item.ProjectId ?? (item.Split is null ? entry.ProjectId?.ToString() : null);
                    var work = Work(projectId, item.TaskId, item.Split, context);
                    // A split proposed without descriptions keeps those its parts have, unless one description replaces them.
                    if (describesParts && fields.Description is null && work.Shares.Length > 1 && !TimeShare.AreDescribed(work.Shares))
                    {
                        work = work with
                        {
                            Shares = [.. work.Shares.Select(s => s with
                            {
                                Description = entry.Shares.FirstOrDefault(p => p.ProjectId == s.ProjectId && p.TaskId == s.TaskId)?.Description ?? string.Empty,
                            })],
                        };
                    }

                    if (!work.Parts.SequenceEqual(entry.Parts))
                    {
                        fields = fields with { Work = work };
                    }
                }

                if (fields.IsEmpty)
                {
                    throw new Problem("That is how the entry already is.");
                }

                plan.Proposals.Add(New(context, entry.Id, ProposedEntrySnapshot.Of(entry), fields, item.Reason));
            }
            catch (Problem problem)
            {
                plan.Refused.Add(new(index, item.Id, problem.Message));
            }
        }

        return plan;
    }

    /// <summary>
    /// What saving the proposal sends: the entry as it is now with the proposed parts, or the new entry. A split whose
    /// parts have their own descriptions is saved with them, the entry's description made of them; one description
    /// proposed for a split is one for all of its parts again.
    /// </summary>
    public static CreateTimeEntryRequest Request(AgentProposal proposal, TimeEntry? current)
    {
        var fields = proposal.Fields;
        var request = current is null
            ? new CreateTimeEntryRequest { Description = string.Empty, TagIds = [], IsBillable = false, Start = fields.Start, Stop = fields.Stop, Shares = [] }
            : new CreateTimeEntryRequest
            {
                Description = current.Description,
                ProjectId = current.ProjectId,
                TaskId = current.TaskId,
                TagIds = current.TagIds,
                IsBillable = current.IsBillable,
                Start = current.Start,
                Stop = current.Stop,
                Shares = current.IsSplit ? [.. current.Shares.Select(ShareRequest)] : [],
            };
        if (fields.Start is { } start)
        {
            request = request with { Start = start };
        }

        if (fields.Stop is { } stop)
        {
            request = request with { Stop = stop };
        }

        if (fields.Description is { } description)
        {
            request = request with { Description = description, Shares = [.. request.Shares!.Select(s => s with { Description = null })] };
        }

        if (fields.Work is { } work)
        {
            request = work.Shares.Length > 1
                ? request with
                {
                    ProjectId = Largest(work.Shares).ProjectId,
                    TaskId = Largest(work.Shares).TaskId,
                    IsBillable = Largest(work.Shares).IsBillable,
                    Shares = [.. work.Shares.Select(ShareRequest)],
                }
                : request with { ProjectId = work.ProjectId, TaskId = work.TaskId, IsBillable = work.IsBillable, Shares = [] };
        }

        if (request.Shares!.Length > 1)
        {
            var described = request.Shares.Any(s => s.Description is not null);
            request = request with { DescribeParts = described };
            if (described)
            {
                var shares = request.Shares.Select(s => s with { Description = s.Description ?? string.Empty }).ToArray();
                request = request with
                {
                    Shares = shares,
                    Description = string.Join("; ", shares.Select(s => s.Description).Where(d => !string.IsNullOrEmpty(d))),
                };
            }
        }

        return request;
    }

    /// <summary>The part the server keeps as the entry's own project: the largest, the first of equal ones.</summary>
    public static TimeShare Largest(IReadOnlyList<TimeShare> parts) => parts.First(p => p.Percent == parts.Max(q => q.Percent));

    private static TimeShareRequest ShareRequest(TimeShare share) => new()
    {
        ProjectId = share.ProjectId, TaskId = share.TaskId, IsBillable = share.IsBillable, Percent = share.Percent, Description = share.Description,
    };

    private static AgentProposal New(Context context, Guid? entryId, ProposedEntrySnapshot? original, ProposedFields fields, string? reason) => new()
    {
        WorkspaceId = context.WorkspaceId,
        UserId = context.UserId,
        EntryId = entryId,
        Original = original,
        Fields = fields,
        Reason = Reason(reason),
        Client = context.Client,
        ProposedAt = context.Now,
        Status = AgentProposalStatus.Pending,
    };

    private static void CheckRange(DateTimeOffset start, DateTimeOffset stop, Context context)
    {
        if (stop <= start)
        {
            throw new Problem("stop must be after start.");
        }

        if (stop - start > Longest)
        {
            throw new Problem($"An entry may be at most {(int)Longest.TotalHours} hours long; propose several.");
        }

        if (stop > context.Now.AddSeconds(60))
        {
            throw new Problem("Time can't be proposed for the future.");
        }

        if (context.Locked(start) is not null)
        {
            throw new Problem("The workspace no longer allows entries on that day.");
        }
    }

    private static ProposedWork Work(string? projectText, string? taskText, ProposedPartRequest[]? split, Context context)
    {
        if (split is { Length: > 1 })
        {
            if (split.Length > TimeShare.MaxShares)
            {
                throw new Problem($"A split has at most {TimeShare.MaxShares} parts.");
            }

            if (split.Sum(p => p.Percent) != 100 || split.Any(p => p.Percent < 1))
            {
                throw new Problem("The parts of a split are whole percentages, each at least 1, that add up to 100.");
            }

            var parts = new List<TimeShare>();
            foreach (var part in split)
            {
                var one = Work(part.ProjectId, part.TaskId, null, context);
                var value = new TimeShare(one.ProjectId, one.TaskId, one.IsBillable, part.Percent, Description(part.Description));
                if (parts.Any(p => p.ProjectId == value.ProjectId && p.TaskId == value.TaskId))
                {
                    throw new Problem("A split can't have the same project and task twice.");
                }

                parts.Add(value);
            }

            var largest = Largest(parts);
            return new ProposedWork(largest.ProjectId, largest.TaskId, largest.IsBillable, [.. parts]);
        }

        var only = split?.FirstOrDefault();
        projectText = only?.ProjectId ?? projectText;
        taskText = only?.TaskId ?? taskText;
        if (projectText is null)
        {
            if (taskText is not null)
            {
                throw new Problem("task_id needs the project it belongs to.");
            }

            return new ProposedWork(null, null, false, []);
        }

        var project = Guid.TryParse(projectText, out var projectId) ? context.Project(projectId) : null;
        if (project is null || project.ArchivedAt is not null)
        {
            throw new Problem("There is no such open project. List projects first.");
        }

        Guid? taskId = null;
        if (taskText is not null)
        {
            var task = Guid.TryParse(taskText, out var id) ? context.Tasks.FirstOrDefault(t => t.Id == id && t.ProjectId == project.Id) : null;
            if (task is null || task.IsDone)
            {
                throw new Problem("task_id must be an open task of that project.");
            }

            taskId = task.Id;
        }

        return new ProposedWork(project.Id, taskId, project.IsBillable, []);
    }

    /// <summary>A split's parts can be described one by one, or the entry as a whole, not both.</summary>
    private static void CheckDescribed(string? description, ProposedPartRequest[]? split)
    {
        if (split is not { Length: > 1 } || !split.Any(p => p.Description is not null))
        {
            return;
        }

        if (description is not null)
        {
            throw new Problem("Give either a description or one in each part of the split, not both.");
        }

        if (string.Join("; ", split.Select(p => p.Description?.Trim()).Where(d => !string.IsNullOrEmpty(d))).Length > TimeEntry.DescriptionMaxLength)
        {
            throw new Problem("Together the parts' descriptions are too long.");
        }
    }

    private static string? Description(string? text)
    {
        if (text?.Trim() is not { } trimmed)
        {
            return null;
        }

        return trimmed.Length > TimeEntry.DescriptionMaxLength ? throw new Problem("The description is too long.") : trimmed;
    }

    private static string Reason(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length > AgentProposal.ReasonMaxLength ? trimmed[..AgentProposal.ReasonMaxLength] : trimmed;
    }

    /// <summary>
    /// A time an agent wrote: ISO 8601 with an offset, or a wall-clock time in the person's time zone, with a <c>T</c>
    /// or a space and with or without seconds. A time daylight saving skips moves on; one it repeats is the earlier.
    /// </summary>
    public static DateTimeOffset? Date(string text, DateTimeZone zone)
    {
        text = text.Trim();
        if (WithOffset().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            return instant;
        }

        var match = WallClock().Match(text);
        if (!match.Success)
        {
            return null;
        }

        if (!DateOnly.TryParseExact($"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        var hours = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
        var seconds = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
        if (hours >= 24 || minutes >= 60 || seconds >= 60)
        {
            return null;
        }

        var local = new LocalDateTime(day.Year, day.Month, day.Day, hours, minutes, seconds);
        return zone.ResolveLocal(local, Resolvers.LenientResolver).ToDateTimeOffset();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex WithOffset();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})[T ](\d{1,2}):(\d{2})(?::(\d{2}))?$")]
    private static partial Regex WallClock();
}
