# Spindle for Android

The native Android app uses Kotlin and Jetpack Compose and talks to the same API as the web, Mac and iPhone apps. Requires Android 9 (API 28) or later.

## Build and run

Install JDK 17 and the Android SDK (platform 36). Open this directory in Android Studio, or run:

```sh
cd android
./gradlew :app:assembleDebug
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

The debug APK is signed with the local Android debug key, installs as `com.spindletrack.android.debug`, and uses Spindle Cloud by default. On the sign-in screen, choose **Server** to use another server. The emulator reaches an API on this Mac through `http://10.0.2.2:5258`. Debug permits HTTP for development; release requires HTTPS. Local SDK paths belong in the ignored `local.properties` or `ANDROID_HOME`.

Release builds use `com.spindletrack.android` and go to Google Play as a bundle signed with the upload key, which is not in the repository: `android/scripts/release.sh` builds it. [docs/releasing.md](../docs/releasing.md#android) covers signing and what the build must meet; without a key, release builds are unsigned.

## Design

The app is the iPhone app's counterpart, in Android's own controls: the same tabs, screens, words and colours, with
Material's bars, bottom sheets, full-screen editors, date and time pickers, and swipe and long-press actions. When a
screen changes on the iPhone, change it here too.

- Colours follow `branding/README.md`: neutral grounds with cards, marigold only for the main button, the start
  button and now in the calendar, bronze (marigold in dark mode) for text in the brand colour, and the projects'
  colours on dots, labels and calendar blocks. Splits show as overlapping dots and a thread of their shares.
- `ui/theme/Theme.kt` holds the colours and type, with figures that line up for timers. `ui/Components.kt` and
  `ui/Chrome.kt` are the shared pieces: cards and rows, buttons, project and split labels, bars, sheets and notices.
- `ui/Model.kt` mirrors the iPhone app's SpindleKit: how durations, days and money are written, and the workspace's
  rules for locked days and required details.
- The launcher icon, notification icon and logo are drawn from `branding/` by `scripts/generate-brand.py`. Run it
  again after the drawing changes.

## What works

- Password sign-in, registration where the server permits it, explicit terms consent, password-reset email and choosing a server.
- Continue with Google or Apple where the server offers them: the provider's page opens in a Custom Tab and comes back
  to `spindle://auth/callback` (`AuthCallbackActivity`) with a code that only this app's PKCE verifier can redeem.
  Accounts made this way have no password; Settings offers an email to set one instead of Change Password.
- The timer: description with suggestions from earlier entries, project, tags and billable as chips, the start time
  of a running timer, the Pomodoro's countdown. Entries by day, swiped to continue or delete, held for more.
- Adding and editing entries, with project and tag pickers that can create new ones. Splitting an entry's time between
  projects, as on the iPhone: a bar whose dividers are dragged in 5% steps, a page per part (project, share, billable,
  remove), and Split equally. `SplitMath` in `ui/Split.kt` is the Apple apps' arithmetic, with the same test cases.
  Choosing one project, or removing all parts but one, ends a split.
- The calendar: a week strip, a day timeline with tonal blocks, tap to add an hour, hold a block to move it.
- Summary, detailed and weekly reports, filters, grouping, rounding, saved reports shared with other apps, and
  CSV, Excel and PDF export.
- Projects with tasks, estimates, rates and colours; clients and tags; archive and delete where permitted.
- Profile, time zone, display and Pomodoro preferences, workspaces, password, signed-in devices, data export and
  account deletion.
- Long-pressing the app's icon offers Start timer and Stop timer, as Siri and Shortcuts do on the iPhone.
- Persistent offline entry changes, recovery for server-rejected drafts and reauthentication without discarding pending work.
- A running-timer notification with elapsed time and Stop, Pomodoro notifications and restoration after restart.
- A read-only organization (its trial or plan has ended) is said so above the timer and under the calendar's week,
  and Start, the add buttons and tapping the calendar are disabled. Owners and admins on the hosted service see
  **Settings → Plan**: the same words as the web's plan page and the iPhone (`PlanDescription` in `ui/Model.kt`) and
  the seats. As on the iPhone, there is no way to choose a plan and no mention of where to pay: Google Play's payments
  policy, like the App Store's, limits pointing to buying outside the store in most countries.

Not yet:

- Passkeys. The iPhone signs in with them on Spindle Cloud.
- Split suggestions from agent activity and the day's review. The Mac and Windows apps have them, and so does the web
  when activity sync is on.
- Saving a split as separate entries ("Split at 10:42"), which the Mac, Windows and the web can do.

## Screenshots without tapping

A debug build reads launch options from the intent's extras, as the iPhone app's `SPINDLE_AUTOMATION_*` variables:
`email`, `password` and `server` sign in, `tab` opens a tab (`timer`, `calendar`, `reports`, `projects`,
`settings`), `edit` (`first`, `running`, `new`), `picker` (`project`, `tags`), `report` (`summary`, `detailed`,
`weekly`), `filters` (`1`), `manage` (`projects`, `clients`, `tags`), `project` (a name, or `new`), `auth`
(`signIn`, `register`, `forgotPassword`) and `description`. For example:

```sh
adb shell am start -n com.spindletrack.android.debug/com.spindletrack.android.MainActivity \
  --es server http://10.0.2.2:5258 --es email ana@example.com --es password '…' --es tab reports --es report weekly
```

The UI smoke test (`LocalUiSmokeTest`) goes through every tab against a local API on a new account:

```sh
./gradlew :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.class=com.spindletrack.android.LocalUiSmokeTest \
  -Pandroid.testInstrumentationRunnerArguments.server=http://10.0.2.2:5258
```

## Persistence and synchronization

Room stores separate caches for each server/account/workspace and an ordered outbox. Entry UUIDs are created before sending; a lost create response can be retried without creating another entry. Stopping uses the retry-safe `/stop` endpoint and the entry's own workspace, including when another workspace is selected. Rejected changes retain their draft and offer Edit, Retry and Discard. Edits, stops and deletes carry the version of the entry they were made on (`If-Match`); when another device changed the entry since, the server refuses the change and it becomes a rejected draft too, showing theirs: Retry sends yours again over theirs, Discard keeps theirs. Signing out with pending changes requires explicit confirmation.

The server's answers are authoritative. Pending changes overlay the cache; rejected changes do not pretend to have succeeded. The global running timer is cached separately from workspace data. Session tokens are encrypted with an Android Keystore key, and token rotation is coordinated across requests. Android backups are disabled so account data and encrypted sessions are not copied to another device.

SignalR refreshes data while the app is in front. WorkManager sends queued changes when Android allows it; opening the app also sends them and reloads changes from other devices. Android may delay background work. Without remote push, a timer stopped elsewhere while this app is suspended can remain in its notification until the next sync. Timer duration itself is calculated from timestamps, not a background loop.

Pomodoro uses battery-aware inexact alarms. Notifications may be delayed by Android, but an automatic stop records the planned focus end time. Required workspace details can prevent the stop; the app retains the timer and asks the person to finish its details. Notification permission is requested when a timer first runs.

## Checks

From the repository root:

```sh
./scripts/check.sh android
```

From this directory:

```sh
./gradlew :app:assembleDebug :app:testDebugUnitTest :app:lintDebug
```

API models come from `openapi/spindle.json`. From the repository root:

```sh
python3 android/scripts/generate-models.py
python3 android/scripts/generate-models.py --check
```

Device smoke tests run against a local API with a disposable account. Start the API and an emulator, then:

```sh
./gradlew connectedDebugAndroidTest \
  -Pandroid.testInstrumentationRunnerArguments.server=http://10.0.2.2:5260
```

They check account creation, projects/tags, timer edits, persistence across repository recreation, stopping, splits and report totals; actual Compose interactions across all five tabs; and stale Pomodoro alarms delivered through WorkManager. They refuse a production server and only clear their own local test sessions. Run them on an isolated installation and database: they create disposable accounts, which remain in that test database. Regular unit tests cover offline queues, retries, token rotation, workspace routing, stop rejection, calendar rules and report settings.

Validation of this implementation: debug APK built; 19 unit/integration tests, Android lint (no errors), and all three device tests passed on an Android 14 emulator against an isolated local PostgreSQL database. A release build signed with a test key installs and starts on an Android 14 emulator against Spindle Cloud. Physical-device and Google Play validation have not been run.
