# Principles

What Spindle is for, the rules it follows, and what it deliberately doesn't do. Read it before proposing a feature.

## In one sentence

A high-quality, open-source time tracker with great UX, built for people who run several AI-agent workstreams at once. Splitting time between projects is easy, by hand or from a suggestion the app works out locally from agent logs.

## Who it is for

- **Developers and freelancers who work through AI agents.** They want to know, at least roughly, how much of their time went into each project or client, even when they run several agents at once.
- **Small teams and co-founders.** They track time voluntarily to see who put how much time into which part of the work, billable or internal.
- **Agencies and software companies.** They want to know what their developers spend time on, across clients, internal work and work that cannot be billed directly, without surveilling them. Each developer tracks their own time; nothing is recorded behind their back.

We are the first users. Spindle started as our own tool: three co-founders tracking our time, some billable to clients and some internal. We use every feature ourselves first, but we build it for all three groups.

## The problem

AI agents made time tracking harder. You start the timer on the project you are focused on, but while agents work there, you often start smaller tasks in one or two other projects: a small feature, a code review, a refactoring. Over a day, the time and attention you gave those other projects adds up, and it is recorded nowhere.

If you really switched from one project to another, you would stop the timer and start a new one, and some tools let you split an entry at a point in time. That doesn't fit here: the work runs in parallel, and you don't know the exact moments your attention moved between projects. What matters is the total, so it is better to say what share of the time went to each project, for example 70% to A and 30% to B. The moments themselves are still roughly recorded, but the share is what counts. Tools that count each agent session separately don't help either, because parallel sessions add up and the same time is counted twice.

For people who work this way, more and more of their time goes into supervising and planning agents, not writing code. What matters is how much of *their* time went into what, even roughly. A "my time vs the agent's time" metric is not a goal.

## What Spindle is

- **A classic time tracker first.** You start the timer on the project you are focused on, and that is your statement of what you are working on. Projects, tasks, billable and internal work, reports, and native apps for the Mac (with a menu bar), Windows, iPhone and Android.
- **Parallel work as a first-class feature.** One entry can split its time between several projects (2–6 parts, whole percentages). Splits are supported everywhere: the timer, calendar, editor and reports, and each part is counted on its own project.
- **You decide, the logs suggest.** The Mac and Windows apps read Claude Code, Codex and GitHub Copilot CLI logs locally and, when the evidence is clear, suggest a split, or an entry for agent work no timer covered. Nothing is saved until you confirm it, and your own split always wins over what the logs show. Without logs, Spindle is still a complete time tracker.
- **No setup.** Reading logs works on its own, with no hooks or configuration to install. The Mac app is distributed with Developer ID and is not sandboxed, so it can read `~/.claude` and `~/.codex`; the Windows app reads the same folders in the user's profile.
- **Local and private by default.** Logs stay on the computer. Only timestamps, folders, branches and short session titles are kept; prompt text and answers are discarded. Only the entries the user has confirmed are synced to the server, along with the projects they chose for their repositories (and their branch rules) and the suggestions they turned down.
- **Your own agent can ask.** Through Spindle's MCP server, Claude Code, Codex, Claude and other MCP clients can read the person's time and reports, describe entries from the synced activity and the repository's git history, and start or stop the timer, when the person asks them to. It writes descriptions directly (only empty ones, unless told otherwise) and never changes times, projects or splits itself: it proposes new entries and changes, which wait in Spindle until the person adds, edits or discards them.
- **Sync only if you ask for it.** People who don't need absolute privacy can turn on activity sync for their account, so the web can show the same suggestions, untracked agent time and day's review, and their own agent can see what they worked on. Then the Mac and Windows apps send when prompts were sent, when agents were busy, and each repository's name, git origin and branches; session titles only with a second switch. Never prompts, answers or folder paths. It belongs to the person: nobody else sees it, not admins, not reports, and turning it off deletes it.

## Principles

- **Don't claim what we can't know.** Logs do not see calls, meetings, reading or thinking outside the agent. So the app does not reconstruct a whole day from logs and does not guess where attention "probably" was.
- **The timer is the user's word, logs only help divide it.** Time with no evidence belongs to the project on the timer.
- **Not surveillance.** No tracking of apps, windows or screenshots. That is what makes the tool acceptable to developers in a team.
- **Honest about parallel work.** One hour of your time is one hour, not the sum of agent sessions.

## Log analysis rules

