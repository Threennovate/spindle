# Releasing

How each part of Spindle is built, signed and published. The scripts and workflows named here are in the repository;
the keys, certificates and servers they need are yours. If you build the apps for your own server, first change the
names and addresses listed in [self-hosting.md](self-hosting.md#building-the-apps-for-your-own-server).

| What | Version | Published by | Goes to |
| --- | --- | --- | --- |
| Server image | the `server-X.Y.Z` tag | **Server image** workflow | `ghcr.io/threennovate/spindle` |
| Mac app | `MARKETING_VERSION` in `apple/project.yml` | **Mac tag** and **Mac release** workflows, or `apple/scripts/publish-mac.sh` | the server's downloads folder (Sparkle) |
| iPhone app | the same `MARKETING_VERSION` | Xcode or `xcodebuild` | App Store Connect |
| Windows app | `<Version>` in `windows/Directory.Build.props` | `windows/scripts/publish-windows.sh` | the server's downloads folder (Velopack) |
| Android app | `versionName` in `android/app/build.gradle.kts` | `android/scripts/release.sh` | Google Play |

**Build numbers** of the Mac, iPhone and Android apps are the commit count (`git rev-list --count HEAD`) plus an offset
set in `apple/scripts/release-mac.sh`, `apple/scripts/release-ios.sh` and `android/scripts/release.sh`. The offset
keeps them above the builds made before this repository started; the count makes them rise with every commit on
`main`. They need the full history, so CI fetches it; don't squash or rewrite `main` between releases.

**Release notes** for the Mac and Windows apps are `apple/ReleaseNotes/X.Y.Z.md` and `windows/ReleaseNotes/X.Y.Z.md`:
a short list of what's new, shown in the app's update window.

## Server

Pushing a tag such as `server-1.2.0` builds the image for amd64 and arm64 and publishes it tagged `1.2.0`, `1.2`, `1`
and `latest`. Self-hosters pull it with [deploy/docker-compose.yml](../deploy/docker-compose.yml). The migrations run
when the new version starts.

Spindle Cloud deploys from **Web CI**: once the checks on `main` pass, its `deploy` job calls Coolify's deploy webhook.
It needs the environment `web-production` with the secrets `COOLIFY_TOKEN` and `COOLIFY_DEPLOY_WEBHOOK`, and
Coolify's own auto deploy off, so a push isn't deployed twice or before its checks.

## Mac

People download a notarized disk image from the server's downloads folder (`App:DownloadsPath`), and the app updates
itself from a [Sparkle](https://sparkle-project.org) feed there:

| Address | What it is |
| --- | --- |
| `/downloads/Spindle.dmg` | Always the latest version. `/download/mac` leads here. |
| `/downloads/spindle-mac/Spindle-X.Y.Z.dmg` | One version. Installed apps update to it. |
| `/downloads/spindle-mac/appcast.xml` | The feed. Installed apps check it once a day and on **Spindle › Check for Updates…**. |

Release builds have the server's address built in (`SPINDLE_SERVER_URL` in `apple/project.yml`). Sparkle installs an
update only if it is signed with the private update key and the app in it is signed by the same Apple team as the
installed one, so whoever controls the server can't slip in anything else.

The web shows **Download for Mac** once `HEAD /downloads/Spindle.dmg` returns a disk image.

### Once: keys and certificates

You need Xcode and XcodeGen. The first run of `release-mac.sh` downloads Sparkle's tools into `apple/build.noindex/`
(a dry run, below, gets them too).

1. **Developer ID certificate.** In Xcode, **Settings › Accounts**, select the team, **Manage Certificates… › + ›
   Developer ID Application**. Only the team's Account Holder can create one. Export it with its private key as a
   `.p12` and keep it somewhere safe. A lost certificate can be replaced: updates are matched by team, not by
   certificate.
2. **Notarization.** Create an app-specific password at [account.apple.com](https://account.apple.com) and save it
   under the name the scripts look for (`SPINDLE_NOTARY_PROFILE` picks another):

   ```sh
   xcrun notarytool store-credentials Spindle --apple-id YOUR_APPLE_ID --team-id YOUR_TEAM_ID
   ```

3. **Update key.** `apple/build.noindex/sparkle-2.10.0/bin/generate_keys` creates the key pair, keeps the private key
   in your login keychain and prints the public key. Put the public key in `apple/project.yml` as
   `SPINDLE_UPDATES_PUBLIC_KEY`, run `xcodegen` in `apple/` and commit it. Back the private key up with
   `generate_keys -x FILE` and import it on another Mac with `generate_keys -f FILE`. **Never commit the private
   key.** If it is lost, installed apps can't be updated any more and everyone has to download the next version by
   hand.

### Every release

1. In a pull request, raise `MARKETING_VERSION` in `apple/project.yml`, run `xcodegen` in `apple/`, and add
   `apple/ReleaseNotes/X.Y.Z.md`.
2. Merge it. **Mac tag** sees a version with release notes and no tag yet, tags the merge commit `mac-X.Y.Z` and starts
   **Mac release**, which tests, signs and notarizes the disk image, signs the feed, and publishes both over SSH with
   the feed last. Merging a version with notes is the decision to publish; a version without notes releases nothing,
   so a version can be raised first and released when the notes land.
3. Install the previous version and check that it updates.

**Mac release** needs two GitHub environments:

- `mac-release`, secrets `MAC_CERTIFICATE_BASE64` and `MAC_CERTIFICATE_PASSWORD` (the Developer ID `.p12`),
  `MAC_PROVISIONING_PROFILE_BASE64` (the Developer ID profile with Associated Domains, for passkeys),
  `APPLE_NOTARY_KEY_BASE64`, `APPLE_NOTARY_KEY_ID` and `APPLE_NOTARY_ISSUER_ID` (an App Store Connect API key), and
  `SPARKLE_PRIVATE_KEY` (Sparkle's base64 Ed25519 seed); variable `SPARKLE_PUBLIC_KEY`.
- `mac-publish`, secrets `MAC_UPLOAD_SSH_KEY`, `MAC_UPLOAD_KNOWN_HOSTS`, `MAC_UPLOAD_HOST`, `MAC_UPLOAD_USER` and
  `MAC_DOWNLOADS_PATH`. The host, user and path are secrets too, so the public workflow logs don't show them. The
  upload user needs SSH, Python 3.11+, rsync and write access to the downloads folder, nothing more. Files are
  published with mode 0644 so the API container can read them.

Without the workflows, `apple/scripts/publish-mac.sh` does the same from an up-to-date `main`, with the certificate,
notarization profile and update key in this Mac's keychain and `SPINDLE_UPLOAD_SSH`, `SPINDLE_DOWNLOADS_PATH` and
`SPINDLE_RELEASE_USER` set. It pushes the `mac-X.Y.Z` tag at the end, so don't use it while the workflows run: the tag
would start **Mac release** too.

The publisher takes a lock, checks file hashes, refuses older builds and changed existing versions, keeps previous
downloads, and replaces `appcast.xml` last, so a failed upload leaves the previous release active. Fix a bad version
with a newer one. `python3 -m unittest discover -s apple/scripts -p 'test_*.py'` tests these rules.

### Trying it without the certificate

```sh
apple/scripts/release-mac.sh --dry-run
```

It signs ad hoc, skips notarization, builds without the hardened runtime (an ad-hoc-signed app under it can't load
Sparkle), uses a throwaway update key and writes to `apple/build.noindex/downloads-dry-run/`. Any `SETTING=value`
after `--dry-run` goes to `xcodebuild`.

To watch an update end to end, run the API with `App__DownloadsPath` set to that folder, make the dry run with
`SPINDLE_SERVER_URL` pointing at that API, build an older version with the same settings (for example
`MARKETING_VERSION=0.9.0 CURRENT_PROJECT_VERSION=1`) and start it. Use another bundle ID, such as
`PRODUCT_BUNDLE_IDENTIFIER=com.spindletrack.mac.dryrun`, so the test app doesn't touch your real app's settings or
Keychain.

## iPhone

The iPhone app goes through App Store Connect: TestFlight first, then the App Store.

1. Sign in to the team in Xcode **Settings › Accounts**. Xcode's automatic signing makes the Apple Distribution
   certificate and the App Store profiles for the app and its widget extension.
2. Archive with a build number higher than the last upload, in Xcode (**SpindleiOS**, any iOS device, **Product →
   Archive**) or from `apple/`:

   ```sh
   xcodebuild archive -project Spindle.xcodeproj -scheme SpindleiOS \
     -configuration Release -destination 'generic/platform=iOS' \
     -archivePath build.noindex/testflight/Spindle.xcarchive \
     -onlyUsePackageVersionsFromResolvedFile \
     CURRENT_PROJECT_VERSION=$(( $(git rev-list --count HEAD) + 3000 )) -allowProvisioningUpdates
   ```

3. Upload it from Xcode's Organizer (**Distribute App → App Store Connect**), or export with
   `scripts/ExportOptions-TestFlight.plist` and its `destination` set to `upload`.
4. When App Store Connect has processed the build, test it in TestFlight, then submit it for review.

The app declares `ITSAppUsesNonExemptEncryption=false`, and `iOS/Resources/PrivacyInfo.xcprivacy` declares its
UserDefaults use; review both when that changes.

`apple/scripts/release-ios.sh` makes an Ad Hoc build instead, with an install page for the server's downloads folder:
it installs only on iPhones registered on the team and stops opening when its profile expires a year later.
`--dry-run` does it unsigned.

## Windows

Spindle for Windows installs and updates itself with [Velopack](https://velopack.io). A release is published from a
Mac:

1. In a pull request, raise `<Version>` in `windows/Directory.Build.props`, add `windows/ReleaseNotes/X.Y.Z.md`, and
   run `scripts/check.sh windows`.
2. From an up-to-date `main`, with `SPINDLE_UPLOAD_SSH`, `SPINDLE_DOWNLOADS_PATH` and `SPINDLE_RELEASE_USER` set as for
   the Mac, run `windows/scripts/publish-windows.sh`. It checks the version, notes and tag, runs the Windows checks,
   builds both architectures, uploads the files with each feed last, compares what the server serves with what was
   built, and pushes the `windows-X.Y.Z` tag.
3. Install the previous version on a Windows PC and check that it updates (**Settings → General → About → Check for
   updates**).

`windows/scripts/release-windows.sh` alone builds into `windows/artifacts/release/downloads` without publishing. With
`SPINDLE_WINDOWS_FEED=http://localhost:8765/spindle-windows` it reads a local feed, for trying a release against a
folder served with `python3 -m http.server 8765`.

| In the downloads folder | What it is |
| --- | --- |
| `Spindle-Setup.exe` | Installer for x64 PCs; Windows on Arm runs it too. `/download/windows` leads here. |
| `Spindle-Setup-arm64.exe` | Native Arm64 installer. `/download/windows-arm64` leads here. |
| `spindle-windows/releases.win-x64.json`, `releases.win-arm64.json` | The feeds installed copies read, one per architecture. |
| `spindle-windows/Spindle.Desktop-X.Y.Z-win-*-full.nupkg`, `-delta.nupkg` | The update packages the feeds name. |

Velopack installs per user, without administrator rights, into `%LocalAppData%\Spindle.Desktop`; the app's data stays
in `%LocalAppData%\Spindle`, which uninstalling leaves alone. The package ID `Spindle.Desktop` must never change, or
installed copies no longer find their updates. The app checks the feed 30 seconds after launch and every 6 hours, and
installs a downloaded version when Spindle quits or on **Restart to update**. The publisher
(`windows/scripts/windows-release-bundle.py`) follows the Mac's rules: a lock, checksums, no older or changed versions,
feeds last.

Builds aren't signed yet, so Windows shows SmartScreen's prompt on the first install (**More info → Run anyway**) and
Smart App Control blocks the installer where it is on. Updates downloaded by the installed app don't show it.

## Android

Google Play signs the installed app with a key Google keeps (Play App Signing); you sign what you upload with an
**upload key**. Create it once, outside the repository:

```sh
keytool -genkeypair -v -keystore ~/.spindle/android/upload.jks -alias upload -keyalg RSA -keysize 4096 -validity 10000
```

Then copy `android/keystore.properties.example` to `android/keystore.properties` (ignored by git) and fill it in, or
set `SPINDLE_UPLOAD_STORE_FILE`, `SPINDLE_UPLOAD_STORE_PASSWORD`, `SPINDLE_UPLOAD_KEY_ALIAS` and
`SPINDLE_UPLOAD_KEY_PASSWORD`. Without either, a release build is unsigned. Keep the first upload's default of a key
Google generates.

For a release, raise `versionName` in a pull request if it is a new version for people, then run
`android/scripts/release.sh` from an up-to-date `main`. It runs `scripts/check.sh android`, builds the bundle, checks
that it is signed with the upload key and not the debug key, and copies it to
`android/build/release/Spindle-<version>-<code>.aab` for Play Console. Play refuses a version code it has seen before;
`SPINDLE_VERSION_CODE` sets a higher one. `--dry-run` builds with uncommitted changes and, without a key, unsigned: to
try the build, never to upload.

What the build must meet:

- **Target API 36** (Android 16), which Google Play requires; it moves up every August. On Android 16, screens 600 dp
  and wider ignore orientation and resizability limits, so the app works in any size and orientation.
- **16 KB pages.** Native libraries must be aligned to 16 KB. The only one now is AndroidX's
  `libandroidx.graphics.path.so`, which is; check again when a dependency brings native code.
- **HTTPS only** in release builds (`network_security_config.xml`); debug builds allow HTTP for local servers.
- R8 is off. Turning it on needs rules for kotlinx.serialization's generated models, OkHttp and SignalR, and a test of
  a minified build.

## Store pictures

The App Store and Google Play screenshots show a made-up studio (`android/scripts/play-demo-data.py`) on a local API:

- `apple/scripts/appstore-screenshots.sh` takes the raw iPhone screenshots on a simulator into `apple/appstore/`.
- `android/scripts/play-screenshots.sh` takes the Android ones on an emulator into `android/play/`, and
  `node android/scripts/play-graphics.mjs` draws the 512 × 512 icon there.
- `node branding/store/store-art.mjs ios` (or `android`) turns the raw screenshots into the captioned pictures that are
  uploaded, in `branded/` next to them.

Both folders are ignored by git. The data is relative to today, so take the screenshots again rather than reusing old
ones.
