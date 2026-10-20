# Spindle for Mac and iPhone

The native Mac and iPhone apps, in Swift and SwiftUI. They talk to the same API as the web app.

| Folder | What's in it |
| --- | --- |
| `Mac/` | The Mac app: sign-in, the main window and, in debug builds, the snapshot runner |
| `MacTests/` | The Mac app's tests (Swift Testing) |
| `iOS/` | The iPhone app (iOS 17 and later), and in debug builds the options for automated runs in the simulator |
| `iOSTests/` | The iPhone app's tests |
| `Shared/` | What both apps compile in: the app model, the brand colors and logo, and a few views |
| `LiveActivity/` | The running timer's Live Activity and its stop intent, compiled into both the iPhone app and its widget extension |
| `Widgets/` | The iPhone app's widget extension, which draws the Live Activity on the Lock Screen and in the Dynamic Island |
| `SpindleKit/` | A Swift package both apps build on: the generated API client, signing in, the offline cache and outbox, live sync, and the rules for showing time |
| `Tools/` | Pins the OpenAPI generator that `scripts/generate-api.sh` runs |
| `scripts/` | Generating the API client, making and publishing releases (`release-mac.sh`, `publish-mac.sh`, `release-ios.sh`), App Store screenshots and profiling the Mac app |
| `ReleaseNotes/` | What's new in a version (`X.Y.Z.md`, added with the release), shown in the update window |
| `project.yml` | The Xcode project, described for [XcodeGen](https://github.com/yonaskolb/XcodeGen) |

## Setting up

You need Xcode 26 or later and XcodeGen (`brew install xcodegen`).

`Spindle.xcodeproj` is generated from `project.yml` and committed, so you can open it straight away. After you change `project.yml` or add files, run `xcodegen` in this folder to update it.

## The API client

`SpindleKit/Sources/SpindleAPI/Generated` comes from `openapi/spindle.json`, which the API writes when it builds. After you change the API, run:

```sh
apple/scripts/generate-api.sh
```

This is the Swift counterpart of `npm run generate:api` in the web app.

## Running and testing

In Xcode, run the **SpindleMac** scheme. A debug build talks to the API on this Mac, `http://localhost:5258`, and its links to the web app go to `http://localhost:5173`. A release build talks to `https://app.spindletrack.com`. That address is Spindle Cloud for the build (`SPINDLE_SERVER_URL` in `project.yml`). On the sign-in screen, **Change server** lets people use their own server instead: the app checks that `/health` answers "Healthy" there, keeps the choice for the next launch, and offers passkeys only on Spindle Cloud, the one domain the app is associated with. Updates always come from Spindle Cloud. If Spindle Cloud ever moves to another https address, a new build moves the sign-in and this Mac's copy of the account to it, from the address it saw last or from one listed in `SPINDLE_FORMER_SERVER_URLS`, so the move signs nobody out, even someone who skipped versions. The iPhone app offers the same on its sign-in screen.

From the command line:

```sh
cd apple
xcodebuild -project Spindle.xcodeproj -scheme SpindleMac -derivedDataPath build.noindex/DerivedData test
(cd SpindleKit && swift test)
```

Command-line builds go to `build.noindex`. Spotlight skips a folder with that ending, so the builds don't show up as more copies of Spindle next to the one in Applications.

The kit's live tests register an account on a real API. They run only when `SPINDLE_API_URL` is set, e.g. `SPINDLE_API_URL=http://localhost:5258 swift test`.

## The iPhone app

In Xcode, run the **SpindleiOS** scheme on a simulator. Like the Mac's debug build, it talks to the API on this Mac, which the simulator reaches as `localhost:5258`. A release build talks to `https://app.spindletrack.com`.

From the command line:

```sh
xcodebuild -project Spindle.xcodeproj -scheme SpindleiOS -derivedDataPath build.noindex/DerivedData-ios \
  -destination "platform=iOS Simulator,name=iPhone 17 Pro" test
```

While a timer runs, the iPhone shows it as a Live Activity, with a button that stops it. Siri, Spotlight and Shortcuts can start and stop the timer ("Start a timer in Spindle"; the home screen and Siri call the app Spindle, while the code and bundle keep the name Spindle). The system runs those intents in the app, which has the sign-in; the widget extension only draws. With the Pomodoro on, a notification tells of each focus interval's end, with a Stop Timer button.

The tests of those intents start and stop a timer on a new account on a real API, and run only when `SPINDLE_API_URL` is set (`TEST_RUNNER_SPINDLE_API_URL=http://localhost:5258` in front of `xcodebuild test`). The UI tests (`iOSUITests/`) drive the app in the simulator against the API on this Mac, which must be running. Each run signs up a new account through the API, so it leaves other accounts' time alone.

A debug build reads a few `SPINDLE_AUTOMATION_*` environment variables, listed in `iOS/Automation/LaunchOptions.swift`. They can sign it in with an account and open a tab, so a script can take screenshots without anyone tapping. Such a run keeps its sign-in in memory, away from the Keychain. With `simctl`, each variable gets the `SIMCTL_CHILD_` prefix:

```sh
xcrun simctl install booted build.noindex/DerivedData-ios/Build/Products/Debug-iphonesimulator/Spindle.app
SIMCTL_CHILD_SPINDLE_AUTOMATION_EMAIL=ana@example.com SIMCTL_CHILD_SPINDLE_AUTOMATION_PASSWORD=… \
  SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=settings xcrun simctl launch booted com.spindletrack.iphone
xcrun simctl io booted screenshot settings.png
```

To put the app on your own iPhone, connect it with a cable, turn on Developer Mode, and install a Release build, which talks to the production server. The iPhone's iOS must not be newer than what your Xcode supports. Xcode registers the iPhone on the team the first time:

```sh
xcodebuild build -project Spindle.xcodeproj -scheme SpindleiOS -configuration Release \
  -derivedDataPath build.noindex/DerivedData-ios-device -destination "platform=iOS,id=<UDID>" \
  -allowProvisioningUpdates -allowProvisioningDeviceRegistration
xcrun devicectl device install app --device <UDID> \
  build.noindex/DerivedData-ios-device/Build/Products/Release-iphoneos/Spindle.app
```

`xcrun devicectl list devices` shows the UDID. For other people's iPhones, see [docs/releasing.md](../docs/releasing.md#iphone).

## Offline and live sync

The server's database is the one that counts; the Mac keeps a cache so the timer works without a connection.

- **Cache.** For each account, a small SQLite database in `~/Library/Application Support/Spindle/` keeps the account, this week's entries, the running timer and the workspace's projects, tags and settings. The app opens from it at once, then catches up with the server. Signing out deletes it.
- **Outbox.** Every change to an entry (start, stop, edit, delete) goes into an outbox in that database and is sent in order. Online it goes at once, and the server's answer (including a refusal, e.g. a locked day) reaches whoever made the change. Offline it waits and is sent when the server can be reached again. New entries get their ID on the Mac, so sending one twice is harmless. What the app shows is the server's data with the waiting changes applied on top.
- **Conflicts.** A change to an existing entry carries the entry's version as the device last had it (`If-Match`). If another device changed the entry since, the server refuses it, and the change stays, still shown, as a conflict: the sync status in the toolbar (Mac) or on the timer (iPhone) says how many changes wait, and each shows both versions with Keep mine and Keep theirs. Changes to other entries go on meanwhile. Stopping a timer that was only renamed elsewhere goes through anyway, since it overwrites nothing of theirs. Conflicts aren't kept on disk: after a restart the change is sent again and finds the conflict again.
- **Live sync.** The app listens on the API's SignalR hub (`/api/hubs/sync`) and loads again what another device or a teammate changed. API calls carry the `X-Sync-Connection` header, so the Mac isn't told about its own changes.

## Passkeys

Mac and iPhone use AuthenticationServices to create passkeys and sign in with existing ones, including those created on the web. Mac Account settings and iPhone Settings → Security → Passkeys list, add and remove the same account's keys. Registration asks for the current password, or, for an account made with Google or Apple, a sign-in on this device in the last ten minutes; signing in uses Touch ID, Face ID or the system's device authentication. Refresh tokens stay in Keychain, as with password sign-in.

"Continue with Google" and "Continue with Apple" show on the sign-in and create-account screens when the server lists them in `/api/server`, on Spindle Cloud and on people's own servers alike. They open the server's sign-in in `ASWebAuthenticationSession`, which comes back to `spindle://auth/callback` with a one-time code that only this app's PKCE verifier can redeem (see `docs/architecture.md`). No entitlement or URL type is needed for it.

Both apps carry `com.apple.developer.associated-domains` with `webcredentials:app.spindletrack.com`. The API serves `/.well-known/apple-app-site-association` there, containing team `9ARDA688G5` and the Mac and iPhone bundle IDs. Deploy the API containing that endpoint before testing native passkeys; it must return JSON directly over HTTPS without a redirect. Apple's association service may cache the file.

Enable the Associated Domains capability for both app IDs and include it in the provisioning profiles used to sign the apps, including the Developer ID profile for the Mac. A build with signing disabled checks compilation but cannot verify the association or system authentication. Existing release automation still performs signing; it needs profiles that include this entitlement.

For a local Mac release, set `SPINDLE_MAC_PROVISIONING_PROFILE` to the downloaded Developer ID `.provisionprofile` file. The release script validates its app ID, capability and expiry, installs it for Xcode, and uses it to archive and export. GitHub releases need the same file as the `MAC_PROVISIONING_PROFILE_BASE64` secret in `mac-release`. Credential-free CI tests and dry runs omit the Associated Domains entitlement.

Native passkeys require the associated HTTPS domain. Localhost Debug builds keep password sign-in and can list/remove keys, but do not offer native passkey sign-in or creation. To test on the production domain, use a signed Release build (or override both `SPINDLE_SERVER_URL` and `SPINDLE_WEB_URL` to the production URL in a properly signed development build). A real signed app with a working domain association is required to verify Touch ID/Face ID end to end.

## Signing

- Everything is signed for the team Threennovate d.o.o. (9ARDA688G5). The Apple ID's free Personal Team can't make Ad Hoc or Developer ID builds.
- **Debug** builds are signed with your Apple Development certificate. Because the signature stays the same from one build to the next, the Keychain keeps trusting the app with its sign-in instead of asking each time.
- **Mac Release** builds are signed with a Developer ID Application certificate, for distribution outside the App Store. iOS distribution uses Apple Distribution with Ad Hoc or App Store profiles, depending on the export method.

## Releases and updates

People download the Mac app from the server as a notarized disk image. It updates itself with [Sparkle](https://sparkle-project.org) from the feed on its server (`/downloads/spindle-mac/appcast.xml`). [docs/releasing.md](../docs/releasing.md#mac) covers the one-time setup (certificate, notarization, update key) and each release, for the Mac and for the iPhone (TestFlight or an Ad Hoc link for registered iPhones). A build without the update key, such as a debug build before the key is set, doesn't look for updates.

If `codesign` can't reach your certificate's key, for example in a terminal session without access to your login keychain, sign the build ad hoc instead:

```sh
xcodebuild … CODE_SIGN_IDENTITY=-
```

## Snapshots

A debug build can draw its window into a PNG and quit. Use it to check the interface without anyone at the screen. The environment variables are listed in `Mac/Snapshots/SnapshotRun.swift`. For example:

```sh
SPINDLE_SNAPSHOT_DIR=/tmp/shots SPINDLE_SNAPSHOT_NAME=sign-in SPINDLE_SNAPSHOT_APPEARANCE=dark \
  build.noindex/DerivedData/Build/Products/Debug/Spindle.app/Contents/MacOS/Spindle
```

A snapshot run keeps its sign-in in memory and never touches the Keychain.

Liquid Glass surfaces, such as the sidebar and the toolbar buttons, are drawn by the window server, which an app can't read without the screen recording permission. In snapshots they appear as plain panels, and the sidebar's vibrant rows come out faint or missing.

## Measuring performance (Mac)

`Performance` in SpindleKit marks the work that may keep the interface waiting (split suggestions, reading agent logs,
the tracker's entries, reports) and how often views are drawn, as signposts Instruments shows under Points of
Interest. They cost next to nothing unless Instruments records them.

To measure on your own account and logs, quit Spindle and run:

```sh
apple/scripts/profile-mac.sh 120 before
```

It builds a Release copy (signed ad hoc, without updates), opens it under Instruments with the Time Profiler and Hangs
for two minutes while you use it, and prints a summary: each signpost's count and durations, how often views were
drawn, the hangs and what was measured during each, and the app's own functions that kept the main thread busy. The
recording and summary stay in `build.noindex/profiles`, so a second run after a change (`... 120 after`) can be
compared with the first.

## Quick entry (Mac only)

⌃⌥⌘K (Settings → General → Keyboard shortcuts, also in the menu bar menu) opens a panel over any app, on every Space
and over full-screen apps, without bringing Spindle to the front. Esc, a click outside or an action closes it, and
the app that was in front stays there. It is the web palette's model: one line such as `Review PR @Acme #review since 9:40`
or `Standup yesterday 9-9:15`, read by `EntrySentence` in the Kit, which runs the same cases as the web
(`tests/vectors/entry-sentence.json`); `+15` and `started 9:40` on the running timer; and a split page (↑/↓ choose a
part, ←/→ resize, a number sets its share, `+` adds a project). When agent activity suggests a split for the running
entry or the one that just stopped, it is the first row, and Enter takes it. Everything goes through the tracker and its
outbox, so it works offline; what was done can be undone from the notification it posts, or with ⌘Z in the panel.

`SPINDLE_SNAPSHOT_QUICK_ENTRY` draws the panel with the snapshot runner: `open`, `split`, or a line to type, e.g.
`Review PR @be`.

## Agent activity on the Mac

Agent activity is a beta feature. Under Settings → General → Beta, turn on **Try beta features**, then
**Agent activity in the calendar**, and choose Claude Code and/or Codex. With beta features off, the calendar
has no activity toggle and reads no logs.
The Mac reads JSONL logs in `~/.claude/projects/` and `~/.codex/sessions/`, plus Codex's `session_index.jsonl`
for thread titles. Both sources select files by modification date, so Codex threads resumed in older date
folders are included. Missing folders are harmless. Logs stay on this Mac; parsed activity is kept in memory and in a
ledger in Application Support, never in the app's cache, and is never turned into time entries. It is sent to the API
only when the person turns on activity sync for their account (see "Syncing agent activity" below).

The day view shows Focus (attention inferred from prompts, capped at 15 minutes) and each source's agent runs
(events no more than three minutes apart). Focus is drawn as blocks of uninterrupted attention, banded by how
the block divided between repositories. Click a block or run for its repositories, branches and sessions. The week view shows
only a thin Focus strip. Activity never participates in dragging entries. Today is read again when agents
write to their logs (`ActivityWatcher`, FSEvents), at most every 15 seconds. A measured week with about 0.8 GB of selected logs
loads in roughly 1–2 seconds initially, about 25 ms unchanged and about 235 ms after a small append on the
development Mac. Directory listings, parsed metadata and completed days are cached in memory; growing files
resume at the last complete line. These timings depend on the log volume and machine.

The loader bounds each record at 32 MiB and parent/title-index metadata at 1 MiB. Records above those
limits are skipped, including valid JSON, so their activity or titles may be absent. A discarded incomplete
record resumes skipping at EOF after an append, then recovers at the next newline. Reads use local
Foundation autorelease pools and a reusable buffer for records spanning chunks.

Derived data uses bounded LRU caches: parsed files 32 MiB, completed days 16 MiB (at most eight requests),
repositories and directory listings 2 MiB each, parent metadata and title indices 1 MiB each. These are
conservative accounting allowances (54 MiB combined), not a hard limit on process RAM; decoding, timeline
construction, displayed days and the allocator also use memory. Oversized values remain usable but are not
cached. Disabling beta, activity or both sources clears loader caches even outside the calendar. Completed
days retain only their contributing sessions and that day's events; timeline rules remain unchanged.

A read-only check of the same local history measured about 15 MiB of standalone-loader footprint after the
first week and 54 MiB after seven weeks, down from about 960 MiB and 1,195 MiB before the resource fixes.
First/unchanged loads took 1.65 s/25 ms. These are post-load measurements on this development Mac, not
whole-app peaks or cold-boot guarantees.

### Splitting entries from agent activity

With "Suggest splits from agent activity" on (Settings › General › Parallel work, separate from the beta calendar
lanes), the Mac suggests splits from the attention inside an entry, offers to end a running split whose work changed,
finds agent work no entry covers, and collects all of it in the day's review. Nothing is saved without a click. How
it decides, where each suggestion appears and the words it uses are in
[docs/splits-and-suggestions.md](../docs/splits-and-suggestions.md); the code is in `Mac/Split` and the Kit's
`Activity` folder.

A repository's project is, in order: a branch rule on its link, the project the person chose (in a suggestion, the
onboarding or Settings › General › Linked repositories; kept in `UserDefaults` as `repositoryProjectLinks` by the
repository's identity, and with the account), one learned from entries on a single project during which that
repository had most of the attention (at least 20 minutes, and at least 60% of it on that project), or a project
with the repository's name. "Keep as Is" stops suggestions for an entry (`dismissedSplitSuggestions`, the last 500),
on every device. The split arithmetic in `SplitMath` matches the API's, so the Mac shows the same seconds per part as
reports.

The iPhone splits entries too (`iOS/Timer/SplitSection.swift`): a bar whose dividers are dragged in 5% steps, a row per
part that opens its project, share and billable flag, swiping a part out, and Split Equally. Choosing one project, or
removing all parts but one, puts all of the time on it.

### Syncing agent activity

A repository is named without its path by `RepositoryIdentity`: `origin:` and its git origin, normalised (SSH and HTTPS
alike, without user, password, port or `.git`), or `path:` and a hash of its path when it has no origin or the origin is
itself a local path. Links and kept suggestions always go to the account (`ActivitySync`, through the unsent lists in
`SplitAdvisor`), so every device and the web agree; a link through a workspace that has the project, so one for another
workspace waits until that workspace is open. Links kept by path before this are moved to identities on launch.

With **Sync agent activity to your account** on (Settings › General › Parallel work, an account setting), the Mac
uploads sessions through `ActivityUploader`: prompts as times, agent runs as intervals, the repository's identity and
name, branches, and the title only with **Include session titles**. Sessions outside git go without a repository,
because their prompts still end the attention before them. On turning sync on it sends the last 90 days, then only
sessions that changed (it keeps a fingerprint of each in `AgentActivityUploads.json`), at most every five minutes while
agents write, when the timer stops, and every half hour otherwise. Turning sync off, a newer parser
(`ActivityLedger.version`) or allowing titles sends everything again. Each Mac has its own device ID.

### Agents over MCP

Agents use the server's MCP. Settings › Agents (`Mac/Settings/AgentSettings.swift`, `Mac/Agents/AgentConnector.swift`)
adds `<server>/mcp` to Claude Code and Codex with their own commands, in the person's interactive login shell so the
commands are found as in Terminal, and starts signing in; `claude mcp login` runs under `script`, as it needs a
terminal. Whether an agent has the server is read from its settings (`AgentConnections` in the Kit); the signed-in
agents come from the account's sessions. See [docs/connect-agents.md](../docs/connect-agents.md).

The server's MCP is the only one: the app has no MCP server of its own. `SPINDLE_SNAPSHOT_WINDOW=settingsAgents` draws the
settings tab.

For synthetic snapshots, set `SPINDLE_SNAPSHOT_AGENT_LOGS` to the absolute path of
`Mac/Snapshots/Fixtures/agent-logs`, `SPINDLE_SNAPSHOT_DAY=2026-09-28`, and
`SPINDLE_SNAPSHOT_CALENDAR_MODE=day` or `week`. The fixture root forces activity on without changing saved
settings. All fixture text is invented. Its three repository paths are `/tmp/spindle-activity-repos/{atlas,beacon,copper}`;
create empty Git repositories there for repository labels, or leave them missing to review the folder fallback.
`SPINDLE_SNAPSHOT_WINDOW=activityDetails` renders the same content as an activity popover as a separate panel.
`SPINDLE_SNAPSHOT_SIZE=1280x800` produces PNGs at exactly that pixel size, including panel previews.