- A touch is a prompt the user sent: in Claude Code a real user message, in Codex `task_started` in the main session. Subagents produce no touches; their work belongs to the main session's repository. A prompt that only asks Spindle's own tools something, such as "describe today's entries", is not a touch.
- A repository is resolved to its git root, and worktrees are merged with their main repository. Folders outside git do not count towards suggestions.
- A repository's work goes to the project the person linked it to. Branch rules can send some of its branches to another project, e.g. a client's, when one repository serves several.
- Inside an entry:
  - Time between two consecutive prompts in the same project belongs to that project.
  - When work switches between projects, the time after a prompt goes to that prompt's project up to a limit (15 min).
  - Time without evidence goes to the project on the timer.
- If work switched once or twice, the app offers to split it into separate entries at the switch. If it went back and forth, it offers a percentage split.
- Rejected as too much guessing: planning time before a prompt, agent completion meaning review, prompt length as a measure of effort.

## How suggestions reach the user

Splitting works in the background. The user never has to look at agent activity in the calendar; that view is optional and only shows the evidence.

- **One consent.** Using agent logs is a single setting, separate from showing activity in the calendar. When the app first finds `~/.claude` or `~/.codex`, it asks once. Turning it off forgets everything read from the logs. Hiding the calendar lanes does not. Syncing activity to the account is a second, separate opt-in, off by default.
- **The day's review, and a few strong interruptions.** Everything waits quietly in one review of the day (or the week): splits with their evidence, work that changed after a split, and agent time nobody tracked, each to take, change or put off, or all at once with one Undo. It is a click away in the menu bar (on Windows, the notification area) and each day's header, and the next morning a card above the timer says what is waiting. With activity sync on, the web has the same review. Only clear cases interrupt, each at most once per entry:
  - **While the timer runs, only for a clear switch.** If the timer is on project A and for 15 minutes or more every prompt went to project B, a notification asks "Still on A?" and offers to switch the timer to B from the moment the work moved. Back-and-forth work gets no notification.
  - **While a split runs, only when its work changed.** A split you set is never judged against the prompts. But if one of its projects has gone quiet for 30 minutes while the others went on, or agents have worked in another project for 15, a notification offers once to end the split there and carry on in a new entry.
  - **When the timer stops**, only for a split the logs back firmly. A card offers it in one click (Split, Review…, Keep as Is), with Undo, or a notification when the app is in the background. When the card goes, nothing is lost: it is in the day's review.
  - Entries keep a quiet chip in the list and calendar too, and pointing at a calendar block's ✦ draws the split on it. With activity sync on, the web shows the same.
- **Turning logs on shows the point.** It reads the last week at once and says what it found in the time already tracked, honestly computed by the same rules, then asks which project each active repository is for and opens that week's review.
- **Time nobody tracked.** Agent work with no entry is offered only as the stretches the prompts show: "Start at 9:12?" just after a timer starts, the reminder to track naming the project agents are in, a block in the calendar, and an item in the day's review. The person adds each with a click.
- **The menu bar** (on Windows, the notification area) marks the running timer with ✦ when there is a suggestion, and offers it there, so the feature works with the main window closed.
- **Adding time manually** offers the same suggestion for the typed time.
- **Rules that keep it quiet:** nothing is saved without a click; "Keep as Is" stops all suggestions for that entry; repositories without a project are only linked inside the app, never from a notification; notifications have their own setting and respect Focus.

## Not now

- Reconstructing a whole day from logs. Logs don't see calls, meetings or reading, so the apps offer only the stretches agents were working in ("Time nobody tracked" above), never a guess at the rest of the day. The person's own agent can use the same stretches to propose entries when asked to fill in a day; the person confirms each, and tells about the rest.
- Invoicing and billing clients. The model stays ready for it (billable/internal work, rates, agreements), but it is not the focus.
- Tracking apps and windows. Maybe much later, if ever.
- A "human vs agent time" metric.

## Open source and Spindle Cloud

Open source is a condition of trust here: Spindle asks for access to agent logs, so anyone can read the code that
reads them and the code that divides time and money. The code is under AGPL-3.0.

- **No feature is locked artificially.** A self-hosted server has everything needed to track and divide time honestly:
  reading agent logs, splits and suggestions, the MCP for agents, reports, exports, teams and the API.
- **Spindle Cloud adds low friction.** Everything works in a few clicks with nothing to configure. Self-hosting gives
  the same app to people who are willing to run it.
- **Only Spindle Cloud has what depends on our infrastructure or our credentials, or costs us per user**, such as iPhone
  push notifications and, later, calendar and issue tracker integrations. Their code is open too, and a self-hoster can
  turn them on with their own credentials.

What comes next is in [roadmap.md](roadmap.md).
