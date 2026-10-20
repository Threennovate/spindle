# Splits and suggestions

How split entries work, and how the apps suggest splits from Claude Code, Codex and GitHub Copilot CLI logs. Why Spindle works this way is
in [principles.md](principles.md); this page is how the code does it.

## Split entries

- One time entry can divide its time between 2–6 parts, each a project (and optionally a task) with a billable flag
  and a whole percentage; the parts add up to 100. Stored as `TimeEntry.Shares` (jsonb) with an `IsSplit` column.
- The entry's own `ProjectId`, `TaskId` and `IsBillable` mirror its largest part, so older clients still show it. A
  request with `shares` null (an older client) keeps the split while the project is unchanged.
- Everything that counts time counts each part on its own project, at that project's rates: the whole entry is
  rounded first, then divided by largest remainder (`TimeShare.Divide`), so the parts add up to the entry exactly.
  The web (`split.ts`) and the Kit's, Windows' and Android's `SplitMath` mirror this. Reports, billed days, billing
  and agreements (each part counts towards the agreement of its own task) all do this. Any new code
  that sums time must too.
- Descriptions: by default one description covers all parts, and every project's reports and timesheet links show
  it. The parts can instead be described one by one (`TimeShare.Description`, request `describeParts: true`); each
  project's reports, billed days and timesheet links then show only its own part's text, and the entry's description
  is made of the parts' joined with "; " for lists and older apps. A request that leaves `describeParts` out (older
  apps) keeps the parts' descriptions while it sends the entry's description unchanged; a changed one becomes one
  description for all again, as does a bulk description change. A required description is then required of each
  part. The web, Mac and Windows editors describe parts ("Describe per project" under the description of a split,
  "One description for all" to join them back; the one description so far goes to the largest part), the iPhone editor
  with a "Describe Each Project" switch in its description section, and so does the person's agent through the
  server's MCP (`update_entries` with `parts`, a description per project; `list_entries` shows them).
  Proposed splits (`propose_entries`, `propose_changes`) can carry a description in each part;
  a split proposed without them keeps the parts' own, and one description for the whole entry replaces them. Both
  apps' reviews show such a proposal as a line per project.
- `@Project` in a description (web: the timer, manual entries and the editor) says what was done on which project:
  typing `@` offers the open projects, and the text after a mention belongs to that project up to the next one; text
  before the first goes to the entry's own project, or to the first mentioned when it has none. Only exact names count,
  so a typo stays text, and a line under the field shows where the time and each text go before anything is saved. A
  mentioned project not yet in the split joins it (an even split, or an equal share added to an existing one); one
  project alone is no split. A running entry described per project shows its parts as mentions, so editing that line
  rewrites all of them. In the editor a mention switches to describing per project at once (`mentions.ts`).
- Editing: web, Mac and Windows have a bar with draggable dividers (5% steps; 1% with ⌥, or Alt on Windows), rows per part,
  and "Split" in the project picker. iPhone and Android have the same bar (5% steps), a page per part (project, share, billable, remove)
  and "Split between projects" under the project; Android's `SplitMath` (Kotlin) shares the Swift test cases.
- A split can also be saved as separate consecutive entries instead ("Split at 10:42"): the existing entry becomes
  the last stretch, so a running timer keeps running, and earlier stretches are created. `SplitApplier` (Mac) does
  this for the editor and for one-click offers. It returns what it changed (`SavedChange`: the entry as it was and the
  IDs it chose for the entries it added, through `Tracker.save(_:as:id:)`), so Undo can delete those and save the
  entry back. Windows does the same in `SplitAdvisor.ApplyAsync` and `UndoAsync`.

## Reading agent logs (Mac, `SpindleKit/Activity`)

The Windows app reads the same logs with the same rules in `windows/Spindle.Activity` (see its
[README](../windows/Spindle.Activity/README.md)); the shared test cases below keep the two in step.

- Sources: `~/.claude/projects/**/*.jsonl`, `~/.codex/sessions/**/rollout-*.jsonl` (+ `~/.codex/session_index.jsonl`
  for titles) and `~/.copilot/session-state/<session>/events.jsonl`. The Mac app is Developer ID signed and not
  sandboxed, so no setup or permission is needed. The agents' own overrides of these folders (`CLAUDE_CONFIG_DIR`,
  `CODEX_HOME`, `COPILOT_HOME`) aren't followed: the apps don't see the environment of the shell an agent ran in, so
  logs written elsewhere aren't read.
- Where a source keeps its logs is its `AgentLogFormat`, with the same fields in the Kit (`AgentLogFormat.swift`) and
  on Windows (`AgentLogFormat.cs`): its folder, which files are logs (a name or a prefix, and how many folders below
  the logs folder), which logs make one session, and a title index (Codex's `session_index.jsonl`). The loader, the
  watcher, settings, the calendar's lanes and the upload go through it; how a source's lines read, and its index's
  rows, are its part of `AgentLogParser`. `tests/vectors/agent-logs.json` has the cases both run: lines and the
  sessions read from them (with text that must never survive), and paths and whether each is a log and of which
  session. A new source is a case in both and cases there; the server takes any agent named in lower-case letters and
  digits (`AgentSession.SourcePattern`), so it needs no deploy. Only line-based logs fit; a SQLite store would need a
  reader of its own.
- Each source has its checkbox under "Suggest splits from agent activity" (Mac: `activity.<source>` in the defaults;
  Windows: `ReadClaudeLogs`, `ReadCodexLogs`, `ReadCopilotLogs`), and the calendar has a lane for each one read. A
  source added later starts off for someone who was already reading agent logs, who agreed to the ones named then, and
  on for anyone else, so turning agent logs on reads what the offer names; it is kept at once, so turning them on later
  doesn't change it. A new source needs no parser version: its logs have no records yet, so they are read, and the
  uploader reads the 90 days again when the sources it reads grow (state from before had Claude Code and Codex), and
  sends what it hasn't yet.
- `AgentLogParser` keeps only timestamps, `cwd`, branch and an 80-character title per session; prompt text and answers
  are discarded. A **touch** is a prompt the person sent: in Claude Code a real user message (not a tool result,
  sidechain, meta or system line), in Codex `task_started` in a top-level session, in Copilot a `user.message` that
  isn't autopilot's continuation (`isAutopilotContinuation`), injected (`source`: a skill or another agent), a
  subagent's (`agentId`) or a slash command (a word after one slash, `/model`; a prompt that starts with a path is a
  prompt). Subagents make no touches and belong to their root session's repository. A prompt whose turn only calls Spindle's own MCP tools
  (`mcp__spindle__…`; Copilot names the server in `mcpServerName`, and its `report_intent` says nothing either way)
  is no touch either: asking an agent to describe today's entries is not work in the repository
  it was asked from.
