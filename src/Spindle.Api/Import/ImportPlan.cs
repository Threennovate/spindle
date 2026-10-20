using Microsoft.EntityFrameworkCore;
using NodaTime;
using Spindle.Api.Workspaces;
using Spindle.Domain.Clients;
using Spindle.Domain.Projects;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Import;

/// <param name="Key">Identifies the person in the file, to assign their time to a member: their email, or name.</param>
/// <param name="UserId">The workspace member the time goes to, or none: the person's rows are left out.</param>
public sealed record ImportPerson(string Key, string Name, string? Email, Guid? UserId, int Entries, long Seconds);

public sealed record ImportNewProject(string Name, string? Client);

/// <summary>A row the import will add, as whose time and when.</summary>
internal sealed record PlannedEntry(ImportRow Row, Guid UserId, DateTimeOffset Start, DateTimeOffset Stop);

/// <summary>What importing a file would do: what it adds, what it creates to hold it, and what it leaves out.</summary>
internal sealed class ImportPlan
{
    public required ParsedFile File { get; init; }

    public required List<PlannedEntry> Entries { get; init; }

    public required int Duplicates { get; init; }

    public required int Skipped { get; init; }

    public required List<ImportPerson> People { get; init; }

    /// <summary>Existing clients, projects, tasks and tags by name, to reuse instead of creating again.</summary>
    public required Dictionary<string, Client> Clients { get; init; }

    public required Dictionary<(Guid? ClientId, string Name), Project> Projects { get; init; }

    public required Dictionary<(Guid ProjectId, string Name), ProjectTask> Tasks { get; init; }

    public required Dictionary<string, Tag> Tags { get; init; }

    public required int ExistingProjectCount { get; init; }

    public List<string> NewClients() =>
        Entries.Select(e => e.Row.Client).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(c => !Clients.ContainsKey(Key(c))).Order().ToList();

    public List<ImportNewProject> NewProjects() =>
        Entries.Where(e => e.Row.Project is not null)
            .Select(e => (Project: e.Row.Project!, e.Row.Client))
            .DistinctBy(p => (Key(p.Project), p.Client is null ? null : Key(p.Client)))
            .Where(p => ExistingProject(p.Project, p.Client) is null)
            .OrderBy(p => p.Project)
            .Select(p => new ImportNewProject(p.Project, p.Client))
            .ToList();

    public List<string> NewTasks() =>
        Entries.Where(e => e.Row.Task is not null)
            .Select(e => (e.Row.Project, e.Row.Client, Task: e.Row.Task!))
            .DistinctBy(t => (Key(t.Project!), t.Client is null ? null : Key(t.Client), Key(t.Task)))
            .Where(t => ExistingProject(t.Project!, t.Client) is not { } project || !Tasks.ContainsKey((project.Id, Key(t.Task))))
            .Select(t => $"{t.Project} · {t.Task}")
            .Order()
            .ToList();

    public List<string> NewTags() =>
        Entries.SelectMany(e => e.Row.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !Tags.ContainsKey(Key(t))).Order().ToList();

    public Project? ExistingProject(string name, string? client) => Projects.GetValueOrDefault((ClientId(client), Key(name)));

    /// <summary>The existing client's id; none for no client; and the empty id for a client still to be created.</summary>
    public Guid? ClientId(string? client) => client is null ? null : Clients.GetValueOrDefault(Key(client))?.Id ?? Guid.Empty;

    /// <summary>Who a row's person is in the file: their email, else their name, else the person importing.</summary>
    public static string PersonKey(ImportRow row) => row.Email ?? (row.UserName is { } name ? $"name:{Key(name)}" : "you");

    /// <summary>Names match whatever their case or surrounding spaces, as the database's unique names do.</summary>
    public static string Key(string name) => name.Trim().ToUpperInvariant();
}

