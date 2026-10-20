using System.Text.Json;
using ModelContextProtocol.Protocol;
using Spindle.Api.AgentActivity;

namespace Spindle.Api.Mcp;

/// <summary>
/// The tools as agents see them, and what an agent is told when it connects: those of the apps'
/// former local MCP, with the evidence from synced agent activity instead of the computer's logs.
/// </summary>
internal sealed partial class McpTools
{
    /// <summary>The most entries one update_entries call changes.</summary>
    public const int UpdateLimit = 100;

    public const string Instructions = """
        Spindle is the person's time tracker. Use these tools when they ask about their tracked time, or ask you to describe, add, change, start or stop it. Times are in the person's time zone; dates are YYYY-MM-DD. You reach one of their workspaces, the one they chose when they connected you.

        Describing entries: list_entries, then get_evidence for the ones to describe, then one update_entries call for all of them. Only entries without a description are filled unless the person asks to overwrite; undo_last_update takes back the last update_entries call.
        Evidence comes from the agent activity the person's Macs sync to Spindle, if they turned that on: when they prompted agents, in which repository and on which branches. Prompts and commits never reach Spindle; each repository comes with its git origin, the time window and a git log command, so if you have the repository at hand, read its commits for that window yourself.

        How to write descriptions, unless the person wants something else (their wishes in the conversation always win, e.g. another language, more detail, ticket numbers first, or a fixed format):
        - Match recent_descriptions: their language, tone, length and habits, such as ticket prefixes or capitalisation.
        - Otherwise one short line, starting with a verb, about the outcome: "Fix the login redirect and add tests", not "Worked on stuff" or "Claude Code session".
        - Name the feature, bug or ticket the person would recognise from branches, commits and prompts; keep issue numbers such as #142 or ABC-12. Leave out tools, file names and the agent itself.
        - An entry with several things gets the main two or three, joined with "; ".
        - When the evidence is thin, say less rather than guess, or ask.
        - An entry split between projects gets a description per project (parts in update_entries, a description in each part of a proposed split), each about its own project's work only, since each project's reports and client timesheets show only its own part's: match the split's projects to the evidence's repositories. One description for all parts only when the work was the same, e.g. a meeting about both.

        Adding and changing time: time, projects and splits are the person's to confirm. Propose new entries with propose_entries and changes with propose_changes, all in one call, each with a short reason; the person reviews them in Spindle. Tell the person they wait in Spindle; list_proposals says what became of them.
        Filling in a day: find_untracked_time shows when agents were busy and no entry covers it, with the evidence for each stretch and its runs per repository. Propose an entry per piece of work on the project of its repository, described as above: runs one after another make consecutive entries, runs at the same time a split by their share, each part described with its own repository's work. Ask which project a repository without one belongs to. It only sees agent work: ask about meetings, calls and other gaps rather than filling them, and never fill a day on a guess.

        For totals over a period, by project, client, task or day, use get_report.
        """;

    private const string TimeHint = "Like 2026-10-12T14:30 in the person's time zone, or with an offset.";
    private const string ReasonHint = "Why, in a few words the person sees beside it, e.g. \"Commits on fix/apns 14:02–15:40\".";

    private static object Schema(Dictionary<string, object>? properties = null, string[]? required = null) =>
        new { type = "object", properties = properties ?? [], required = required ?? [], additionalProperties = false };

    private static object String(string description = "") => new { type = "string", description };

    private static Tool Tool(string name, string title, string description, bool readOnly, object? schema = null) => new()
    {
        Name = name,
        Title = title,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(schema ?? Schema()),
        Annotations = new ToolAnnotations
        {
            Title = title,
            ReadOnlyHint = readOnly,
            DestructiveHint = false,
            IdempotentHint = readOnly,
            OpenWorldHint = false,
        },
    };