- `JSONScanner` lets the parser skip lines that only add a timestamp (tool output, model turns) without decoding them,
  and only for lines the full decoder would accept; anything else is decoded in full, so results are identical.
- `AgentActivityLoader` (an actor) reads files incrementally from where it stopped, and keeps what it parsed in an
  on-disk ledger (`ActivityLedger`, Application Support/…/AgentActivity) so a launch only reads what logs gained.
  Records of logs the agent has since deleted are kept for 400 days. Turning agent logs off deletes the ledger.
  Bump `ActivityLedger.version` whenever the parser reads logs differently: logs still on disk are read again, and a
  deleted log's older record keeps counting, as it can't be read again.
- Interactive days (calendar, suggestions, stop and switch offers) have priority over upload work.
  `AgentActivityLoader` remains the only owner of parsed-file caches and the ledger. Upload reads are coalesced:
  equal requests join, a different request cancels the old one, and clearing activity cancels pending work. Each
  upload file is parsed on a detached utility task, with an independent decoder; the actor is available to read
  interactive days even while that file is being read. It commits the result only if the file still has the same
  stamp and activity has not been cleared (or the log was deleted and no newer record exists). An old parse cannot overwrite an interactive read of a newer append.
  Cached files, retained records, parents and repositories are processed with yields between them; sorting and
  deduplicating the combined upload events also run on a utility task.
  This avoids a second ledger writer or a second lasting parse cache. A cold interactive read can parse the same
  file an upload is currently reading; the temporary extra work is the trade-off for not waiting for it.
- `Activity wait` measures an interactive request's queue time before it starts; `Activity load` and `Activity parse`
  measure its work. `Activity sessions for upload` includes the whole coalesced background read. Ledger operations and directory listings still happen on the loader actor;
  the wait signpost makes any remaining cost visible in a real-data profile.
- Repositories resolve to their git root; worktrees merge into their main repository. Folders outside git don't count.
  Away from the Mac a repository is named by its identity (`RepositoryIdentity`): its normalised git origin without
  credentials, or a hash of its path.
- `ActivityTimeline` builds a day: attention segments (each touch to the next touch anywhere, at most 15 min), focus
  blocks, and agent runs (events within 3 min). Attention is sequential; runs may overlap but are not used for splits.
  A segment carries the branches of its sessions; one repository's attention joins across prompts only while the
  branches stay the same, so branch rules can tell two sessions in one repository apart. The server and Windows build
  theirs the same way, from the branches each session already has (no upload change).
- The Mac app watches both folders with FSEvents (`ActivityWatcher`) and reads today again at most every 15 s while
  agents write; without it, it falls back to a timer.

## Suggestions

`SplitSuggestion.suggest` (Kit), for an entry's time range and the project on the timer (`fallback`):

- Each attention segment goes to its repository's project. Repository → project: a branch rule on the repository's
  link, then the person's choice, then a link learned from single-repository entries, then a project named like the
  repository (`RepositoryLinks`, by identity; `Matcher.place` reads a segment's branches, `project(for:)` doesn't).
  A repository the person set to **No project** has none: neither a learned link nor a name takes its work, and it is
  named as itself (`RepositoryLinks.noProject`, vector "a repository the person set to no project…").
- Time between two consecutive prompts for the same project goes to it however long it was.
- Time the logs don't account for stays on the timer's project.
- Parts under 10% are dropped; under 10 min of evidence suggests nothing.
- When the work moved from project to project (each project's time in one place, detours under 10 min ignored), it
  also returns `stretches`, offered as "Split at 10:42". When it went back and forth, only percentages.

### Repositories that belong to more than one project

A monorepo, or maintenance and a feature billed separately, can't be one project. **Branch rules** (`BranchRule` in
the Kit, the server's and Windows' `SplitSuggestion.cs`) sit on a repository's link, in the person's order: a pattern
and the project, and optionally the task, its work goes to. The link's own project takes the other branches.

- A pattern is a branch name where `*` stands for any characters, `/` included (`acme/*`, `ACME-*`, `*-hotfix`). Case
  doesn't matter; spaces around it don't count.
- A rule applies to a segment when its sessions have at least one branch and every one of them matches. The logs
  can't tell which branch a prompt was on, so a session that was on `main` and `acme/login` goes by the link. The
  first rule that applies wins; a rule whose project is archived or gone is skipped.
- **Tasks:** a part gets the task its pieces all have (`Part.taskId`; one piece without it, or another task, and the
  part has none). When a suggestion is taken, the entry's own project keeps the entry's task if it has one; a rule's
  task is dropped if it was finished or moved since. Separate entries ("Split at 10:42") get it too.
- Rules sync with the link: `PUT /api/workspaces/{id}/agent-activity/links` takes `branchRules` (none keeps them, an
  empty list clears them; each names an active project of that workspace and a task of that project, at most 20), and
  `GET /api/me/agent-activity/links` lists them. They are kept on `AgentRepositoryLinks.BranchRules` (`jsonb`).
