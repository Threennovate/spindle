using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Protocol;
using NodaTime;
using Spindle.Api.AgentActivity;
using Spindle.Api.Auth;
using Spindle.Api.Reports;
using Spindle.Api.Subscriptions;
using Spindle.Api.TimeEntries;
using Spindle.Api.Workspaces;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Common;
using Spindle.Domain.TimeEntries;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Mcp;

/// <summary>
/// The tools agents get through Spindle's MCP server: those of the apps'
/// former local MCP, with the same names, inputs and rules, run on the server for the person and workspace the
/// agent was let into. They read entries, projects, reports and synced agent activity, write descriptions and tasks,
/// and start and stop the timer; time, projects and splits only ever become proposals the person confirms. Each tool
/// goes through the API's own handlers, so the same checks apply as in the apps.
/// </summary>
internal sealed partial class McpTools(
    AppDbContext db,
    CurrentWorkspace workspace,
    WorkspacePolicy policy,
    TimeEntryReferences references,
    TimeEntryRules rules,
    ReportLoader reports,
    AgentEvidence evidence,
    OrganizationSubscriptions subscriptions,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<McpTools> logger)
{
    /// <summary>A mistake in what the agent asked, told to it in words.</summary>
    private sealed class ToolProblem(string message) : Exception(message);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Who is asking, where, and in which time zone, for one tool call.</summary>
    private sealed record Call(Guid UserId, Guid SessionId, string WorkspaceName, DateTimeZone Zone, string? Client, CancellationToken Ct)
    {
        public LocalDate Today(DateTimeOffset now) => Instant.FromDateTimeOffset(now).InZone(Zone).Date;

        public DateTimeOffset StartOfDay(LocalDate day) => day.AtStartOfDayInZone(Zone).ToDateTimeOffset();

        public string Iso(DateTimeOffset instant) => AgentEvidence.Iso(instant, Zone);
    }

    public async ValueTask<CallToolResult> CallAsync(ClaimsPrincipal principal, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == request.Name);
        if (tool is null)
        {
            return Failure($"Unknown Spindle tool: {request.Name}.");
        }

        var userId = principal.GetUserId();
        var sessionId = principal.GetSessionId()!.Value;
        var workspaceId = Guid.Parse(principal.FindFirstValue(AccessTokenIssuer.WorkspaceClaim)!);
        var scope = principal.FindFirstValue(AccessTokenIssuer.ScopeClaim) ?? "";
        var member = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId)
            .Select(m => new { m.Role, Name = db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.Name).FirstOrDefault() })
            .SingleOrDefaultAsync(cancellationToken);
        if (member?.Name is null)
        {
            return Failure("The workspace this agent was connected to is no longer the person's. Connect Spindle again and choose a workspace.");
        }

        workspace.Set(workspaceId, userId, member.Role);
        var writes = tool.Annotations?.ReadOnlyHint != true;
        if (writes && !McpOAuth.CanWrite(scope))
        {
            return Failure("This agent was connected to Spindle to read only. To let it describe entries, run the timer or propose time, "
                + "the person connects Spindle again and allows changes.");
        }

        // As in the apps, a read-only organization can still stop its timer.
        if (writes && tool.Name != "stop_timer" && await ReadOnlyFilter.ReadOnlyOrganizationAsync(db, subscriptions, workspaceId, cancellationToken) is not null)
        {
            return Failure("This organization is read-only: its trial or plan has ended. Its time can still be read and reported, but not changed.");
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.TimeZone }).SingleAsync(cancellationToken);
        var client = await db.RefreshTokens.Where(t => t.FamilyId == sessionId).Select(t => t.DeviceName).FirstOrDefaultAsync(cancellationToken);
        var call = new Call(userId, sessionId, member.Name, DateTimeZoneProviders.Tzdb.GetZoneOrNull(user.TimeZone) ?? DateTimeZone.Utc, client, cancellationToken);
        var arguments = request.Arguments is { } given
            ? JsonSerializer.SerializeToElement(given)
            : JsonSerializer.SerializeToElement(new { });

        try
        {
            var value = tool.Name switch
            {
                "get_timer" => await TimerAsync(arguments, call),
                "list_entries" => await ListEntriesAsync(arguments, call),
                "list_projects" => await ListProjectsAsync(arguments, call),
                "get_evidence" => await EvidenceAsync(arguments, call),
                "update_entries" => await UpdateEntriesAsync(arguments, call),
                "undo_last_update" => await UndoAsync(arguments, call),
                "start_timer" => await StartTimerAsync(arguments, call),
                "stop_timer" => await StopTimerAsync(arguments, call),
                "continue_entry" => await ContinueEntryAsync(arguments, call),
                "propose_entries" => await ProposeEntriesAsync(arguments, call),
                "propose_changes" => await ProposeChangesAsync(arguments, call),
                "list_proposals" => await ListProposalsAsync(arguments, call),
                "withdraw_proposals" => await WithdrawProposalsAsync(arguments, call),
                "find_untracked_time" => await UntrackedAsync(arguments, call),
                "get_report" => await ReportAsync(arguments, call),
                _ => throw new ToolProblem("Unknown Spindle tool."),
            };
            var structured = JsonSerializer.SerializeToElement(value, Json);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
                StructuredContent = structured,
            };
        }
        catch (ToolProblem problem)
        {
            return Failure(problem.Message);
        }
        catch (DomainValidationException problem)
        {
            return Failure(problem.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure("Something changed the same entries at the same moment; try again.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "The MCP tool {Tool} failed", tool.Name);
            return Failure("Spindle couldn't do that just now. Try again; if it keeps failing, tell the person.");
        }
    }

    private static CallToolResult Failure(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };

    // Reading

    private async Task<object> TimerAsync(JsonElement arguments, Call call)
    {
        Fields(arguments);
        var now = timeProvider.GetUtcNow();
        var running = await db.TimeEntries.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == call.UserId && e.Stop == null, call.Ct);
        var elsewhere = running is not null && running.WorkspaceId != workspace.Id;
        return new
        {
            // A timer in another workspace is only said to run: its entry is outside what the agent was let into.
            running = running is null || elsewhere ? null : TimeEntryResponse.From(running),
            running_elsewhere = elsewhere ? "It runs in another workspace, which this agent can't reach." : null,
            today = call.Today(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            time_zone = call.Zone.Id,
            now = call.Iso(now),
            workspace_id = workspace.Id,
            workspace = call.WorkspaceName,
        };
    }

    private async Task<object> ListEntriesAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "from", "to");
        var now = timeProvider.GetUtcNow();
        var from = Day(arguments, "from", call.Today(now));
        var to = Day(arguments, "to", from);
        if (to < from || Period.DaysBetween(from, to) >= 31)
        {
            throw new ToolProblem("Ask for 1 to 31 inclusive days.");
        }

        var (start, end) = (call.StartOfDay(from), call.StartOfDay(to.PlusDays(1)));
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == call.UserId && e.Start >= start && e.Start < end)
            .OrderByDescending(e => e.Start)
            .ToListAsync(call.Ct);
        var names = await ProjectNamesAsync(call.Ct);
        // Each part of a split entry counts on its own project.
        var totals = entries
            .SelectMany(e => TimeShare.Divide(e.Parts, Math.Max(0, (long)((e.Stop ?? now) - e.Start).TotalSeconds)))
            .GroupBy(p => p.Part.ProjectId)
            .Select(g => new { project_id = g.Key, project = g.Key is { } id ? names.GetValueOrDefault(id) : null, seconds = g.Sum(p => p.Seconds) })
            .OrderByDescending(t => t.seconds);
        return new
        {
            from = Text(from),
            to = Text(to),
            time_zone = call.Zone.Id,
            entries = entries.Select(TimeEntryResponse.From),
            projects = totals,
        };
    }

    private async Task<object> ListProjectsAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "include_archived");
        var archived = Boolean(arguments, "include_archived");
        var projects = await policy.VisibleProjects().AsNoTracking()
            .Where(p => archived || p.ArchivedAt == null)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.IsBillable,
                Archived = p.ArchivedAt != null,
                Client = db.Clients.Where(c => c.Id == p.ClientId).Select(c => c.Name).FirstOrDefault(),
            })
            .ToListAsync(call.Ct);
        var ids = projects.Select(p => p.Id).ToList();
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => ids.Contains(t.ProjectId) && !t.IsDone)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.ProjectId, t.Name })
            .ToListAsync(call.Ct);
        return new
        {
            projects = projects.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                client = p.Client,
                billable = p.IsBillable,
                archived = p.Archived,
                tasks = tasks.Where(t => t.ProjectId == p.Id).Select(t => new { id = t.Id, name = t.Name }),
            }),
        };
    }

    private async Task<object> EvidenceAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "entry_ids");
        var ids = Ids(arguments, "entry_ids", 50);
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == call.UserId && ids.Contains(e.Id))
            .OrderBy(e => e.Start)
            .ToListAsync(call.Ct);
        var unknown = ids.Where(id => entries.All(e => e.Id != id)).ToArray();
        if (entries.Count == 0)
        {
            return new { entries = System.Array.Empty<object>(), unknown_ids = unknown };
        }

        var now = timeProvider.GetUtcNow();
        var names = await ProjectNamesAsync(call.Ct);
        var (from, to) = (entries.Min(e => e.Start), entries.Max(e => e.Stop ?? now));
        // The whole days around them, as split suggestions see them.
        var activity = await evidence.LoadAsync(SplitSuggestions.Window(from, to, call.Zone).Start, SplitSuggestions.Window(from, to, call.Zone).End, call.Ct);
        var dismissed = (await db.SplitSuggestionDismissals.AsNoTracking()
            .Where(d => d.UserId == call.UserId && ids.Contains(d.EntryId))
            .Select(d => d.EntryId)
            .ToListAsync(call.Ct)).ToHashSet();
        var projectIds = entries.Select(e => e.ProjectId).OfType<Guid>().Distinct().ToList();
        var recent = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == call.UserId && e.ProjectId != null && projectIds.Contains(e.ProjectId.Value)
                && e.Description != "" && e.Start >= from.AddDays(-60))
            .OrderByDescending(e => e.Start)
            .Take(400)
            .Select(e => new { e.Id, e.ProjectId, e.Description })
            .ToListAsync(call.Ct);

        var results = new List<object>();
        foreach (var entry in entries)
        {
            var stop = entry.Stop ?? now;
            var repositories = AgentEvidence.Repositories(activity, entry.Start, stop, names, call.Zone);
            var suggestion = !activity.Sync || entry.IsSplit || dismissed.Contains(entry.Id)
                ? null
                : SplitSuggestions.Suggest(entry.Start, stop, entry.ProjectId, activity.Attention, activity.Links, activity.Projects, call.Zone);
            results.Add(new
            {
                entry = TimeEntryResponse.From(entry),
                window = new { start = call.Iso(entry.Start), stop = call.Iso(stop) },
                agent_minutes = Math.Round(activity.Attention.Where(a => a.End > entry.Start.ToUnixTimeMilliseconds() && a.Start < stop.ToUnixTimeMilliseconds())
                    .Sum(a => Math.Min(a.End, stop.ToUnixTimeMilliseconds()) - Math.Max(a.Start, entry.Start.ToUnixTimeMilliseconds())) / 60_000.0),
                repositories,
                split_suggestion = suggestion is null ? null : SplitSuggestionResponse.From(entry.Id, suggestion),
                recent_descriptions = recent.Where(e => e.Id != entry.Id && e.ProjectId == entry.ProjectId)
                    .Select(e => e.Description).Distinct().Take(8),
                note = AgentEvidence.Missing(activity, entry.Start, repositories),
                prompts = "Not shared. Prompts and commits never reach Spindle's server; read the commits with the git command given, if you have the repository.",
            });
        }

        return new { entries = results, unknown_ids = unknown };
    }

    /// <summary>
    /// When agents were busy on the days asked for and no entry or waiting proposal covers it, with the evidence for
    /// each stretch, to propose entries from.
    /// </summary>
    private async Task<object> UntrackedAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "from", "to");
        var now = timeProvider.GetUtcNow();
        var from = Day(arguments, "from", call.Today(now));
        var to = Day(arguments, "to", from);
        if (to < from)
        {
            throw new ToolProblem("from must not be after to.");
        }

        if (Period.DaysBetween(from, to) >= 7)
        {
            throw new ToolProblem("Ask for at most 7 days at a time.");
        }

        var (start, end) = (call.StartOfDay(from), call.StartOfDay(to.PlusDays(1)));
        var activity = await evidence.LoadAsync(start, end, call.Ct);
        if (!activity.Sync)
        {
            return new { from = Text(from), to = Text(to), stretches = System.Array.Empty<object>(), note = AgentEvidence.Missing(activity, start, []) };
        }

        // A day before too, for an entry that runs over midnight into the first.
        var dayBefore = call.StartOfDay(from.PlusDays(-1));
        var entries = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == call.UserId && e.Start >= dayBefore && e.Start < end)
            .ToListAsync(call.Ct);
        var proposals = await AgentProposalEndpoints.ListAsync(workspace, db, references, rules, timeProvider, call.Ct);
        var covered = entries.Select(e => Range(e.Start, e.Stop ?? now))
            .Concat(proposals.Value!.Pending.Select(p => p.Saves).OfType<TimeEntryRequest>()
                .Where(r => r.Start is not null && r.Stop is not null)
                .Select(r => Range(r.Start!.Value, r.Stop!.Value)))
            .ToArray();
        var stretches = UntrackedActivity.Stretches(activity.Attention, covered, Range(start, end), now.ToUnixTimeMilliseconds());
        var names = await ProjectNamesAsync(call.Ct);
        var results = stretches.Select(stretch =>
        {
            var (stretchStart, stretchEnd) = (DateTimeOffset.FromUnixTimeMilliseconds(stretch.Start), DateTimeOffset.FromUnixTimeMilliseconds(stretch.End));
            var runs = UntrackedActivity.Runs(activity.Attention, covered, stretch).Select(run =>
            {
                var projectId = run.Repository is { } repository ? activity.ProjectFor(repository) : null;
                return new
                {
                    repository = run.Repository?.Name,
                    outside_git = run.Repository is null ? true : (bool?)null,
                    project = projectId is { } id ? new { id, name = names.GetValueOrDefault(id) ?? "" } : null,
                    start = call.Iso(DateTimeOffset.FromUnixTimeMilliseconds(run.Range.Start)),
                    stop = call.Iso(DateTimeOffset.FromUnixTimeMilliseconds(run.Range.End)),
                };
            }).ToArray();
            return new
            {
                start = call.Iso(stretchStart),
                stop = call.Iso(stretchEnd),
                minutes = Math.Round(stretch.Duration / 60_000.0),
                runs,
                repositories = AgentEvidence.Repositories(activity, stretchStart, stretchEnd, names, call.Zone),
            };
        }).ToArray();
        var recent = await db.TimeEntries.AsNoTracking()
            .Where(e => e.WorkspaceId == workspace.Id && e.UserId == call.UserId && e.Description != "" && e.Start < end)
            .OrderByDescending(e => e.Start)
            .Take(100)
            .Select(e => e.Description)
            .ToListAsync(call.Ct);

        return new
        {
            from = Text(from),
            to = Text(to),
            time_zone = call.Zone.Id,
            stretches = results,
            recent_descriptions = recent.Distinct().Take(10),
            note = AgentEvidence.Missing(activity, start, activity.Sessions)
                ?? (results.Length == 0
                    ? "No agent activity outside entries and waiting proposals. Ask the person about anything else they did."
                    : "Only when agents were busy with no entry: meetings, calls, reading and thinking away from agents don't show here. Propose what the person confirms or the evidence shows, and ask about the rest."),
        };
    }

    private static TimeRange Range(DateTimeOffset start, DateTimeOffset end) =>
        new(start.ToUnixTimeMilliseconds(), Math.Max(start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds()));

    // Writing

    /// <summary>What an agent may change on an entry: its description, its task, and a split's part descriptions when each has its own.</summary>
    private sealed record Values(string Description, string[]? Parts, Guid? TaskId)
    {
        public static Values Of(TimeEntryResponse entry) =>
            new(entry.Description, DescribesParts(entry) ? entry.Shares.Select(s => s.Description ?? "").ToArray() : null, entry.TaskId);

        public bool Same(Values other) => Description == other.Description && TaskId == other.TaskId
            && (Parts is null ? other.Parts is null : other.Parts is not null && Parts.SequenceEqual(other.Parts));
    }

    private sealed record PartEdit(Guid ProjectId, Guid? TaskId, string Description);

    private sealed record Edit(Guid Id, string? Description, Guid? TaskId, PartEdit[]? Parts);

    private sealed record Change(TimeEntryResponse Before, Values After);

    private static bool DescribesParts(TimeEntryResponse entry) => entry.Shares.Count > 1 && entry.Shares.Any(s => s.Description is not null);

    /// <summary>The last update_entries call of an agent's session, for undo_last_update: kept a day, in this server's memory.</summary>
    private static string UndoKey(Call call) => $"mcp-undo:{call.SessionId}";

    private async Task<object> UpdateEntriesAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "updates", "overwrite");
        var edits = Updates(arguments);
        var overwrite = Boolean(arguments, "overwrite");
        var changedEntries = new List<object>();
        var skipped = new List<object>();
        var changes = new List<Change>();
        var seen = new HashSet<Guid>();
        Dictionary<Guid, (Guid ProjectId, string Name, bool Done)>? tasks = null;
        foreach (var (id, description, taskId, parts) in edits)
        {
            if (!seen.Add(id))
            {
                skipped.Add(new { id, reason = "Duplicate entry ID." });
                continue;
            }

            var current = await FindAsync(id, call.Ct);
            if (current is null)
            {
                skipped.Add(new { id, reason = "There is no such entry among the person's entries in this workspace. Call list_entries first." });
                continue;
            }

            var before = Values.Of(current);
            var after = before;
            if (parts is not null)
            {
                if (description is not null)
                {
                    skipped.Add(new { id, reason = "Give either a description or one per project (parts), not both." });
                    continue;
                }

                var (described, partProblem) = DescribeParts(current, parts, overwrite);
                if (partProblem is not null)
                {
                    skipped.Add(new { id, reason = partProblem });
                }
                else
                {
                    after = after with { Description = string.Join("; ", described!), Parts = described };
                }
            }

            if (description is not null)
            {
                var problem = DescriptionProblem(current, description, overwrite);
                if (problem is not null)
                {
                    skipped.Add(new { id, reason = problem });
                }
                else if (current.Description != description)
                {
                    // One description for the whole entry: every project of a split shows it again.
                    after = after with { Description = description, Parts = null };
                }
            }

            if (taskId is { } task && task != current.TaskId)
            {
                tasks ??= await db.Tasks.AsNoTracking().Where(t => t.WorkspaceId == workspace.Id)
                    .ToDictionaryAsync(t => t.Id, t => (t.ProjectId, t.Name, t.IsDone), call.Ct);
                var problem = TaskProblem(current, task, overwrite, tasks);
                if (problem is not null)
                {
                    skipped.Add(new { id, reason = problem });
                }
                else
                {
                    after = after with { TaskId = task };
                }
            }

            if (after.Same(before))
            {
                continue;
            }

            var saved = await SaveAsync(current, before, after, call.Ct);
            if (saved.Error is not null)
            {
                skipped.Add(new { id, reason = saved.Error });
                continue;
            }

            var entry = saved.Entry!;
            changes.Add(new(current, Values.Of(entry)));
            var taskName = entry.TaskId is { } savedTask && tasks?.TryGetValue(savedTask, out var known) == true ? known.Name : null;
            changedEntries.Add(DescribesParts(entry)
                ? new { id, description = entry.Description, task = taskName, parts = entry.Shares.Select(s => new { project_id = s.ProjectId, task_id = s.TaskId, description = s.Description }).ToArray() }
                : new { id, description = entry.Description, task = taskName });
        }

        if (changes.Count > 0)
        {
            cache.Set(UndoKey(call), changes, TimeSpan.FromDays(1));
        }

        return new { changed = changedEntries, skipped };
    }

    /// <summary>
    /// Saves an agent's change: a split's parts described one by one are saved whole, saying so; anything else goes
    /// through the bulk update, which keeps every tracking field as it is.
    /// </summary>
    private async Task<(TimeEntryResponse? Entry, string? Error)> SaveAsync(TimeEntryResponse current, Values before, Values after, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (current.Shares.Count > 1 && (after.Parts is not null || before.Parts is not null))
        {
            var result = await TimeEntryEndpoints.UpdateAsync(current.Id, Whole(current, after), workspace, db, references, rules, timeProvider, ct);
            return result.Result switch
            {
                Ok<TimeEntryResponse> ok => (ok.Value, null),
                ValidationProblem problem => (null, Message(problem)),
                _ => (null, "The entry is gone."),
            };
        }

        var bulk = new BulkUpdateTimeEntriesRequest
        {
            Ids = [current.Id],
            Description = after.Description != before.Description || (after.Parts is null && before.Parts is not null) ? after.Description : null,
            Project = after.TaskId != before.TaskId ? new ProjectChoice(current.ProjectId, after.TaskId) : null,
        };
        var updated = await TimeEntryEndpoints.BulkUpdateAsync(bulk, workspace, db, references, rules, ct);
        return updated.Result switch
        {
            Ok<List<TimeEntryResponse>> ok => (ok.Value!.Single(), null),
            ValidationProblem problem => (null, Message(problem)),
            _ => (null, "The entry couldn't be saved."),
        };
    }

    /// <summary>
    /// The entry saved whole with these values; times, projects, tags and the split as they are. A split always says
    /// whether its parts have their own descriptions.
    /// </summary>
    private static TimeEntryRequest Whole(TimeEntryResponse entry, Values values) => new()
    {
        Description = values.Description,
        ProjectId = entry.ProjectId,
        TaskId = values.TaskId,
        TagIds = [.. entry.TagIds],
        IsBillable = entry.IsBillable,
        Start = entry.Start,
        Stop = entry.Stop,
        Shares = entry.Shares.Select((s, i) => new TimeShareRequest
        {
            ProjectId = s.ProjectId,
            TaskId = s.TaskId,
            IsBillable = s.IsBillable,
            Percent = s.Percent,
            Description = values.Parts?.ElementAtOrDefault(i),
        }).ToArray(),
        DescribeParts = entry.Shares.Count > 1 ? values.Parts is not null : null,
    };

    private async Task<object> UndoAsync(JsonElement arguments, Call call)
    {
        Fields(arguments);
        var restored = new List<Guid>();
        var notRestored = new List<object>();
        foreach (var change in cache.Get<List<Change>>(UndoKey(call)) ?? [])
        {
            var current = await FindAsync(change.Before.Id, call.Ct);
            if (current is null || !Values.Of(current).Same(change.After))
            {
                notRestored.Add(new { id = change.Before.Id, reason = "It was changed again or is gone." });
                continue;
            }

            var saved = await SaveAsync(current, change.After, Values.Of(change.Before), call.Ct);
            if (saved.Error is null)
            {
                restored.Add(current.Id);
            }
            else
            {
                notRestored.Add(new { id = current.Id, reason = saved.Error });
            }
        }

        cache.Remove(UndoKey(call));
        return new { restored, skipped = notRestored };
    }

    private async Task<object> StartTimerAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "description", "project_id", "task_id");
        var description = Text(arguments, "description")?.Trim() ?? "";
        if (description.Length > TimeEntry.DescriptionMaxLength)
        {
            throw new ToolProblem($"Description is longer than {TimeEntry.DescriptionMaxLength} characters.");
        }

        return await StartAsync(new CreateTimeEntryRequest
        {
            Id = Guid.NewGuid(),
            Start = timeProvider.GetUtcNow(),
            Description = description,
            ProjectId = OptionalId(arguments, "project_id"),
            TaskId = OptionalId(arguments, "task_id"),
        }, call);
    }

    /// <summary>Starts the timer now with an earlier entry's description, project, task, tags and split.</summary>
    private async Task<object> ContinueEntryAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "entry_id");
        var earlier = (OptionalId(arguments, "entry_id") is { } id ? await FindAsync(id, call.Ct) : null)
            ?? throw new ToolProblem("There is no such entry among the person's entries. List entries first.");
        if (earlier.Stop is null)
        {
            throw new ToolProblem("That entry is the one running.");
        }

        var split = earlier.Shares.Count > 1;
        return await StartAsync(new CreateTimeEntryRequest
        {
            Id = Guid.NewGuid(),
            Start = timeProvider.GetUtcNow(),
            Description = earlier.Description,
            ProjectId = earlier.ProjectId,
            TaskId = earlier.TaskId,
            TagIds = [.. earlier.TagIds],
            IsBillable = earlier.IsBillable,
            Shares = split ? earlier.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable, Percent = s.Percent, Description = s.Description }).ToArray() : null,
            DescribeParts = split ? DescribesParts(earlier) : null,
        }, call);
    }

    private async Task<object> StartAsync(CreateTimeEntryRequest request, Call call)
    {
        var previous = await db.TimeEntries.AsNoTracking().Where(e => e.UserId == call.UserId && e.Stop == null).Select(e => (Guid?)e.Id).SingleOrDefaultAsync(call.Ct);
        var created = await TimeEntryEndpoints.CreateAsync(request, workspace, db, references, rules, timeProvider, call.Ct, stopsTimerElsewhere: false);
        return created.Result switch
        {
            Created<TimeEntryResponse> started => new { started = started.Value, stopped_id = previous },
            Ok<TimeEntryResponse> started => new { started = started.Value, stopped_id = previous },
            ValidationProblem problem => throw new ToolProblem(Message(problem)),
            ProblemHttpResult problem => throw new ToolProblem(problem.ProblemDetails.Title ?? "The timer couldn't start."),
            _ => throw new ToolProblem("The timer couldn't start."),
        };
    }

    private async Task<object> StopTimerAsync(JsonElement arguments, Call call)
    {
        Fields(arguments);
        var running = await db.TimeEntries.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == call.UserId && e.Stop == null, call.Ct)
            ?? throw new ToolProblem("No timer is running.");
        if (running.WorkspaceId != workspace.Id)
        {
            throw new ToolProblem("The timer runs in another workspace, which this agent can't reach. The person stops it in Spindle.");
        }

        var stopped = await TimeEntryEndpoints.StopAsync(running.Id, null, workspace, db, rules, timeProvider, call.Ct);
        return stopped.Result switch
        {
            Ok<TimeEntryResponse> ok => new { stopped = ok.Value },
            ValidationProblem problem => throw new ToolProblem("The timer keeps running: " + Message(problem)),
            _ => throw new ToolProblem("No timer is running."),
        };
    }

    // Proposals: the same endpoints the apps call, so the same rules and inbox.

    private async Task<object> ProposeEntriesAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "entries");
        var items = Array(arguments, "entries", AgentProposalRules.Limit, $"Propose 1 to {AgentProposalRules.Limit} entries at a time.").Select(e =>
        {
            Fields(e, "start", "stop", "description", "project_id", "task_id", "split", "reason");
            var start = Text(e, "start");
            var stop = Text(e, "stop");
            if (start is null || stop is null)
            {
                throw new ToolProblem("entries must be a list of objects with a start and a stop.");
            }

            return new ProposedEntryRequest
            {
                Start = Limit(start, 64, "start")!,
                Stop = Limit(stop, 64, "stop")!,
                Description = Limit(Text(e, "description"), 4000, "description"),
                ProjectId = Limit(Text(e, "project_id"), 64, "project_id"),
                TaskId = Limit(Text(e, "task_id"), 64, "task_id"),
                Split = Split(e),
                Reason = Limit(Text(e, "reason"), 1000, "reason"),
            };
        }).ToArray();
        var plan = await AgentProposalEndpoints.ProposeEntriesAsync(new ProposeEntriesRequest { Entries = items, Client = call.Client }, workspace, db, policy, timeProvider, call.Ct);
        return await ProposalResultAsync(plan.Value!, call);
    }

    private async Task<object> ProposeChangesAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "changes");
        var items = Array(arguments, "changes", AgentProposalRules.Limit, $"Propose 1 to {AgentProposalRules.Limit} changes at a time.").Select(e =>
        {
            Fields(e, "id", "start", "stop", "description", "project_id", "task_id", "split", "reason");
            return new ProposedChangeRequest
            {
                Id = Limit(Text(e, "id"), 64, "id") ?? throw new ToolProblem("changes must be a list of objects with an entry id and what to change."),
                Start = Limit(Text(e, "start"), 64, "start"),
                Stop = Limit(Text(e, "stop"), 64, "stop"),
                Description = Limit(Text(e, "description"), 4000, "description"),
                ProjectId = Limit(Text(e, "project_id"), 64, "project_id"),
                TaskId = Limit(Text(e, "task_id"), 64, "task_id"),
                Split = Split(e),
                Reason = Limit(Text(e, "reason"), 1000, "reason"),
            };
        }).ToArray();
        var plan = await AgentProposalEndpoints.ProposeChangesAsync(new ProposeChangesRequest { Changes = items, Client = call.Client }, workspace, db, policy, timeProvider, call.Ct);
        return await ProposalResultAsync(plan.Value!, call);
    }

    private async Task<object> ProposalResultAsync(ProposalPlanResponse plan, Call call)
    {
        var catalog = await CatalogAsync(call.Ct);
        return new
        {
            proposed = plan.Proposals.Select(p => ProposalJson(p, catalog, call)).ToArray(),
            refused = plan.Refused.Select(r => new { index = r.Index, id = r.Id, reason = r.Reason }).ToArray(),
            note = plan.Proposals.Count > 0 ? "Nothing is saved yet. The proposals wait in Spindle (on the web, the Mac and Windows) for the person to review." : null,
        };
    }

    private async Task<object> ListProposalsAsync(JsonElement arguments, Call call)
    {
        Fields(arguments);
        var listed = (await AgentProposalEndpoints.ListAsync(workspace, db, references, rules, timeProvider, call.Ct)).Value!;
        var catalog = await CatalogAsync(call.Ct);
        return new
        {
            waiting = listed.Pending.Select(p =>
            {
                var value = ProposalJson(p, catalog, call);
                if (p.Blocking.Count > 0)
                {
                    value["blocking"] = p.Blocking;
                }

                if (p.Warnings.Count > 0)
                {
                    value["warnings"] = p.Warnings;
                }

                return value;
            }).ToArray(),
            dealt_with = listed.Outcomes.Select(o => new { id = o.ProposalId, outcome = o.Outcome, entry_id = o.EntryId, date = call.Iso(o.At) }).ToArray(),
        };
    }

    private async Task<object> WithdrawProposalsAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "proposal_ids");
        var given = Array(arguments, "proposal_ids", AgentProposalRules.Limit, "proposal_ids must be a list of IDs.")
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new ToolProblem("proposal_ids must be a list of IDs."))
            .ToArray();
        var ids = given.Select(text => Guid.TryParse(text, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();
        var taken = ids.Length == 0
            ? []
            : (await AgentProposalEndpoints.WithdrawAsync(new WithdrawAgentProposalsRequest { Ids = ids }, workspace, db, timeProvider, call.Ct)).Value!.Withdrawn;
        return new
        {
            withdrawn = taken,
            unknown_ids = given.Where(text => !Guid.TryParse(text, out var id) || !taken.Contains(id)).ToArray(),
        };
    }

    private sealed record Catalog(Dictionary<Guid, string> Projects, Dictionary<Guid, string> Tasks);

    private async Task<Catalog> CatalogAsync(CancellationToken ct) => new(
        await ProjectNamesAsync(ct),
        await db.Tasks.AsNoTracking().Where(t => t.WorkspaceId == workspace.Id).ToDictionaryAsync(t => t.Id, t => t.Name, ct));

    private static Dictionary<string, object?> ProposalJson(AgentProposalResponse proposal, Catalog catalog, Call call)
    {
        object? Project(Guid? id) => id is { } projectId && catalog.Projects.TryGetValue(projectId, out var name) ? new { id = projectId, name } : null;
        object? Task(Guid? id) => id is { } taskId && catalog.Tasks.TryGetValue(taskId, out var name) ? new { id = taskId, name } : null;

        var fields = proposal.Fields;
        var value = new Dictionary<string, object?> { ["id"] = proposal.Id, ["kind"] = proposal.EntryId is null ? "new_entry" : "change" };
        if (proposal.EntryId is { } entryId)
        {
            value["entry_id"] = entryId;
        }

        if (fields.Start is { } start)
        {
            value["start"] = call.Iso(start);
        }

        if (fields.Stop is { } stop)
        {
            value["stop"] = call.Iso(stop);
        }

        if (fields.Description is { } description)
        {
            value["description"] = description;
        }

        if (fields.Work is { } work)
        {
            if (work.Shares.Count > 1)
            {
                value["split"] = work.Shares.Select(part =>
                {
                    var item = new Dictionary<string, object?> { ["project"] = Project(part.ProjectId), ["task"] = Task(part.TaskId), ["percent"] = part.Percent };
                    if (part.Description is { } own)
                    {
                        item["description"] = own;
                    }

                    return item;
                }).ToArray();
            }
            else
            {
                value["project"] = Project(work.ProjectId);
                value["task"] = Task(work.TaskId);
            }
        }

        if (proposal.Reason.Length > 0)
        {
            value["reason"] = proposal.Reason;
        }

        return value;
    }

    // Reports

    private static readonly string[] Groupings = ["project", "client", "task", "tag", "description"];

    private async Task<object> ReportAsync(JsonElement arguments, Call call)
    {
        Fields(arguments, "from", "to", "group_by", "sub_group_by", "project_ids", "billable");
        if (Text(arguments, "from") is null)
        {
            throw new ToolProblem("from is required.");
        }

        var from = Day(arguments, "from", default);
        var to = Day(arguments, "to", from);
        if (to < from)
        {
            throw new ToolProblem("from must not be after to.");
        }

        if (Period.DaysBetween(from, to) >= 366)
        {
            throw new ToolProblem("Ask for at most 366 days at a time.");
        }

        var groupBy = Text(arguments, "group_by") ?? "project";
        if (!Groupings.Contains(groupBy))
        {
            throw new ToolProblem($"group_by is one of {string.Join(", ", Groupings)}.");
        }

        var subGroupBy = Text(arguments, "sub_group_by");
        if (subGroupBy is not null && (!Groupings.Contains(subGroupBy) || subGroupBy == groupBy))
        {
            throw new ToolProblem($"sub_group_by is another of {string.Join(", ", Groupings)}.");
        }

        Guid[] projectIds = arguments.TryGetProperty("project_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var id) ? id : throw new ToolProblem("project_ids must be a list of IDs.")).ToArray()
            : [];
        bool? billable = arguments.TryGetProperty("billable", out _) ? Boolean(arguments, "billable") : null;
        static ReportGrouping Grouping(string name) => Enum.Parse<ReportGrouping>(name, ignoreCase: true);
        var request = new SummaryReportRequest
        {
            From = from.ToDateOnly(),
            To = to.ToDateOnly(),
            UserIds = [call.UserId],
            GroupBy = Grouping(groupBy),
            SubGroupBy = subGroupBy is null ? null : Grouping(subGroupBy),
            ProjectIds = projectIds,
            Billable = billable,
        };
        var (data, problem) = await reports.LoadAsync(request, ReportFilter.MaxDays, call.Ct);
        if (data is null)
        {
            throw new ToolProblem(Message(problem!));
        }

        var report = ReportBuilder.Summary(data, request.GroupBy, request.SubGroupBy);
        static long Minutes(long seconds) => seconds / 60;
        object Node(SummaryGroup group)
        {
            var value = new Dictionary<string, object?> { ["name"] = group.Name, ["minutes"] = Minutes(group.Seconds), ["billable_minutes"] = Minutes(group.BillableSeconds), ["key"] = group.Key };
            if (group.Detail is { } detail)
            {
                value["detail"] = detail;
            }

            if (group.Amount is { } amount && amount != 0)
            {
                value["amount"] = amount;
            }

            // The report breaks each group down by description unless told otherwise; only what was asked for is given.
            if (subGroupBy is not null && group.SubGroups.Count > 0)
            {
                value["groups"] = group.SubGroups.Select(Node).ToArray();
            }

            return value;
        }

        var totals = new Dictionary<string, object?> { ["minutes"] = Minutes(report.Totals.Seconds), ["billable_minutes"] = Minutes(report.Totals.BillableSeconds), ["entries"] = report.Totals.EntryCount };
        if (report.Totals.Amount is { } total && total != 0)
        {
            totals["amount"] = total;
            totals["currency"] = report.Currency;
        }

        return new { from = Text(from), to = Text(to), workspace = call.WorkspaceName, group_by = groupBy, totals, groups = report.Groups.Select(Node).ToArray() };
    }

    // Rules for writing, as the apps' AgentEdits

    private static string? DescriptionProblem(TimeEntryResponse current, string description, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "Descriptions cannot be cleared by agents.";
        }

        if (description.Length > TimeEntry.DescriptionMaxLength)
        {
            return $"Description is longer than {TimeEntry.DescriptionMaxLength} characters.";
        }

        return !overwrite && !string.IsNullOrWhiteSpace(current.Description) && current.Description != description
            ? "It already has a description. Overwrite only if the person asked to."
            : null;
    }

    private static string? TaskProblem(TimeEntryResponse entry, Guid taskId, bool overwrite, IReadOnlyDictionary<Guid, (Guid ProjectId, string Name, bool Done)> tasks)
    {
        if (entry.Shares.Count > 1)
        {
            return "The entry is split between projects; tasks of its parts are set in the app.";
        }

        if (!tasks.TryGetValue(taskId, out var task))
        {
            return "There is no such task. List projects for their tasks.";
        }

        if (entry.ProjectId is not { } projectId)
        {
            return "The entry has no project; projects are chosen in the app.";
        }

        if (task.ProjectId != projectId)
        {
            return "That task belongs to another project; the entry's project is changed only in the app.";
        }

        if (task.Done)
        {
            return "That task is done.";
        }

        return entry.TaskId is not null && !overwrite ? "It already has a task. Change it only if the person asked to." : null;
    }

    /// <summary>
    /// The descriptions of a split entry's parts once the agent's are written: a part not given keeps what it shows
    /// now. A part that already shows a description, its own or the entry's, is only rewritten when overwriting.
    /// </summary>
    private static (string[]? Descriptions, string? Problem) DescribeParts(TimeEntryResponse entry, IReadOnlyList<PartEdit> edits, bool overwrite)
    {
        if (entry.Shares.Count < 2)
        {
            return (null, "The entry isn't split between projects; give it one description instead.");
        }

        var parts = entry.Shares;
        var shared = entry.Description.Trim();
        var descriptions = DescribesParts(entry) ? parts.Select(p => p.Description ?? "").ToArray() : parts.Select(_ => shared).ToArray();
        var seen = new HashSet<int>();
        foreach (var edit in edits)
        {
            var matches = Enumerable.Range(0, parts.Count).Where(i => parts[i].ProjectId == edit.ProjectId && (edit.TaskId is null || parts[i].TaskId == edit.TaskId)).ToArray();
            if (matches.Length == 0)
            {
                return (null, $"The entry has no part on project {edit.ProjectId}. Its parts are in list_entries.");
            }

            if (matches.Length > 1)
            {
                return (null, $"The entry has several parts on project {edit.ProjectId}; give each its task_id.");
            }

            var index = matches[0];
            if (!seen.Add(index))
            {
                return (null, "A part was described twice.");
            }

            var wanted = edit.Description.Trim();
            if (wanted.Length == 0)
            {
                return (null, "A description can't be cleared here.");
            }

            if (wanted.Length > TimeEntry.DescriptionMaxLength)
            {
                return (null, $"A description is longer than {TimeEntry.DescriptionMaxLength} characters.");
            }

            var now = descriptions[index].Trim();
            if (now.Length > 0 && now != wanted && !overwrite)
            {
                return (null, $"Its part on that project already shows “{now}”. Overwrite only if the person asked to.");
            }

            descriptions[index] = wanted;
        }

        return string.Join("; ", descriptions).Length > TimeEntry.DescriptionMaxLength
            ? (null, $"Together the descriptions are longer than {TimeEntry.DescriptionMaxLength} characters.")
            : (descriptions, null);
    }

    // Helpers

    private async Task<TimeEntryResponse?> FindAsync(Guid id, CancellationToken ct)
    {
        var entry = await db.TimeEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.WorkspaceId == workspace.Id && e.UserId == workspace.UserId, ct);
        return entry is null ? null : TimeEntryResponse.From(entry);
    }

    private Task<Dictionary<Guid, string>> ProjectNamesAsync(CancellationToken ct) =>
        policy.VisibleProjects().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);

    private static string Message(ValidationProblem problem) =>
        string.Join(" ", problem.ProblemDetails.Errors.SelectMany(e => e.Value).Distinct());

    private static string Text(LocalDate day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Edit[] Updates(JsonElement arguments) => Array(arguments, "updates", UpdateLimit).Select(e =>
    {
        Fields(e, "id", "description", "task_id", "parts");
        var id = OptionalId(e, "id") ?? throw new ToolProblem("Each update needs an id.");
        var parts = e.TryGetProperty("parts", out _)
            ? Array(e, "parts", 6).Select(p =>
            {
                Fields(p, "project_id", "task_id", "description");
                return new PartEdit(
                    OptionalId(p, "project_id") ?? throw new ToolProblem("Each part needs a project_id."),
                    OptionalId(p, "task_id"),
                    Text(p, "description") ?? throw new ToolProblem("Each part needs a description."));
            }).ToArray()
            : null;
        var description = Text(e, "description")?.Trim();
        var taskId = OptionalId(e, "task_id");
        if (description is null && parts is null && taskId is null)
        {
            throw new ToolProblem("Each update needs a description, parts or task_id.");
        }

        return new Edit(id, description, taskId, parts);
    }).ToArray();

    /// <summary>A proposed split as the agent sent it; IDs stay text for the rules to check.</summary>
    private static ProposedPartRequest[]? Split(JsonElement value)
    {
        if (!value.TryGetProperty("split", out var split) || split.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (split.ValueKind != JsonValueKind.Array || split.GetArrayLength() > 10)
        {
            throw new ToolProblem("split must be a list of parts with a project_id and a percent.");
        }

        return split.EnumerateArray().Select(p =>
        {
            Fields(p, "project_id", "task_id", "percent", "description");
            if (!p.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetInt32(out var whole))
            {
                throw new ToolProblem("Each part of a split needs a whole percent.");
            }

            return new ProposedPartRequest
            {
                ProjectId = Limit(Text(p, "project_id"), 64, "project_id"),
                Percent = whole,
                TaskId = Limit(Text(p, "task_id"), 64, "task_id"),
                Description = Limit(Text(p, "description"), 4000, "description"),
            };
        }).ToArray();
    }

    private static string? Limit(string? text, int length, string name) =>
        text is { } value && value.Length > length ? throw new ToolProblem($"{name} is too long.") : text;

    private static Guid[] Ids(JsonElement value, string name, int limit) => Array(value, name, limit)
        .Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var id) ? id : throw new ToolProblem("Expected entry IDs."))
        .Distinct()
        .ToArray();

    private static JsonElement[] Array(JsonElement value, string name, int limit, string? message = null)
    {
        if (!value.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() < 1 || list.GetArrayLength() > limit)
        {
            throw new ToolProblem(message ?? $"{name} requires 1 to {limit} items.");
        }

        return [.. list.EnumerateArray()];
    }

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ToolProblem("Arguments must be an object.");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new ToolProblem($"Unsupported field: {property.Name}.");
            }
        }
    }

    private static string? Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return field.ValueKind == JsonValueKind.String ? field.GetString() : throw new ToolProblem($"{name} must be a string.");
    }

    private static bool Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            return false;
        }

        return field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : throw new ToolProblem($"{name} must be boolean.");
    }

    private static Guid? OptionalId(JsonElement value, string name) => Text(value, name) is { } text
        ? Guid.TryParse(text, out var id) ? id : throw new ToolProblem($"{name} must be an ID.")
        : null;

    private static LocalDate Day(JsonElement value, string name, LocalDate fallback) => Text(value, name) is { } text
        ? DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? LocalDate.FromDateOnly(day)
            : throw new ToolProblem("Dates must be YYYY-MM-DD.")
        : fallback;
}
