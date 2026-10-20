#!/usr/bin/env bash
# Makes a release of the Mac app: a disk image signed with Developer ID and notarized by Apple, and the update feed
# that tells installed apps about it. docs/releasing.md says what it needs and what to do with what it makes.
#
#   apple/scripts/release-mac.sh                          the version in project.yml, from a clean checkout
#   apple/scripts/release-mac.sh --dry-run [SETTING=value]   the same, signed ad hoc and not notarized, with a
#                                                         throwaway update key: tries everything that doesn't need
#                                                         the certificate. Settings go to xcodebuild; the server is
#                                                         SPINDLE_SERVER_URL, or the one in project.yml.
set -euo pipefail

# The tools must match the Sparkle the app is built with; update both after `Package.resolved` moves to a new version.
sparkle_version="2.10.0"
sparkle_sha256="c2bf58aa8387266ac179357b1415d6f2635f044da8be41042af32425dae6da0c"
team="9ARDA688G5"
notary_profile="${SPINDLE_NOTARY_PROFILE:-Spindle}"

dry_run=false
extra_settings=()
if [[ "${1:-}" == "--dry-run" ]]; then
    dry_run=true
    shift
    extra_settings=("$@")
elif [[ $# -gt 0 ]]; then
    echo "Usage: $0 [--dry-run [SETTING=value ...]]" >&2
    exit 2
fi

apple="$(cd "$(dirname "$0")/.." && pwd)"
project="$apple/Spindle.xcodeproj"
work="$apple/build.noindex/release"
if $dry_run; then
    downloads="${SPINDLE_DOWNLOADS:-$apple/build.noindex/downloads-dry-run}"
else
    downloads="${SPINDLE_DOWNLOADS:-$apple/build.noindex/downloads}"
fi

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
ed25519() { xcrun swift "$apple/scripts/ed25519.swift" "$@"; }

# --- What the release needs --------------------------------------------------------------------------------------

step "Checking what the release needs"
cd "$apple"
if ! $dry_run; then
    [[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first: a release is made from a commit."
    identity="$(security find-identity -v -p codesigning | awk -v team="($team)" '/"Developer ID Application: / && index($0, team) { print $2; exit }')"
    [[ -n "$identity" ]] || fail "No Developer ID Application certificate for team $team in your keychain. See docs/releasing.md."
    xcrun notarytool history --keychain-profile "$notary_profile" >/dev/null 2>&1 \
        || fail "No notarization credentials saved as \"$notary_profile\", or Apple can't be reached. See docs/releasing.md."
    profile="${SPINDLE_MAC_PROVISIONING_PROFILE:-}"
    [[ -f "$profile" ]] || fail "Set SPINDLE_MAC_PROVISIONING_PROFILE to the Developer ID profile with Associated Domains enabled. See apple/README.md."
    profile_plist="$(mktemp)"
    security cms -D -i "$profile" > "$profile_plist" || { rm -f "$profile_plist"; fail "Cannot read the Mac provisioning profile."; }
    profile_id="$(python3 "$apple/scripts/mac-passkey-profile.py" "$profile_plist")" || { rm -f "$profile_plist"; fail "The Mac provisioning profile does not support this app's passkeys."; }
    rm -f "$profile_plist"
    profiles="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
    mkdir -p "$profiles"
    [[ "$profile" == "$profiles/$profile_id.provisionprofile" ]] || cp "$profile" "$profiles/$profile_id.provisionprofile"
    if [[ -n "${RUNNER_TEMP:-}" ]]; then
        printf '%s' "$profile_id" > "$RUNNER_TEMP/mac-profile-id"
    fi
fi

resolved="$project/project.xcworkspace/xcshareddata/swiftpm/Package.resolved"
built_with="$(python3 -c 'import json, sys; print(next(p["state"]["version"] for p in json.load(open(sys.argv[1]))["pins"] if p["identity"] == "sparkle"))' "$resolved")"
[[ "$built_with" == "$sparkle_version" ]] \
    || fail "The app is built with Sparkle $built_with but this script uses its $sparkle_version tools. Update sparkle_version and sparkle_sha256."

tools="$apple/build.noindex/sparkle-$sparkle_version"
if [[ ! -x "$tools/bin/generate_appcast" ]]; then
    step "Downloading Sparkle $sparkle_version's tools"
    rm -rf "$tools" && mkdir -p "$tools"
    curl -fsSL -o "$tools/sparkle.tar.xz" "https://github.com/sparkle-project/Sparkle/releases/download/$sparkle_version/Sparkle-$sparkle_version.tar.xz"
    echo "$sparkle_sha256  $tools/sparkle.tar.xz" | shasum -a 256 -c --quiet - || { rm -rf "$tools"; fail "Sparkle's download doesn't match its checksum."; }
    tar -xf "$tools/sparkle.tar.xz" -C "$tools"
fi

# The settings a Release build gets from project.yml.
release_settings="$(xcodebuild -project "$project" -target SpindleMac -configuration Release -showBuildSettings 2>/dev/null)"
release_setting() {
    awk -v name="$1" '$1 == name && $2 == "=" { sub(/^[^=]*= ?/, ""); print; exit }' <<<"$release_settings"
}
version="$(release_setting MARKETING_VERSION)"
server="$(release_setting SPINDLE_SERVER_URL)"
public_key="${SPINDLE_UPDATES_PUBLIC_KEY:-$(release_setting SPINDLE_UPDATES_PUBLIC_KEY)}"
key_arguments=()
overrides=()

if $dry_run; then
    # Stand-ins for what a real release takes from project.yml and the keychain.
    server="${SPINDLE_SERVER_URL:-$server}"
    mkdir -p "$work"
    key_file="$work/throwaway-update-key.txt"
    [[ -f "$key_file" ]] || ed25519 new "$key_file" >/dev/null
    public_key="$(ed25519 public "$key_file")"
    key_arguments=(--ed-key-file "$key_file")
    # Without the hardened runtime, because under it an app signed ad hoc may not load Sparkle, which is too.
    overrides=(CODE_SIGN_IDENTITY=- CODE_SIGN_ENTITLEMENTS= ENABLE_HARDENED_RUNTIME=NO "SPINDLE_SERVER_URL=$server" "SPINDLE_UPDATES_PUBLIC_KEY=$public_key" ${extra_settings[@]+"${extra_settings[@]}"})
else
    [[ -n "$public_key" ]] || fail "Set SPINDLE_UPDATES_PUBLIC_KEY in project.yml. See docs/releasing.md."
    if [[ -n "${SPINDLE_UPDATES_PRIVATE_KEY_FILE:-}" ]]; then
        signing_public_key="$(ed25519 public "$SPINDLE_UPDATES_PRIVATE_KEY_FILE")"
        key_arguments=(--ed-key-file "$SPINDLE_UPDATES_PRIVATE_KEY_FILE")
    else
        signing_public_key="$("$tools/bin/generate_keys" -p 2>/dev/null)" || fail "No update signing key in your keychain. See docs/releasing.md."
    fi
    [[ "$signing_public_key" == "$public_key" ]] \
        || fail "The update key in your keychain isn't the one in project.yml; installed apps would refuse this update."
    # The profile is the app's alone, so it goes through the app's own setting rather than to every target.
    overrides=("SPINDLE_UPDATES_PUBLIC_KEY=$public_key" "SPINDLE_MAC_PROFILE=$profile_id")
fi
server="${server%/}"
[[ -n "$server" ]] || fail "Set SPINDLE_SERVER_URL for Release in project.yml: the app, the feed and the downloads live there."
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "MARKETING_VERSION in project.yml should look like 1.2.0, not \"$version\"."

# Updates are compared by build number, which grows with every commit on main.
# The commit count plus 3000: the public repository starts from one commit, and build numbers must keep rising.
build="$(( $(git rev-list --count HEAD) + 3000 ))"
feed="$downloads/spindle-mac/appcast.xml"
image_name="Spindle-$version.dmg"
[[ ! -e "$downloads/spindle-mac/$image_name" ]] || fail "$version is already released ($downloads/spindle-mac/$image_name). Raise MARKETING_VERSION in project.yml."
if [[ -f "$feed" ]]; then
    latest="$(grep -o '<sparkle:version>[0-9]*</sparkle:version>' "$feed" | grep -o '[0-9]\+' | sort -n | tail -1)"
    [[ -z "$latest" || "$build" -gt "$latest" ]] || fail "Build $build isn't newer than build $latest in the feed. Release from a newer commit."
fi
echo "Spindle $version ($build) for $server"

# --- Building ----------------------------------------------------------------------------------------------------

step "Archiving"
rm -rf "$work/Spindle.xcarchive" "$work/export" "$work/dmg" "$work"/*.dmg "$work"/*.zip
mkdir -p "$work"
xcodebuild archive -quiet \
    -project "$project" -scheme SpindleMac -configuration Release -destination "generic/platform=macOS" \
    -archivePath "$work/Spindle.xcarchive" \
    -derivedDataPath "$apple/build.noindex/DerivedData-release" \
    -clonedSourcePackagesDirPath "$apple/build.noindex/DerivedData/SourcePackages" \
    -onlyUsePackageVersionsFromResolvedFile \
    MARKETING_VERSION="$version" CURRENT_PROJECT_VERSION="$build" ${overrides[@]+"${overrides[@]}"}

app="$work/export/Spindle.app"
if $dry_run; then
    mkdir -p "$work/export"
    ditto "$work/Spindle.xcarchive/Products/Applications/Spindle.app" "$app"
else
    step "Signing with Developer ID"
    cat > "$work/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key><string>developer-id</string>
    <key>teamID</key><string>$team</string>
    <key>signingStyle</key><string>manual</string>
    <key>signingCertificate</key><string>Developer ID Application</string>
    <key>provisioningProfiles</key><dict><key>com.spindletrack.mac</key><string>$profile_id</string></dict>
</dict>
</plist>
PLIST
    xcodebuild -exportArchive -quiet -archivePath "$work/Spindle.xcarchive" -exportPath "$work/export" -exportOptionsPlist "$work/ExportOptions.plist"
fi

step "Checking the app"
codesign --verify --deep --strict "$app"
info="$app/Contents/Info.plist"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$info")" == "$version" ]] || fail "The app's version isn't $version."
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$info")" == "$build" ]] || fail "The app's build number isn't $build."
[[ "$(/usr/libexec/PlistBuddy -c 'Print :SUPublicEDKey' "$info")" == "$public_key" ]] || fail "The app doesn't have the update key."
[[ "$(/usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$info")" == "$server/downloads/spindle-mac/appcast.xml" ]] || fail "The app doesn't look for updates on $server."
if ! $dry_run; then
    # Notarization refuses a program without the hardened runtime, e.g. one of Sparkle's helpers.
    while IFS= read -r code; do
        details="$(codesign -d --verbose=2 "$code" 2>&1)"
        grep -q "Authority=Developer ID Application" <<<"$details" || fail "$code isn't signed with Developer ID."
        [[ "$code" == *.framework ]] || grep -q "flags=.*runtime" <<<"$details" || fail "$code doesn't have the hardened runtime."
    done < <(find "$app" \( -name "*.app" -o -name "*.xpc" -o -name "*.framework" -o -name Autoupdate \) -print)
fi

# Sends a file to Apple's notary service and waits for the verdict.
notarize() {
    local file="$1" result id status
    result="$(xcrun notarytool submit "$file" --keychain-profile "$notary_profile" --wait --output-format json)"
    id="$(python3 -c 'import json, sys; print(json.load(sys.stdin)["id"])' <<<"$result")"
    status="$(python3 -c 'import json, sys; print(json.load(sys.stdin)["status"])' <<<"$result")"
    if [[ "$status" != "Accepted" ]]; then
        xcrun notarytool log "$id" --keychain-profile "$notary_profile" >&2 || true
        fail "Apple didn't notarize $(basename "$file"): $status."
    fi
}

if ! $dry_run; then
    step "Notarizing the app (this takes a few minutes)"
    ditto -c -k --keepParent "$app" "$work/Spindle.zip"
    notarize "$work/Spindle.zip"
    xcrun stapler staple -q "$app"
    spctl --assess --type execute "$app" || fail "Gatekeeper doesn't accept the notarized app."
fi

# --- The disk image ----------------------------------------------------------------------------------------------

step "Making the disk image"
mkdir -p "$work/dmg"
ditto "$app" "$work/dmg/Spindle.app"
ln -s /Applications "$work/dmg/Applications"
image="$work/$image_name"
hdiutil create -quiet -volname "Spindle" -srcfolder "$work/dmg" -fs HFS+ -format UDZO -ov "$image"
if ! $dry_run; then
    codesign --sign "$identity" --timestamp "$image"
    step "Notarizing the disk image"
    notarize "$image"
    xcrun stapler staple -q "$image"
    spctl --assess --type open --context context:primary-signature "$image" || fail "Gatekeeper doesn't accept the disk image."
fi

# --- The update feed ---------------------------------------------------------------------------------------------

step "Updating the feed"
# In a copy, so the downloads folder changes only once everything has worked.
staged="$work/feed"
rm -rf "$staged" && mkdir -p "$staged"
[[ ! -d "$downloads/spindle-mac" ]] || ditto "$downloads/spindle-mac" "$staged"
cp "$image" "$staged/$image_name"
notes="$apple/ReleaseNotes/$version.md"
if [[ -f "$notes" ]]; then
    cp "$notes" "$staged/Spindle-$version.md"
else
    echo "No release notes: add ReleaseNotes/$version.md to show what's new in the update window."
fi
"$tools/bin/generate_appcast" ${key_arguments[@]+"${key_arguments[@]}"} \
    --download-url-prefix "$server/downloads/spindle-mac/" \
    --embed-release-notes \
    --link "$server" \
    "$staged"
# generate_appcast moves files it considers old into old_updates, but can keep pointing at some of them from items it
# kept, e.g. their deltas. Those go back beside the feed; the rest aren't published (the server keeps what it has).
python3 - "$staged" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

staged = pathlib.Path(sys.argv[1])
old = staged / "old_updates"
if old.is_dir():
    referenced = {e.get("url", "").rsplit("/", 1)[-1] for e in ET.parse(staged / "appcast.xml").getroot().iter("enclosure")}
    for path in old.iterdir():
        if path.name in referenced:
            path.rename(staged / path.name)
PY
rm -rf "$staged/old_updates"

step "Checking the feed"
# The feed's entry for this build, as an installed app will read it.
entry="$(python3 - "$staged/appcast.xml" "$build" <<'PY'
import sys
import xml.etree.ElementTree as ET

sparkle = "{http://www.andymatuschak.org/xml-namespaces/sparkle}"
for item in ET.parse(sys.argv[1]).getroot().iter("item"):
    if item.findtext(sparkle + "version") == sys.argv[2]:
        enclosure = item.find("enclosure")
        print(enclosure.get("url"), enclosure.get("length"), enclosure.get(sparkle + "edSignature"))
        break
PY
)"
read -r url length signature <<<"$entry"
[[ "$url" == "$server/downloads/spindle-mac/$image_name" ]] || fail "The feed downloads build $build from \"$url\"."
[[ "$length" == "$(stat -f %z "$image")" ]] || fail "The feed has the wrong size for the disk image."
ed25519 verify "$public_key" "$signature" "$image" || fail "The app's update key doesn't accept the feed's signature."

mkdir -p "$downloads/spindle-mac"
rsync -a --delete "$staged/" "$downloads/spindle-mac/"
# A link that always gets the latest version, for the website.
cp "$image" "$downloads/Spindle.dmg"

step "Done"
echo "Spindle $version ($build) is in $downloads:"
echo
printf '  %-26s %s\n' "Spindle.dmg" "the latest version, for people installing Spindle" \
    "spindle-mac/$image_name" "this version, which installed apps update to" \
    "spindle-mac/appcast.xml" "the feed installed apps check for updates"
echo
if $dry_run; then
    echo "This was a dry run: signed ad hoc, not notarized, with a throwaway update key. Don't publish it."
else
    cat <<DONE
Copy everything in that folder to the server's downloads folder (App:DownloadsPath), keeping what's already there.
Put the feed up last, so no app hears about the update before its disk image is there. Then tag the release:

  git tag mac-$version && git push origin mac-$version
DONE
fi