- **Mac:** "Different Projects by Branch…" (`BranchRulesEditor`) opens from the branch button beside a part in the
  editor's banner, beside each repository in the onboarding's link step, and from Settings › General › Linked
  repositories. It lists the repository's recent branches and where each would go with the rules as written. Saving
  links the repository to the project for the other branches, with the rules.
- **Windows** applies the rules it gets with the links (suggestions, the change offers, untracked time, "Agents now",
  the rule's task when a split or untracked time is taken). "Different projects by branch…"
  (`Views/Split/BranchRulesEditor.cs`) works as the Mac's: it opens from the branch button beside a linked part in the
  editor's banner (over the editor, which comes back after), "Branches…" beside each repository in the onboarding's
  link step, and Settings › General › Parallel work › Linked repositories. Rules are kept on the PC until the account
  has them and the account's are read again when another device changes them (live sync topic `agentActivity`).
  **The web** gets suggestions worked out with the rules on the server; its
  `SplitSuggestionResponse` doesn't carry the rule's task yet.
- Cases are in `tests/vectors/split-suggestions.json` (`rules` per repository, a session's `branches`, a part's
  `task`, and `branchPatterns`), run by the Kit, the server and Windows.

### Unlinking a repository

Choosing **No project** for a repository unlinks it: `PUT …/agent-activity/links` with `noProject: true` keeps a link
without a project (`AgentRepositoryLinks.ProjectId` null, in no workspace) and drops its branch rules. Without
`noProject`, no project still forgets the link, and a learned link or a name can take the work again.
`GET /api/me/agent-activity/links?includeNoProject=true` lists such links with a null project and workspace; without
the parameter they are left out, so apps that predate them don't stumble on a null.

- **Mac:** Settings › General › Linked repositories shows each repository's project as a button that opens the project
  picker, "No Project" at the top; right-click a row for Unlink; "Unlink Repository" sits in the corner of "Different
  Projects by Branch…"; and "No project" in the picker for a repository in a suggestion or the day's review. Each says
  "Unlinked atlas" with Undo, which puts back the project and its rules (`Desktop.unlink`,
  `SplitAdvisor.choice(of:)`/`choose(_:for:)`). Unlinked repositories stay in the list as "No Project".
- **Windows:** the same places (`RepositoryLinkButton`, `BranchRulesEditor` "Unlink Repository",
  `SplitAdvisor.UnlinkAsync`/`ChooseAsync`).
- **The web:** "No project" in the picker for a repository in a suggestion, with Undo.

### How sure a suggestion is

Each suggestion is graded (`SplitSuggestion.strength`, the same in the server's and Windows' `SplitSuggestion.cs`,
by `tests/vectors/split-suggestions.json`). **Strong:** the logs account for at least 60% of the entry
(`strongCoverage`), and every part has at least two prompts sent within the entry (`strongPrompts`) and 10 minutes
(`strongPartDuration`; for the timer's own project, the time the logs leave open counts). **Weak:** anything else.
The thresholds are a first guess, not measured against real logs.

Only strong suggestions interrupt: the card after a stop, its notification (Windows: toast) and the menu bar's ✦.
Weak ones keep the quiet chip, the editor's banner and the menu bar panel's card. The running timer's change offers
("Switch at 10:42", "End Split at 11:05") aren't graded.

A suggestion carries what it was built from: its `range`, the `pieces` the logs place (attention and the time
between two prompts for a target), the `open` intervals, the `prompts` with their targets (`touches` counts them per
target) and the `fallback` project. Views draw from these and never work them out again.

The **evidence strip** (Mac `EvidenceStrip`, Windows `Views/Split/EvidenceStrip.cs`) draws them: the entry's time as
a thin line, prompts as ticks in their project's colour (a repository's while it has no project), attention as thin
bars, and the open time hatched in the timer's project's colour. It is in the editor's banner (with "The work moved to
Beacon at 10:42" under it), the card after a stop, and the chip's popover (Windows: tooltip), which also lists each
part's prompts and time. Screen readers get it in words: "Agent activity from 09:00 to 11:00: Atlas 50m, 4 prompts;
Beacon 1h 10m, 6 prompts. The work moved to Beacon at 09:50."

**In the calendar (Mac)**, pointing at a block's ✦ draws its suggestion on the block (`SplitPreview`), as a proposal
that can't be taken for a saved split: the block stays as it is, the proposed parts are hatched and translucent in
their projects' colours, with dashed lines between them and a dashed outline. Each part is labelled with its project
and time ("Atlas 0:50"; the time alone where it is too narrow, nothing on short blocks), and a move has a dashed cut
line with its time, kept inside the block when the last stretch is short. One line under the block names every part
and says what a click does: "Split Atlas 0:50 · Beacon 1:10 · Click ✦ to Apply", "Split at 10:50: Atlas 0:50 · Beacon
1:10 · Click ✦ to Apply", or "… · Click ✦ to Open" when one click can't take it (a repository without a project, or
details the workspace requires) and the editor opens instead. Narrow and short blocks can't name their parts inside,
so that line is the one place each is named; it sits under the block (over it at the end of the day) so the block's
own title, project and time stay clear, and they keep a backdrop over the parts. Labels are in the normal text colour,
not the project's. Weak suggestions are drawn fainter. The ✦ has no help tag, which would open over the preview it
explains; VoiceOver has the whole suggestion as an action on the block. The preview goes the moment the pointer leaves
the ✦, doesn't come back after a click until the pointer has left, and is checked again where the pointer rests when
the calendar redraws and blocks move. A click takes it, as the stop card does, with Undo, and withdraws only that
entry's stop card. With overlay scroll bars, a ✦ at the grid's right edge moves in by the scroll bar's width, so the
scroll bar doesn't take the pointer from it. The grid hit-tests the ✦ itself, as it does blocks; calendar blocks take
no keyboard focus on the Mac.

