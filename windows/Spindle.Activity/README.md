# Local agent activity

Dependency-free .NET 10 library for the Windows app. The caller owns the agent-log consent setting: construct/read the service only after opting in, call `Clear()` and discard the displayed snapshot when opting out. Nothing is saved to a disk ledger. Nothing is uploaded either, unless the person turned on "Sync agent activity to your account": then `AgentActivityUploader` and `AgentSessionUpload` (below) produce what the app sends.

```csharp
var activity = new AgentActivityService();
var snapshot = await activity.ReadAsync(dayStart, dayEnd, cancellationToken);
var links = new RepositoryLinks(chosen, learned);
var suggestion = SplitSuggestion.Suggest(entryStart.ToUnixTimeMilliseconds(),
    entryEnd.ToUnixTimeMilliseconds(), snapshot.Attention, fallbackProjectId,
    segment => links.Place(segment, projects));
```

For suggestions, load the full local days the entry starts and ends on. The next prompt outside an entry may establish that a gap belonged to the same project. When an entry spans more than two days, use attention only from its start and end days, matching the Mac and API. Day boundaries belong to the caller's time zone, including daylight saving changes.

Sources are `.claude/projects/**/*.jsonl`, `.codex/sessions/**/rollout-*.jsonl` with Codex's `session_index.jsonl`, and `.copilot/session-state/*/events.jsonl` under the supplied home folder (default: the user's profile), as `AgentLogFormat.All` lists them, for the sources in `Sources` (the app's per-agent preferences); the parser reads each source's lines its own way, by the same cases as the Kit (`tests/vectors/agent-logs.json`), which also say which paths are logs. Tests inject a temporary home folder. Only timestamps, local folders, branches, source IDs, and titles up to 80 Unicode characters survive decoding. Raw prompts and model answers are discarded.

The JSONL reader caps each record at 32 MiB and resumes after oversized records. It defers unfinished final records until the next refresh. Incremental in-memory metadata caches retain at most 256 logs and 100,000 events, reset on truncation/rewrites, and are invalidated by `Clear()`. Files outside this cache can be read again; initial scans of large histories can be slow. IO failures and oversized records appear in `snapshot.Errors`. The caller should do reads in the background and report partial results honestly.

Claude user tool results, sidechain/meta/system records, Codex subagent task starts, Copilot's autopilot, injected, subagent and slash-command messages, and turns exclusively using Spindle's MCP tools produce no attention touches. Agent runs cluster events separated by no more than three minutes and can overlap; attention never double-counts parallel sessions. Worktrees resolve through `.git`/`commondir`; origins lose credentials, transport details and `.git` before becoming identities. Folders outside git still interrupt attention, but never become suggestion targets.

`SplitSuggestion.cs` ports the current API algorithm without backend dependencies. Keep it aligned with `src/Spindle.Domain/AgentActivity/SplitSuggestion.cs` (the same file but for its namespace) and the Swift implementation. Branch rules come with the links (`new RepositoryLinks(chosen, learned, rules)`); pass `segment => links.Place(segment, projects)` rather than `ProjectFor` so they apply. `ReadAsync` gives each touch its session's branches, so attention segments carry them. Both suggestion and repository-link learning use the existing `tests/vectors/split-suggestions.json` golden cases. `SplitDrift.cs` (a running split whose work changed after it was set) is the Kit's `SplitDrift.swift`, with no server copy; both run `tests/vectors/split-drift.json`.

## Uploading to the account

`AgentActivityService.ReadSessionsAsync(from, changedSince)` returns whole sessions with events since `from`, and their repositories; with `changedSince` only logs written since then are parsed again, together with every log of the same Claude Code session (`project/session.jsonl` and `project/session/subagents/*.jsonl` share a session ID). `AgentSessionUpload.Create` turns one into what the server takes, as the Mac's `AgentSessionUpload`: touches as Unix milliseconds, all events as runs (less than 3 minutes apart), repository as identity and name (no path), branches, and the title only when allowed. `AgentActivityUploader` keeps what it sent per session in a small file and sends only changed sessions, 100 at a time, oldest first. After sync is turned on again, titles are allowed or `AgentLogParser.Version` changes, it sends everything again. Raise `AgentLogParser.Version` whenever the parser reads logs differently. Sessions with IDs the server would not take (over 128 units) are skipped.

Repository identities are the Mac's and the server's: `RepositoryResolver.OriginIdentity` and `PathIdentity` (`origin:` and the lowercased host and path without scheme, user, password, port or `.git`; `path:` and the first 32 hex digits of the SHA-256 of the path when there is no origin or it is a local path or `file:` URL). Change them on all sides at once, or devices stop merging.

Run the activity checks from the repository root:

```sh
dotnet run --project windows/Spindle.Activity.Tests
```