    private static object SplitSchema => new
    {
        type = "array",
        minItems = 2,
        maxItems = 6,
        description = "To split the time between projects instead of project_id: whole percentages that add up to 100. Describe each part with its own work instead of giving the entry one description.",
        items = Schema(
            new()
            {
                ["project_id"] = String(),
                ["task_id"] = String(),
                ["percent"] = new { type = "integer", minimum = 1, maximum = 99 },
                ["description"] = String("What was done on this project, one short line; only its reports and timesheets show it."),
            },
            ["project_id", "percent"]),
    };

    private static readonly string[] GroupingNames = ["project", "client", "task", "tag", "description"];

    public static IList<Tool> Tools { get; } =
    [
        Tool("get_timer", "Running timer", "The running timer, if any, with the person's time zone, today's date and the workspace. A timer in another workspace is only said to run.", true),
        Tool("list_entries", "List entries", "The person's time entries that start between two dates, inclusive, today by default, at most 31 days. Each has its project, task, tags and, when split between projects, its parts.", true,
            Schema(new() { ["from"] = String("First day, YYYY-MM-DD. Default today."), ["to"] = String("Last day, YYYY-MM-DD. Default the same as from.") })),
        Tool("list_projects", "List projects", "The workspace's projects with their clients and open tasks, for project_id and task_id in the other tools.", true,
            Schema(new() { ["include_archived"] = new { type = "boolean", description = "Also archived projects. Default false." } })),
        Tool("get_evidence", "Evidence for entries", "What Spindle knows about each entry's agent work from the activity the person's Macs synced (only if they turned activity sync on): per repository the person's attention in minutes, prompt count, branches, session titles if synced, the project it belongs to, and its git origin with the time window and a git log command, so you can read the commits yourself if you have the repository; a suggested split, if any, which the person confirms in the app; and the entry project's recent descriptions, for style. Says plainly when nothing was synced.", true,
            Schema(new() { ["entry_ids"] = new { type = "array", items = String(), minItems = 1, maxItems = 50, description = "IDs from list_entries." } }, ["entry_ids"])),
        Tool("update_entries", "Describe entries", "Writes descriptions, and optionally tasks of the entry's own project, for many entries at once; a split entry's parts can each get their own description. Only entries (or parts) without a description, and entries without a task, are changed unless overwrite is true, which needs the person's say-so. Times, projects and splits can't be changed here; propose those with propose_changes. Returns what changed and what was left and why.", false,
            Schema(new()
            {
                ["updates"] = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = UpdateLimit,
                    items = Schema(new()
                    {
                        ["id"] = String(),
                        ["description"] = String("What was done, one short line."),
                        ["task_id"] = String("A task of the entry's own project, from list_projects."),
                        ["parts"] = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = 6,
                            description = "For an entry split between projects, instead of description: what was done on each project's part, which only that project's reports and timesheets show. Parts left out keep what they show.",
                            items = Schema(
                                new()
                                {
                                    ["project_id"] = String("A project of the entry's split, from list_entries."),
                                    ["task_id"] = String("Only when the split has two parts on that project."),
                                    ["description"] = String("What was done on that project, one short line."),
                                },
                                ["project_id", "description"]),
                        },
                    }, ["id"]),
                },
                ["overwrite"] = new { type = "boolean", description = "Replace descriptions and tasks already there. Only when the person asked to." },
            }, ["updates"])),
        Tool("undo_last_update", "Undo last update", "Takes back the last update_entries call. Entries changed again since are left as they are.", false),
        Tool("start_timer", "Start timer", "Starts the timer now. A timer already running in this workspace stops where this one starts; one running in another workspace is the person's to stop.", false,
            Schema(new() { ["description"] = String(), ["project_id"] = String("From list_projects."), ["task_id"] = String("A task of that project.") })),
        Tool("stop_timer", "Stop timer", "Stops the running timer now. If the workspace requires details the entry lacks, it keeps running and says which.", false),
        Tool("continue_entry", "Continue entry", "Starts the timer now with an earlier entry's description, project, task, tags and split. A timer already running in this workspace stops where this one starts; one running in another workspace is the person's to stop.", false,
            Schema(new() { ["entry_id"] = String("From list_entries.") }, ["entry_id"])),
        Tool("propose_entries", "Propose entries", "Proposes finished entries to add, e.g. time the person forgot to track. Nothing is saved: the proposals wait in Spindle, where the person reviews them beside their other entries and adds, edits or discards each. Propose only time the person told you about or the evidence shows. Returns the proposals made and what was refused and why.", false,
            Schema(new()
            {
                ["entries"] = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = AgentProposalRules.Limit,
                    items = Schema(new()
                    {
                        ["start"] = String(TimeHint),
                        ["stop"] = String(TimeHint),
                        ["description"] = String("What was done, one short line."),
                        ["project_id"] = String("From list_projects."),
                        ["task_id"] = String("An open task of that project."),
                        ["split"] = SplitSchema,
                        ["reason"] = String(ReasonHint),
                    }, ["start", "stop"]),
                },
            }, ["entries"])),
        Tool("propose_changes", "Propose changes", "Proposes changing existing entries: their start, stop, project, task, split or description. Nothing is saved: each change waits in Spindle, where the person sees it beside the entry and applies or discards it. Only what is given changes. A new proposal for an entry replaces the one waiting for it. A split given without descriptions in its parts keeps those the parts have; a description for the whole entry replaces them.", false,
            Schema(new()
            {
                ["changes"] = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = AgentProposalRules.Limit,
                    items = Schema(new()
                    {
                        ["id"] = String("The entry, from list_entries."),
                        ["start"] = String(TimeHint),
                        ["stop"] = String(TimeHint),
                        ["description"] = String(),
                        ["project_id"] = String("From list_projects. Ends a split unless split is given."),
                        ["task_id"] = String("An open task of the project."),
                        ["split"] = SplitSchema,
                        ["reason"] = String(ReasonHint),
                    }, ["id"]),
                },
            }, ["changes"])),
        Tool("list_proposals", "List proposals", "The proposals waiting for the person in Spindle, each with what stands in its way if anything, and what became of those dealt with in the last week: added, changed, discarded or expired.", true),
        Tool("withdraw_proposals", "Withdraw proposals", "Takes back proposals still waiting, e.g. ones made by mistake.", false,
            Schema(new() { ["proposal_ids"] = new { type = "array", items = String(), minItems = 1, description = "From propose_entries, propose_changes or list_proposals." } }, ["proposal_ids"])),
        Tool("find_untracked_time", "Find untracked time", "When agents were busy with no entry or waiting proposal covering it, between two dates (at most 7 days), from the agent activity the person's Macs synced (only if they turned activity sync on): per stretch its start and stop, its runs per repository (with the project each belongs to) and the same evidence as get_evidence, to propose entries from with propose_entries. Only agent work shows; ask the person about the rest. Says plainly when nothing was synced.", true,
            Schema(new() { ["from"] = String("First day, YYYY-MM-DD. Default today."), ["to"] = String("Last day, YYYY-MM-DD. Default the same as from.") })),
        Tool("get_report", "Report", "The person's own tracked time between two dates, inclusive: the total, the billable part, and the time by a grouping, as Spindle's reports count it (each part of a split entry on its own project). At most 366 days.", true,
            Schema(new()
            {
                ["from"] = String("First day, YYYY-MM-DD."),
                ["to"] = String("Last day, YYYY-MM-DD. Default the same as from."),
                ["group_by"] = new { type = "string", @enum = GroupingNames, description = "Default project." },
                ["sub_group_by"] = new { type = "string", @enum = GroupingNames },
                ["project_ids"] = new { type = "array", items = String(), description = "Only these projects." },
                ["billable"] = new { type = "boolean", description = "Only billable time (true) or only non-billable (false)." },
            }, ["from"])),
    ];
}