**"Agents now" (Mac and Windows)**: the project picker lists, at the top while nothing is typed, the projects agents worked in
during the last 15 minutes (`SplitAdvisor.agentsNow`; Windows `SplitAdvisor.AgentsNow`, `ProjectPicker` in
`Views/Pickers.cs`), most attention first, each with the task a branch rule names. Repositories without a project
don't count. Windows reads today's logs again when the picker opens, if they are a few minutes old, and leaves the
group out where a project is chosen for a repository.

### When the work changes after a split

A split someone set, by hand or from a suggestion, is never judged against the agents' prompts: people know where
their attention went better than the logs, and work outside agents doesn't show in them. Only the time after the
split was set is looked at, to offer once to end the entry there and carry on in a new one.
`SplitDrift.detect` (Kit; Windows `SplitDrift.Detect` in `windows/Spindle.Activity`, both running
`tests/vectors/split-drift.json`) uses the same attention and repository links as `suggest`:

- **Quiet:** a part's project had agent attention earlier in the entry, then none for 30 min, while the split's other
  projects had at least 10 min of attention in those 30 min, some of it after the first 15 (so one last prompt before
  everyone stopped doesn't count). A part that never had attention in the entry is never judged. Several quiet
  projects end at the latest of them, and at least one part must remain.
- **Joined:** a linked project outside the split had at least 15 min of attention since the split was set (not when
  the split already has six parts).
- The quiet window starts no earlier than the statement time, so a split just set never triggers at once. The cut is
  at the nearest whole minute: the end of the quiet project's last attention, or the start of the new project's
  first. When both happened, the earlier is offered.
- **The statement time** is kept on the device: `SplitAdvisor` remembers, for the last 20 entries, a hash of the
  running entry's parts and when it first saw them (`UserDefaults` `splitStatements` on the Mac, `SplitStatements` in
  Windows' preferences file), noted every tick for split and unsplit running entries alike. A split already there
  when the timer started counts from the start; one first seen later (another device, or before this launch) counts
  from then, which only makes the offer later. Changed parts start over. No server change.
- **One click** (`SplitApplier.endAndContinue`, Windows `SplitAdvisor.EndAndContinueAsync`) ends the entry at the cut
  with its split unchanged and adds a new entry from the cut, running when the timer was. Quiet: the rest keep their
  ratio (`SplitDrift.continuing`: 50 · 30 · 20 without the 20 is 63 · 37), or one project when one is left. Joined:
  the parts plus the new project with an equal share, as `@Project` mentions add one. Description, tags, tasks and
  the parts' own descriptions carry over; a project left alone takes its part's own description. Undo deletes the new
  entry and lets the first run on again (`SavedChange.restoring` keeps a stop only when the timer stopped since, on
  the entry or on the one added).

### Time nobody tracked

Agent attention with no entry is the one place the apps speak up without an entry to attach to. `UntrackedActivity`
(Kit, the same rules as the server's `src/Spindle.Domain/AgentActivity/UntrackedActivity.cs`, which the MCP's
`find_untracked_time` uses, and Windows' `windows/Spindle.Activity/UntrackedActivity.cs`) finds it: attention no entry
covers, pieces joined across 20 min, at least 5 min long. It never claims more than the logs show: a stretch is the
attention only (a prompt and up to 15 min after it). The Kit and Windows both run the cases in
`tests/vectors/untracked-activity.json` (stretches, the earlier start and the work going on now); change the rules
on all three sides and add a case. On the Mac and Windows, with "Suggest splits from agent activity" on (not the beta
calendar lanes):

- **"✦ Start at 9:12?"** For the first 10 minutes after a timer starts, however it was started, the timer bar shows
  a chip when an untracked stretch reaches within 5 min of the start (`UntrackedActivity.earlierStart`,
  `SplitAdvisor.earlierStart`). Never earlier than the previous entry's stop, never across midnight, never into a
  locked day; on a whole minute. When the agents' project isn't the timer's, the chip says so ("Agents were in
  Beacon since 9:12"). One click moves the start; a project the person chose stays. A timer without a project gets
  the agents' along with the start ("✦ Start Atlas at 9:12?", "Started Atlas at 9:12"), since it replaces nothing the
  person chose (`EarlierStart.fillsProjectId`). "Started at 9:12" with Undo. Windows: `SplitAdvisor.StartEarlier`, `SplitChips.EarlierStartChip` in the timer bar,
  `DesktopService.TakeAsync(EarlierStart)`.
- **The reminder to track** (`remindToTrack`, its working hours, and only while the person is at the Mac) names the
  work once one linked project has had 15 min of attention in the stretch going on now
  (`UntrackedActivity.ongoing`): "Agents have been working in Atlas since 9:12" / "Start Atlas from 9:12?", with
  "Start from 9:12" and "Not Now". A new stretch is named without waiting for the reminder's interval, once; after
  that the reminder keeps its interval. Repositories without a project don't count, so they never notify; without
  such work the reminder stays as it was. Starting from it says "Started Atlas from 9:12" with Undo. Windows reads
  today's logs for it only in working hours, and shows it as the reminder's toast (`DesktopService.RemindersAsync`,
  `SplitAdvisor.OngoingUntracked`, "Start from 9:12" / "Not now").
- **The calendar** shows past untracked stretches as ghost blocks: no fill, a hatched edge in the project's colour,
  "✦ Atlas · 14:00–15:30" (several projects by attention, "Not tracked" when none is linked). They never sit under an
  entry or a proposal waiting for review, and not on locked days. The dashed blocks are only agents' proposals. A click
  opens a new entry for the stretch with its project, or the split its attention suggests, filled in
  (`SplitAdvisor.editing(newRange:…)`); right-click offers Add Entry… and Dismiss. Dismissals are kept on this Mac, by
  day and start minute (`UserDefaults` `dismissedUntrackedTime`). Windows draws them the same way (`HatchedEdge`,
  `CalendarView.Untracked`) and keeps dismissals in its preferences file (`DismissedUntrackedTime`).

The calendar and the day's review show only stretches with a prompt of their own (`SplitAdvisor.untracked`): the
minutes the logs assume after the last prompt before a stop are no sign of work nobody tracked. `UntrackedActivity`
itself stays as the server has it.

The web shows untracked time in its calendar too (below).

### Where suggestions reach the person

`SplitAdvisor` (Mac; Windows has its own) caches activity per day and decides what to offer. It is off unless "Suggest splits from agent
activity" (`DesktopSettings.useAgentLogs`) is on, which is separate from showing activity in the calendar.

Its day cache is scoped to the enabled sources, log consent, the person's time zone and the snapshot fixture root.
Changing any of them drops the old days, learned links and offers; visible calendar, list and editor tasks read
again, as does the running-timer watcher. Results read under an old configuration are discarded. Up to 128 recently
used days are kept, enough for several weeks and the boundary days of many entries; evicting a day also forgets
its load time and watcher revision. Offers load their days first, making them recently used. Today is still read
again on a watcher revision (or the fallback timer). Suggestions and link learning stay on the main actor. It keeps
each entry's suggestion until the entry, the activity, the links or the projects change (a running entry's for a
minute), so the calendar, the entry list, the timer bar and the menu bar can ask as often as they draw.

Where suggestions reach the person, each at most once per entry, and never saved without a click:

1. While the timer runs (`Desktop.watchParallelWork` → `SplitAdvisor.changeOffer`; Windows
   `DesktopService.ChangeOfferAsync`): a notification "Still on Atlas?" when every prompt for 15+ minutes went to
   another project; "Switch to Beacon" moves the timer from the moment the work moved. "Still on Atlas" only answers
   this question: the switch isn't offered again for that entry, but the split can still be offered when the timer
   stops. For a split entry whose work changed (above), the same notification says "Beacon has been quiet since
   11:05" or "Agents have been in Comet since 11:20", with "End Split at 11:05" / "Add Comet from 11:20" and "Not
   Now", once per split; the timer bar shows it as a chip with a menu (take, Review…, Not Now) and the menu bar as
   a card for as long as it applies. Ignoring the notification does nothing; "Not Now" (or taking it, or Review…)
   hides it until the split changes. Review… opens the entry in the editor.
2. When the timer stops (`Desktop.runningChanged` → `SplitAdvisor.offerAfterStop`), for a strong suggestion only: a
   card in the window with the evidence strip (Keep as Is, Review…, Split or Split at 10:42), or a notification when
   the app is in the background. A split entry gets
   only "End Split at 11:05" (or "Add Comet from 11:20"), and only when nobody answered it while the timer ran.
   The card says "Also in today's review": when it goes away after 45 seconds, nothing is lost.
3. Later: a chip beside the entry in the list and calendar; also beside "Add" in manual mode and in the editor.
   Chips show hours with each project's colour ("✦ 1:20 · 0:40", whole minutes that add up to the entry), or
   "✦ Split at 10:42"; percentages are only in the editor, where a split is set. On Windows the calendar block's ✦
   draws the suggestion on the block only while it is hovered or has the keyboard focus, and never after a click
   (`Views/Calendar/SuggestionPreview.cs`). It looks like a proposal, not a split: the block stays as it is, each
   part is hatched over it in its project's colour (columns as wide as the shares, or stretches as tall as their time)
   with dashed cuts, and the block's own words keep a soft backing, so neither hatching nor a cut runs through them.
   Each part is labelled "Atlas 0:50" ("0:50" when the name doesn't fit), each move's time sits on its cut, and one
   line says what a click does and names every part, as the web's does: "Split Atlas 0:50 · Beacon 1:10 · click ✦ to
   apply" (shorter forms where it doesn't fit) or "Split at 10:50 · click ✦ to apply". Weak suggestions are fainter,
   with finer dashes.
   A click or Enter takes it like the review does (`DesktopService.TakeAsync`), with Undo; when one click can't (a
   repository without a project, details the workspace needs) the editor opens instead. Screen readers hear "Split at
   10:42: Atlas until 10:42, then Beacon", with the evidence strip's sentence as help.
4. In the day's review (below), with everything else the day has, weak suggestions included.

### The day's review

One review instead of many interruptions: a day (or a week) with every suggestion in its place, so a day can be
closed in one go. `SplitAdvisor.review(on:)` collects them (`ReviewItem`, `Mac/Split/DayReview.swift`), in order:

- **Split**: a finished entry on one project with a suggestion, strong or weak, with its evidence strip. Weak ones say
  how much of the time the logs cover. A repository without a project is linked right in the row.
- **Drift**: a finished split entry whose work changed after its split was set and nobody answered that ("End Split at
  11:05"). Only entries this Mac saw run have a statement, so only they can have one.
- **Untracked**: agent time nobody tracked, as in the calendar ("Add Entry"). One click saves the stretch on its
  project, or as the split or separate entries its attention suggests; a stretch only in repositories without a
  project needs Change….
- Agents' proposals still have their own review (`ProposalReview`). `ReviewItem` is where they join, so the day has
  one inbox.

Running entries, locked days and entries kept as they are aren't in it. Each row says what the time is now, struck
through, and what one click makes of it (`ReviewItem.results`): "Atlas 1:30 → 2 entries: Atlas 09:00–09:40 · Beacon
09:40–10:30", "Beacon 1:30 → Beacon 0:50 · Atlas 0:40", "Nothing tracked → New Entry Atlas 0:47". Each item has its
action, "Change…" (the editor with it filled in; the review steps aside and comes back) and "Not Now", which only
takes it out of the review: the chip and the calendar keep it (`UserDefaults` `reviewPutOff`, on this Mac). When one
click can't take it (`SplitAdvisor.blocker`: a repository without a project, details the workspace requires, or times
changed since), "Change…" is its main button and Return opens the editor, with the reason in the row: "Needs a task on
Beacon. Change… to add it.", "In repositories without a project. Change… to choose one." Keyboard: ↑↓ move, Return
takes, E opens the editor, ⌫ is Not Now; ⌘← and ⌘→ go to the day or week before or after, Esc closes.

What is answered keeps its place, done: "Split saved", "Added Atlas 14:00–14:47", "Saved in the editor" or "Not Now",
each with its own Undo (but what was saved in the editor) until the review closes (`Desktop.reviewed`, `undo(reviewed:)`).
A bar under the header counts "2 of 5 done", and once nothing waits the review says "All done" over the done rows.
"Take All (n)" at the top takes every item one click can take and leaves the rest, with one Undo for all of them in
the footer, which says what it left: "Saved 3 suggestions · 1 needs a project or details" (`Desktop.takeAll`); the
footer's Undo otherwise covers the last take. A day shows its other entries as quiet rows between the items, and a
strip of the day with the waiting items outlined in marigold, where a click on an outline selects its item; a week
shows the items day by day.

Ways in: the menu bar panel ("Today: 3 to review"), "✦ 3 to review" in the entry list's day headers and under each
day in the calendar, "Review Day (n)" / "Review Week (n)" in the calendar's header, and above the timer a card for
the last day with something waiting, up to three days back ("Yesterday: 3 to review", Not Now hides it for that
day). With "Remind me to review the day" on (under the notification setting, `DesktopSettings.notifyDayReview`), one
notification in the two hours after working hours end (`ReminderSchedule.endedWorkingDay`), when the day has items.

Windows has the same review (`Views/Split/DayReviewView.cs` over the window, `ReviewItem` and `SplitAdvisor.Review` in
`Model/DayReview.cs`, `DesktopService.ApplyAsync`/`Hold`), with the same items, actions and keyboard (Enter
takes, Backspace is Not now, Ctrl+← and Ctrl+→ go to the day or week before or after) and its words in sentence
case ("Take all (4)", "Add entry"). It has the web's rows too:

- Each row says what the time is now and what it becomes (`SplitAdvisor.Outcomes`): "✦ ~~Atlas 1:30~~ → 2 entries:
  ● Atlas 09:00–09:40 · ● Beacon 09:40–10:30", or "Nothing tracked → New entry ● Atlas 0:47".
- What was dealt with stays in its place, as "Split saved", "Added Atlas 14:00–14:47", "Not now" or "Saved in the
  editor", each with its own Undo (not for the editor's) until the review closes (`ReviewSession`, which lasts across
  Change…). The footer says the last take, or what Take all took, with one Undo for it.