/// <summary>Works out what importing a file into the workspace would do, without changing anything.</summary>
internal sealed class ImportPlanner(AppDbContext db, CurrentWorkspace workspace)
{
    /// <param name="assignments">Whose time each person's rows become, by <see cref="ImportPerson.Key"/>; none leaves them out.</param>
    public async Task<ImportPlan> PlanAsync(
        ParsedFile file,
        DateTimeZone zone,
        IReadOnlyDictionary<string, Guid?> assignments,
        CancellationToken cancellationToken)
    {
        var members = await (
            from member in db.WorkspaceMembers
            join user in db.Users on member.UserId equals user.Id
            where member.WorkspaceId == workspace.Id
            select new { user.Id, user.Email, user.FullName })
            .ToListAsync(cancellationToken);
        var byEmail = members.ToDictionary(m => m.Email!.ToLowerInvariant(), m => m.Id);
        // A name finds someone only if no one else in the workspace has it.
        var byName = members.GroupBy(m => ImportPlan.Key(m.FullName)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Id);
        var importer = members.Single(m => m.Id == workspace.UserId);

        var memberIds = members.Select(m => m.Id).ToHashSet();

        Guid? Owner(ImportRow row)
        {
            if (assignments.TryGetValue(ImportPlan.PersonKey(row), out var assigned))
            {
                return assigned is { } id && memberIds.Contains(id) ? id : null;
            }

            if (row.Email is { } email)
            {
                return byEmail.TryGetValue(email, out var id) ? id : null;
            }

            if (row.UserName is { } name)
            {
                return byName.TryGetValue(ImportPlan.Key(name), out var id) ? id : null;
            }

            return workspace.UserId;
        }

        DateTimeOffset Instant(LocalDateTime local) => local.InZoneLeniently(zone).ToDateTimeOffset();

        var owned = file.Rows.Select(row => (Row: row, UserId: Owner(row))).ToList();
        var people = owned
            .GroupBy(x => ImportPlan.PersonKey(x.Row))
            .Select(g =>
            {
                var first = g.First();
                var name = first.Row.UserName ?? first.Row.Email ?? importer.FullName;
                var seconds = g.Sum(x => (long)Period.Between(x.Row.Start, x.Row.End, PeriodUnits.Seconds).Seconds);
                return new ImportPerson(g.Key, name, first.Row.Email, first.UserId, g.Count(), seconds);
            })
            .OrderByDescending(p => p.UserId is not null)
            .ThenBy(p => p.Name)
            .ToList();

        var planned = owned
            .Where(x => x.UserId is not null)
            .Select(x => new PlannedEntry(x.Row, x.UserId!.Value, Instant(x.Row.Start), Instant(x.Row.End)))
            .ToList();

        // What is in the workspace already, or twice in the file, is not added again; so a file can be imported twice.
        var existing = new HashSet<(Guid, long, long, string)>();
        // A placed entry's times are ours, not the file's, and move when the file holds other entries of that day. It
        // is there already if its person has an entry that day as long and described the same; each matches once.
        var sameDay = new Dictionary<(Guid, LocalDate, long, string), int>();
        if (planned.Count > 0)
        {
            var userIds = planned.Select(p => p.UserId).Distinct().ToArray();
            var from = planned.Min(p => p.Start).AddDays(-1);
            var to = planned.Max(p => p.Start).AddDays(1);
            var entries = await db.TimeEntries.AsNoTracking()
                .Where(e => e.WorkspaceId == workspace.Id && userIds.Contains(e.UserId) && e.Start >= from && e.Start <= to && e.Stop != null)
                .Select(e => new { e.UserId, e.Start, e.Stop, e.Description })
                .ToListAsync(cancellationToken);
            existing.UnionWith(entries.Select(e => (e.UserId, e.Start.ToUnixTimeSeconds(), e.Stop!.Value.ToUnixTimeSeconds(), e.Description)));
            if (planned.Any(p => p.Row.Placed))
            {
                foreach (var e in entries)
                {
                    var key = (e.UserId, NodaTime.Instant.FromDateTimeOffset(e.Start).InZone(zone).Date, e.Stop!.Value.ToUnixTimeSeconds() - e.Start.ToUnixTimeSeconds(), e.Description);
                    sameDay[key] = sameDay.GetValueOrDefault(key) + 1;
                }
            }
        }

        var fresh = new List<PlannedEntry>();
        foreach (var entry in planned)
        {
            if (entry.Row.Placed)
            {
                var key = (entry.UserId, entry.Row.Start.Date, entry.Stop.ToUnixTimeSeconds() - entry.Start.ToUnixTimeSeconds(), entry.Row.Description.Trim());
                if (sameDay.GetValueOrDefault(key) is > 0 and var count)
                {
                    sameDay[key] = count - 1;
                }
                else
                {
                    fresh.Add(entry);
                }
            }
            else if (existing.Add((entry.UserId, entry.Start.ToUnixTimeSeconds(), entry.Stop.ToUnixTimeSeconds(), entry.Row.Description.Trim())))
            {
                fresh.Add(entry);
            }
        }

        var clients = await db.Clients.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(cancellationToken);
        var projects = await db.Projects.Where(p => p.WorkspaceId == workspace.Id).ToListAsync(cancellationToken);
        var tasks = await db.Tasks.Where(t => t.WorkspaceId == workspace.Id).ToListAsync(cancellationToken);
        var tags = await db.Tags.Where(t => t.WorkspaceId == workspace.Id).ToListAsync(cancellationToken);

        return new ImportPlan
        {
            File = file,
            Entries = fresh,
            Duplicates = planned.Count - fresh.Count,
            Skipped = file.Rows.Count - planned.Count,
            People = people,
            Clients = clients.DistinctBy(c => ImportPlan.Key(c.Name)).ToDictionary(c => ImportPlan.Key(c.Name)),
            Projects = projects.DistinctBy(p => (p.ClientId, ImportPlan.Key(p.Name))).ToDictionary(p => (p.ClientId, ImportPlan.Key(p.Name))),
            Tasks = tasks.DistinctBy(t => (t.ProjectId, ImportPlan.Key(t.Name))).ToDictionary(t => (t.ProjectId, ImportPlan.Key(t.Name))),
            Tags = tags.DistinctBy(t => ImportPlan.Key(t.Name)).ToDictionary(t => ImportPlan.Key(t.Name)),
            ExistingProjectCount = projects.Count,
        };
    }
}
