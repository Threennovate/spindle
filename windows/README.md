# Spindle for Windows

A native desktop client for the same Spindle account and API used by the Mac app. Built with .NET 10 and Avalonia.
The UI can also run on macOS for development; Windows provides credential protection, startup, the system tray and the
system-wide shortcut.

## Run and publish

Install the .NET SDK selected by the repository's `global.json`, then:

```sh
dotnet run --project windows/Spindle.Windows
```

On Windows, choose Spindle Cloud or enter your self-hosted server address, and sign in or create an account. Refresh
tokens are kept in Windows Credential Manager under a key scoped to the server. Non-secret preferences and personal
tracking data live under `%LOCALAPPDATA%/Spindle`. On macOS the development preview keeps credentials in memory only.

Publish a self-contained application (no .NET installation required on the destination PC):

```powershell
./windows/scripts/publish.ps1 -Runtime win-x64
./windows/scripts/publish.ps1 -Runtime win-arm64
```

Extract the entire output folder and run `Spindle.exe`; keep its supporting files beside it. These folders don't update themselves. The Windows workflow builds,
checks native platform services, and uploads both bundles.

Released versions are installed from `/download/windows` and update themselves with Velopack; see
[docs/releasing.md](../docs/releasing.md#windows) for releasing one.

## Design

The app follows Windows 11 (Fluent 2) and stays recognisably Spindle: a navigation pane on Mica with the sections of
the Mac's sidebar, each section on a raised layer, Segoe UI Variable, Fluent UI System Icons, and marigold for the
main action with bronze for text in the brand colour, in light and dark following Windows. All colours, sizes and
radii are tokens in `Spindle.Windows/Theme/Tokens.axaml`; views pick them through style classes in
`Theme/Controls.axaml` and never name a colour. Project colours are data. `scripts/fluent-icons.sh` regenerates
`Theme/Icons.axaml`.

## What it does

- Native login, registration, password recovery, workspace switching and secure session renewal; self-hosted HTTPS servers and localhost.
  "Continue with Google" and "Continue with Apple" when the server offers them: in the default browser, back to a
  listener on 127.0.0.1 with PKCE (RFC 8252, `Spindle.Core/ExternalSignIn.cs`). An account made that way has no
  password, and Settings offers an email link to set one.
- Timer bar as on the Mac: description with earlier entries as suggestions, searchable project picker grouped by
  client with tasks and project creation, tag picker with tag creation, billable, the running clock (click it to
  correct the start: 930, 9:30, -15), start and stop, and manual times (day, start, end, duration).
- Entries a week at a time, grouped by day with totals, project colours, split labels and meters, tags, billable and
  lock marks, Continue under the pointer, and a context menu (Continue, Edit, Split, Duplicate, Delete).
- Entry editor over the window in the account's time zone: day, start, end and duration fields that read what people
  type, daylight-saving checks, required details, a draggable split editor (2–6 parts, shares or durations), a
  description per project of a split (its reports and timesheet links show only its own), and the split agent activity
  suggests, by percent or as separate entries. Stopping asks for required details first.
- Calendar: day and week on a 24-hour timeline in project colours, the current time, weekends and locked days,
  drag to add, move and resize (15-minute steps), agent activity (Focus, and a lane each for Claude Code, Codex and Copilot), and agent time
  nobody tracked as hatched blocks that open a prefilled entry. A block's ✦ draws the split agents suggest on the
  block when hovered or focused, and takes it with a click, with Undo.
- Reports: summary, detailed and weekly; periods with arrows and a custom range; member, project, client and tag
  filters; billable, description and rounding; totals tiles, a time chart, a donut breakdown with sub-groups, sortable
  detailed rows with paging, a weekly grid, and CSV/Excel/PDF exports produced by the backend. Split time and
  monetary totals are authoritative server values.
- Projects (status, search and client filters, estimates, editor with colours and rates, tasks), clients and tags.
- Settings in Windows 11 cards: start with Windows, notification area, configurable global shortcuts, time away,
  reminders, parallel work, the sync queue, Pomodoro, connecting agents to the server's MCP, profile, dates and times, password and devices.
- Desktop: quick entry on a global shortcut (Ctrl+Alt+Space by default) that reads lines as the Mac and web do
  (`tests/vectors/entry-sentence.json`); a notification-area panel with the running timer, recent entries and quick
  entry; time away from a running timer; reminders in working hours, which name the agents' untracked work ("Start
  from 9:12"); the Pomodoro; "Start at 9:12?" just after a timer starts; split offers after a stop (strong ones only)
  and switch offers while running; notifications drawn by the app.
- The day's review, as on the Mac: every split, change after a split and stretch of untracked agent time of a day or
  week in one place, to take, change or put off with the keyboard, or all at once with one Undo. Each row says what
  the time becomes; what was dealt with stays in its place with its own Undo, under "2 of 5 done". It opens from the
  notification-area panel, the day headers, the calendar, a card the next morning and an optional toast after working
  hours. Turning agent logs on shows what the last week's logs found and links the repositories agents worked in.
- System tray with elapsed timer, closing to tray, opt-in launch at login and single-instance activation.
- Durable offline time-entry cache/outbox, including cold launch with an existing protected session. Queued changes
  remain visible under Settings; failed changes can be retried or discarded. A stable client GUID prevents duplicate
  creates after an ambiguous network response. Edits, stops and deletes carry the version they were made on
  (`If-Match`), so the server refuses them when another device changed the entry since; that becomes a failed change
  to retry over theirs or discard. Changes queued by older builds compare once before they are sent.
- Live SignalR changes with polling fallback and refresh when the window activates.
- Opt-in local Claude Code/Codex activity reading with bounded incremental metadata, repository/worktree identity,
  shared split rules, explicit repository→project links with branch rules ("Different projects by branch…", from the
  suggestion banner, the onboarding and Settings), "Agents now" at the top of the project picker, and confirmed
  percentage suggestions.
- Agents' proposals wait in a review as on the Mac: a banner above every section, a chip on a changed entry and dashed
  blocks on the calendar open it; each proposal is added, edited or discarded, beside the day's entries and what
  stands in its way. The proposals and what stands in their way are the server's.

One hour remains one hour: local allocation uses the same largest-remainder rule as `TimeShare.Divide` and every
report is computed by the backend. Activity changes nothing without a click: what agents propose waits for the person.

## Agent access

Agents use the server's MCP at `<server>/mcp` ([docs/connect-agents.md](../docs/connect-agents.md)). **Settings →
Agents → Connect** runs Claude Code's or Codex's own commands for it and starts signing in in the browser; the commands
are shown to copy too, with the address for Claude and ChatGPT connectors and the signed-in agents
(`Views/Settings/AgentSettings.cs`, `Desktop/AgentConnector.cs`). `claude` and `codex` are found on the PATH (the
process's, then the user's and machine's as they are now, then their installers' usual folders) as `.exe` or npm's
`.cmd`, and run without a console window, except `claude mcp login`, which needs a terminal and gets a console window
that stays open if it fails. Whether an agent has the server is read from `~/.claude.json` and Codex's `config.toml`
(`Desktop/AgentConnections.cs`).

The server's MCP is the only one: the app has no MCP server of its own. Agents never add time or change times,
projects or splits themselves: `propose_entries` and `propose_changes` leave proposals on the server.

`Desktop/ProposalInbox.cs` reads the proposals waiting on the server. It reads the workspace's on each refresh
of the window (sign-in, workspace switch, sync messages, polling), with what stands in their way in the server's words,
and answers there: Add applies, a save in the editor resolves as saved, Discard resolves as discarded, and Undo in the
review takes back the entry and reopens the proposal.

## Verification

```sh
dotnet build windows/Spindle.Windows -c Release
dotnet run --project windows/Spindle.Core.Tests
dotnet run --project windows/Spindle.Activity.Tests
dotnet run --project windows/Spindle.Windows.Tests
dotnet run --project windows/scripts/PlatformSmoke
```

`scripts/check.sh windows` runs them all. To look at every screen without a Windows PC, give the UI tests a folder;
they draw each screen and state in light (`name.png`) and dark (`name-dark.png`):

```sh
SPINDLE_UI_PREVIEW=/tmp/spindle-windows dotnet run --project windows/Spindle.Windows.Tests
```

The calendar's activity lane and the split suggestions use the Mac's synthetic agent logs
(`apple/Mac/Snapshots/Fixtures/agent-logs`); the test creates their repositories under `/tmp/spindle-activity-repos`.
Off Windows the previews use Inter for Segoe UI Variable and a solid background for Mica.

`Spindle.LiveChecks` runs real API flows against an explicitly selected **throwaway localhost API/database**; read its
README before running. Never point it at a personal account. UI checks instantiate the real views with headless
Avalonia and a deterministic HTTP fixture. Native Windows credential, startup, shortcut, tray, Mica, title bar and
notification behavior need a Windows run; a cross-build or Mac screenshot does not prove those integrations.