- "2 of 5 done" with a thin bar under the header, and "All done" once nothing waits, with the done rows kept.
- When one click can't take an item, Change… is its main button and Enter opens the editor, with the reason in the
  row: "Needs a task on Beacon. Change… to add it." or "In repositories without a project. Change… to choose one."
  A split whose parts need a task (a part on a project that requires one) counts too.
- Take all says what it left: "Saved 3 suggestions · 1 needs a project or details".
- Clicking an outline in the day's strip selects its item. "Not now" is kept in its preferences file (`ReviewPutOff`), and Change… closes
the review for the editor and opens it again after. Its ways in: "Today: 3 to review" in the notification-area panel,
"✦ 3 to review" in the entry list's day headers and under each day in the calendar, "Review day (n)" / "Review week
(n)" in the calendar's header, the card above the timer for the last day with something waiting (up to three days
back), and with "Remind me to review the day" on (Settings › General › Parallel work), one toast in the two hours
after working hours end (`ReminderSchedule.EndedWorkingDay`). Notices
sit above dialogs there, so the Undo after a ✦ in the calendar can be clicked; the review says its own in its footer.

There is no "confirmed day" on the server: a reviewed day is just a day whose items were answered.

### Turning agent logs on

"Turn On" in the offer above the timer (or "Show What the Logs Found…" in Settings; on Windows "Turn on" and "Show what
the logs found…") opens three steps (`AgentLogsOnboarding`, on Windows `Views/Split/AgentLogsOnboarding.cs`), so the
point shows in the first minutes rather than when the first parallel entry stops:

1. What the last 7 days found, by the same rules as the suggestions (`ActivityFindings.elsewhere`; Windows
   `ActivityFindings.Elsewhere` in `windows/Spindle.Activity`): "4h 10m of tracked
   time went to other projects than the one on the timer", as a share of all tracked time, then the entries a split is
   offered for, the time in repositories without a project, and the agent time with no timer running. Split entries and entries without a
   project aren't counted.
2. The repositories agents worked in over the last 14 days, most time first (`ActivityFindings.repositories`), each
   with the project the links would guess filled in. Linking saves them as the person's own links.
3. "Review Last Week (n)", which opens the review of the last 7 days, with how the app offers suggestions from now
   on. Copy here and in the review says what is there (counts by kind, periods, thresholds), not slogans.

The menu bar shows ✦ beside the running timer when there is a strong suggestion; its panel shows the card for any. Every one-click take
(stop card, notification, menu bar, "Switch to Beacon") says "Split saved" or "Switched to Beacon" with Undo, in the
window's notice and, while the app isn't in front, in a notification; Undo deletes the entries it added and saves
the entry as it was (a timer stopped since stays stopped). The same words everywhere, Mac and Windows:

| Meaning | Words |
|---|---|
| Share one entry by percent | Split |
| Separate entries, cut where the work moved | Split at 10:42 / Split into 3 Entries |
| Running entry moved to another project | Switch at 10:42 (the notification: Switch to Beacon / Still on Atlas) |
| Work changed after a split | End Split at 11:05 / Add Comet from 11:20 |
| Untracked agent work before a timer, or with none | Start at 9:12? / Start from 9:12 |
| Look first | Review… |
| Not this time, until the split changes; in the review, not in this review | Not Now |
| Untracked agent time, in the review | Add Entry |
| Everything one click can take, in the review | Take All (4) |
| Never for this entry | Keep as Is |

The web uses the same words where it has the same thing, in sentence case (above).

"Keep as Is" dismisses all suggestions for that entry, on every device. In the editor, "Back to One Entry" turns
separate entries back into one before saving. Windows writes them in sentence case ("Keep as is"). Repositories
without a project are only linked inside the app.

## On the server and the web

Code: `src/Spindle.Domain/AgentActivity` and `src/Spindle.Api/AgentActivity`; on the Mac `ActivitySync`, `ActivityUploader` and the identity in the Kit; on
Windows `ActivitySync` (`windows/Spindle.Windows/Model`), `AgentActivityUploader` and `AgentSessionUpload`
(`windows/Spindle.Activity`) and `RepositoryResolver.OriginIdentity`.

- **Always**, whether or not activity syncs: the projects chosen for repositories (`AgentRepositoryLinks`, by identity,
  with their branch rules) and the entries kept as they are (`SplitSuggestionDismissals`) live with the account, so every device and the web agree.
  A change reaches the other devices through live sync (topic `agentActivity`).
- **Only with activity sync on** (`AppUser.AgentActivitySync`, an account setting, off by default): each Mac and
  Windows PC uploads its sessions to `PUT /api/me/agent-activity/sessions`, one row per session and device (`AgentSessions`), replaced
  whole: prompts as Unix milliseconds, runs as intervals, repository identity and name, branches, and titles only with
  `AgentActivityTitles`. Sessions outside git go without a repository: their prompts end the attention before them and
  the time between two prompts, so leaving them out would change the split. Turning sync off deletes every session;
  turning titles off deletes the titles; account deletion deletes all of it; the data export includes it.
- **Windows uploads like the Mac**, so people who only use Windows get the same evidence on the web and through the
  server's MCP. It reads the same logs with its own parser (`windows/Spindle.Activity`) and follows the Mac's rules for
  what is sent: repository identity, touches as timestamps and other events as runs, titles only when allowed, 90 days
  on first enable or after `AgentActivitySyncedSince` changes (also a new parser version), then only changed
  sessions in batches of 100, nothing older than 400 days. Both read all logs of a Claude Code session
  together when any of them changed, since the server replaces a session whole. It has its own random device id (`AgentActivityDevice.txt`
  in the app's data folder), so a session is a row per device. Differences: there is no file watcher, so it asks
  the account's setting and looks for logs written since the last look every 5 minutes (and soon after the timer
  stops, and at start), which costs a request and a directory listing when nothing changed. The switch is the same
  account setting (Settings › General › Parallel work, only with "Suggest splits from agent activity" on); turning
  it off elsewhere makes the PC forget what it sent. A repository without an origin is named by the hash of its Windows path
  (`C:\...`), as the Mac does with its own, so the same folder keeps its identity on that PC only.
- Activity is personal. Everything is under `/api/me/...`, or in a workspace only ever about the caller's own entries;
  admins and reports never see it.
- `GET /api/workspaces/{id}/time-entries/split-suggestions?from&to` suggests splits for the caller's entries on one
  project, by the Kit's rules ported to C#: attention built over a longer range from every device's prompts, then, as
  on the Mac, only the days the entry starts and ends on in the person's time zone. Links learned from the four weeks
  before, and projects named like a repository, are worked out there too. The Kit, the server and Windows run the cases
  in `tests/vectors/split-suggestions.json` (`SplitSuggestionVectorTests` in the Kit and in `tests/`, and
  `windows/Spindle.Activity.Tests`); change the rules on all three and add a case.
- Each suggestion comes with what it was built from, as the Kit's does: `strength` (strong or weak), the `start` and
  `end` of the time it divides, its `pieces`, the `open` intervals, the `prompts` with their targets, each part's
  `prompts` (touches) and the `fallback` project. Views draw these and work nothing out again.
- `GET /api/workspaces/{id}/agent-activity/untracked?from&to` gives the caller's untracked agent time, day by day in
  their time zone, by the server's `UntrackedActivity` with the Mac's rule that a stretch needs a prompt of its own.
  Folders outside git don't count. What covers time is every entry of the caller's, in any workspace, and the proposals
  waiting for their review in this one. Each stretch names this workspace's projects its attention went to (most
  first), the attention in repositories without one, and the split or stretches a new entry for it would take
  (`SplitSuggestions.Suggest` over the stretch, its first project keeping what the logs leave open). Nothing is stored.
- `GET /api/workspaces/{id}/agent-activity/now` names the projects of this workspace the caller's agents had attention in
  during the last 15 minutes, placed by the same links, the most recent first, for "Agents now" in the web's project
  picker. Repositories without a project and folders outside git don't count. Nothing is stored.

### On the web (with activity sync on)

Without activity sync the web shows none of this, as before. With it, the web follows the Mac in what it shows and
the words it uses, written in sentence case as the web's buttons are ("Keep as is", "Split into 3 entries"). The
running timer's offers ("Still on Atlas?", ending a split whose work changed, "Start at 9:12?" and the reminder) stay
on the desktop apps.

- **Chips** in the entry list show hours with each project's colour ("✦ 1:20 · 0:40", whole minutes that add up to
  the entry), or "✦ Split at 10:42" when the work moved; percentages are only in the editor. Resting on a chip (or
  focusing it) shows the evidence strip with each part's prompts and time. Calendar blocks keep their ✦.
- **Strong or weak:** the web has nothing that interrupts, so a weak suggestion is only quieter: a grey chip and ✦
  where a strong one is marigold, a plain banner, and "The logs back this only lightly: 54% of the time, few
  prompts."
- **The editor's banner** has the evidence strip, each part's percentage and time, "Choose project" for a repository
  without one, "The work moved to Beacon at 10:42", and Keep as is / Split, or Keep as is / Split 60 · 40 / Split at
  10:42 when the work moved. "Keep as is" is the account-wide dismissal; closing the editor is the web's "not now",
  and the chip stays. "Split at 10:42" turns the editor into the separate entries it will save ("Back to one entry"
  undoes it): saving makes the entry the last stretch, so a running timer keeps running, and adds the earlier ones
  with the description and tags, each billable as its project is.
- **Ghost blocks** in the calendar show untracked agent time as on the Mac: no fill, a hatched edge in the project's
  colour, "✦ Atlas · 14:00–15:30" ("Not tracked" when no repository has a project). Not on locked days. A click opens
  a new entry for the stretch with its project, its split, or its separate entries filled in; × dismisses it.
  Dismissals are kept in this browser, per person (`localStorage` `spindle.dismissedUntrackedTime.<user id>`, by day
  and start minute, the newest 200), as the Mac keeps its own: a dismissal only hides a hint, and a block's identity
  moves as more activity syncs, so it isn't worth a table and a sync topic.
- **The day's review** (`DayReview`, `features/agent-activity/day-review.tsx`) follows the Mac's: the same items in
  the order of the day (splits of finished entries on one project, and untracked agent time; the Mac's "End split at
  11:05" comes from the running timer, so the web has none), the day's other entries as quiet rows between them, a
  strip of the day, a week view, "Take all (n)" with one Undo, and the keys ↑↓, Enter, E, Delete or Backspace, and
  Esc. Each row says what the time is now and what it becomes ("● Atlas 1:30 → ● Atlas 0:40 · ● Beacon 0:50", or "2
  entries: …"), with the evidence strip for splits. The main button is what one click can do: "Split", "Split at
  10:42" or "Add entry"; when one click can't (a repository without a project, or details the workspace requires,
  e.g. "Needs a task on Beacon"), it is "Change…", which opens the editor with the suggestion filled in. An item dealt
  with stays in its place ("Split saved", "Not now") with its own Undo until the review closes, under a bar of how
  many are done, and the review says "All done" once none waits. "Not now" leaves the review only, in this browser
  (`spindle.reviewPutOff.<user id>`), as the Mac keeps it on the Mac; ghosts dismissed in the calendar are left out
  too. Saving is `takePlan` (`review.ts`), the Mac's `SplitApplier` rules: a split keeps the entry's own task and
  billable setting on its part, separate entries make the entry the last stretch and add the earlier ones.
  Ways in: "✦ 3 to review" in the entry list's and the calendar's day headers, "Review day (n)" / "Review week (n)"
  in the calendar's header, and a quiet card above the entries for the last day with something waiting, up to three
  days back ("Yesterday: 3 to review", "Not now" hides it for that day in this browser).
- **The calendar's ✦** is a button of its own. Only while it is pointed at, or reached with the keyboard, the
  suggestion is drawn over the block as a proposal: the block stays as it is underneath, each proposed part a
  translucent hatched band in its project's colour with dashed dividers ("Split at": a dashed cut with its time;
  a split by percent: columns as wide as each share), labelled "● Atlas 0:50" (the time alone where narrow; none on a
  short block), fainter when weak, and the block's own text on a soft backing. Under the block one line says what a
  click does, naming the parts: "Split Atlas 0:50 · Beacon 1:10 · click ✦ to apply", "Split at 10:50 · click ✦ to
  apply". A click takes it, saying "Split saved" with Undo; a running entry, or one a click can't take, opens in the
  editor ("click ✦ to open").
- **"Agents now"** at the top of the project picker lists the projects agents worked in during the last 15 minutes
  (`/agent-activity/now`), asked again each minute while the picker is open.

## Agents asking about their time

Agents use the server's MCP ([connect-agents.md](connect-agents.md)). It
reads the evidence above from the synced activity, so the Mac and Windows apps only sync it (and connect agents, and
show their proposals for review); they have no MCP server of their own.

The apps' own part is the review of what agents proposed: the Mac in `Mac/Agents/ProposalReview.swift` and the
Windows app in `Views/Agents/ProposalReview.cs`, which open from a banner above the timer, a chip on a changed entry,
or a dashed block on the calendar. Proposals, and what stands in their way, are the server's
(`/agent-proposals`); the apps read and answer them there. Agents can propose changes only to entries they listed, as
they can only describe those.

## Checking it

- Kit: `swift test --package-path apple/SpindleKit` (parser parity, ledger, timeline windowing, suggestions, the
  shared vectors, repository identities, uploads). With `SPINDLE_API_URL` set, `LiveAgentActivityTests` sends links,
  dismissals and an upload to a real API.
- Server: `dotnet test` (`SplitSuggestionVectorTests`, `AgentActivityTests`).
- Mac: `xcodebuild test -project apple/Spindle.xcodeproj -scheme SpindleMac CODE_SIGNING_ALLOWED=NO`.
- Windows: `dotnet run --project windows/Spindle.Activity.Tests` (the shared vectors: suggestions, `split-drift.json`,
  `untracked-activity.json`) and `dotnet run --project windows/Spindle.Windows.Tests` (the views, with
  `SPINDLE_UI_PREVIEW` to draw them).
- Web: `npm test` in `src/web` (`review.test.ts` and the suggestion views).
- `scripts/check.sh all` runs all of them.
- Snapshots (`apple/Mac/Snapshots/SnapshotRun.swift`) draw the app against a local API with synthetic logs
  (`SPINDLE_SNAPSHOT_AGENT_LOGS`); `SPINDLE_SNAPSHOT_EDIT=suggested:<description>` and
  `SPINDLE_SNAPSHOT_OFFER=<description>` show suggestions taken or offered, `SPINDLE_SNAPSHOT_SPLIT_PREVIEW=<description>`
  a suggestion drawn on its calendar block, `SPINDLE_SNAPSHOT_REVIEW` (with `SPINDLE_SNAPSHOT_REVIEW_TAKE=some`, `all`,
  `allDone` or `allThenUndo`) the day's review, and `SPINDLE_SNAPSHOT_WINDOW=branchRules` (with
  `SPINDLE_SNAPSHOT_REPOSITORY` and `SPINDLE_SNAPSHOT_BRANCH_RULES`) the branch rules editor. Never commit real logs.
