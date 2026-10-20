#!/usr/bin/env bash
# Makes a release of the iPhone app for the devices registered on the team (Ad Hoc): the app, the manifest iOS reads
# to install it from a link, and a page with that link. docs/releasing.md says what it needs and where the files go.
#
#   apple/scripts/release-ios.sh             the version in project.yml, from a clean checkout, signed for Ad Hoc
#   apple/scripts/release-ios.sh --dry-run   the same, unsigned: packages and checks everything but the signature.
#                                            The server is SPINDLE_SERVER_URL, or the one in project.yml.
set -euo pipefail

team="9ARDA688G5"
bundle_id="com.spindletrack.iphone"

dry_run=false
case "${1:-}" in
    "") ;;
    --dry-run) dry_run=true ;;
    *) echo "Usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

apple="$(cd "$(dirname "$0")/.." && pwd)"
project="$apple/Spindle.xcodeproj"
work="$apple/build.noindex/release-ios"
if $dry_run; then
    downloads="${SPINDLE_DOWNLOADS:-$apple/build.noindex/downloads-dry-run}"
else
    downloads="${SPINDLE_DOWNLOADS:-$apple/build.noindex/downloads}"
fi

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

step "Checking what the release needs"
cd "$apple"
if ! $dry_run; then
    [[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first: a release is made from a commit."
fi

# The settings a Release build gets from project.yml.
release_settings="$(xcodebuild -project "$project" -target SpindleiOS -configuration Release -sdk iphoneos -showBuildSettings 2>/dev/null)"
release_setting() {
    awk -v name="$1" '$1 == name && $2 == "=" { sub(/^[^=]*= ?/, ""); print; exit }' <<<"$release_settings"
}
version="$(release_setting MARKETING_VERSION)"
server="$(release_setting SPINDLE_SERVER_URL)"
if $dry_run; then
    server="${SPINDLE_SERVER_URL:-$server}"
fi
server="${server%/}"
[[ -n "$server" ]] || fail "Set SPINDLE_SERVER_URL for Release in project.yml."
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "MARKETING_VERSION in project.yml should look like 1.2.0, not \"$version\"."
# The same build number as the Mac app's release from this commit: the number of commits on main.
# The commit count plus 3000: the public repository starts from one commit, and build numbers must keep rising.
build="$(( $(git rev-list --count HEAD) + 3000 ))"
base="$server/downloads/ios"
echo "Spindle $version ($build) for iPhone, from $server"

step "Archiving"
rm -rf "$work"
mkdir -p "$work"
signing=(-allowProvisioningUpdates)
if $dry_run; then
    # Unsigned, for the server asked for.
    signing=(CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO "SPINDLE_SERVER_URL=$server")
fi
xcodebuild archive -quiet \
    -project "$project" -scheme SpindleiOS -configuration Release -destination "generic/platform=iOS" \
    -archivePath "$work/Spindle.xcarchive" \
    -derivedDataPath "$apple/build.noindex/DerivedData-ios-release" \
    -clonedSourcePackagesDirPath "$apple/build.noindex/DerivedData/SourcePackages" \
    -onlyUsePackageVersionsFromResolvedFile \
    MARKETING_VERSION="$version" CURRENT_PROJECT_VERSION="$build" "${signing[@]}" \
    || fail "Archiving failed. For a signed build, Xcode must be signed in to the team's account (see docs/releasing.md)."

ipa="$work/Spindle.ipa"
if $dry_run; then
    step "Packaging (unsigned)"
    mkdir -p "$work/package/Payload"
    ditto "$work/Spindle.xcarchive/Products/Applications/Spindle.app" "$work/package/Payload/Spindle.app"
    (cd "$work/package" && zip -qry "$ipa" Payload)
else
    step "Signing for Ad Hoc"
    cat > "$work/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key><string>release-testing</string>
    <key>teamID</key><string>$team</string>
    <key>signingStyle</key><string>automatic</string>
    <key>thinning</key><string>&lt;none&gt;</string>
</dict>
</plist>
PLIST
    xcodebuild -exportArchive -quiet -archivePath "$work/Spindle.xcarchive" -exportPath "$work/export" \
        -exportOptionsPlist "$work/ExportOptions.plist" -allowProvisioningUpdates \
        || fail "Signing failed. See docs/releasing.md for the certificate and the devices it needs."
    mv "$work/export/Spindle.ipa" "$ipa"
fi

step "Checking the app"
rm -rf "$work/check" && mkdir -p "$work/check"
unzip -q "$ipa" -d "$work/check"
app="$work/check/Payload/Spindle.app"
info() { /usr/libexec/PlistBuddy -c "Print :$1" "$app/Info.plist"; }
[[ "$(info CFBundleIdentifier)" == "$bundle_id" ]] || fail "The app's bundle ID isn't $bundle_id."
[[ "$(info CFBundleShortVersionString)" == "$version" ]] || fail "The app's version isn't $version."
[[ "$(info CFBundleVersion)" == "$build" ]] || fail "The app's build number isn't $build."
[[ "$(info SpindleServerURL)" == "$server" ]] || fail "The app doesn't talk to $server."
[[ -d "$app/PlugIns/SpindleWidgets.appex" ]] || fail "The Live Activity's widget extension is missing."
if ! $dry_run; then
    codesign --verify --deep --strict "$app" || fail "The app's signature isn't valid."
    profile="$(security cms -D -i "$app/embedded.mobileprovision" 2>/dev/null)"
    devices="$(plutil -extract ProvisionedDevices raw -o - - <<<"$profile" 2>/dev/null || echo 0)"
    echo "Signed for $devices registered device(s)."
fi

step "Making the install page"
staged="$work/ios"
mkdir -p "$staged"
cp "$ipa" "$staged/Spindle.ipa"
sips -z 57 57 "$apple/../branding/spindle-app-icon-1024.png" --out "$staged/icon-57.png" >/dev/null
sips -z 512 512 "$apple/../branding/spindle-app-icon-1024.png" --out "$staged/icon-512.png" >/dev/null
python3 - "$staged/manifest.plist" "$base" "$bundle_id" "$version" <<'PY'
import plistlib, sys
path, base, bundle_id, version = sys.argv[1:]
manifest = {"items": [{
    "assets": [
        {"kind": "software-package", "url": f"{base}/Spindle.ipa"},
        {"kind": "display-image", "url": f"{base}/icon-57.png"},
        {"kind": "full-size-image", "url": f"{base}/icon-512.png"},
    ],
    "metadata": {"bundle-identifier": bundle_id, "bundle-version": version, "kind": "software", "title": "Spindle"},
}]}
with open(path, "wb") as file:
    plistlib.dump(manifest, file)
PY
plutil -lint -s "$staged/manifest.plist" || fail "The manifest isn't a valid property list."
cat > "$staged/install.html" <<HTML
<!doctype html>
<html lang="en">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Install Spindle for iPhone</title>
<body style="margin: 0; font-family: -apple-system, system-ui, sans-serif; background: #f2f2f7; color: #040f0f; text-align: center">
<main style="max-width: 420px; margin: 0 auto; padding: 56px 24px">
<img src="icon-512.png" alt="" width="96" height="96" style="border-radius: 22px">
<h1 style="font-size: 28px; margin: 20px 0 4px">Spindle for iPhone</h1>
<p style="color: #6b6b70; margin: 0 0 32px">Version $version ($build)</p>
<a href="itms-services://?action=download-manifest&amp;url=$base/manifest.plist"
   style="display: block; background: #587c18; color: #fff; text-decoration: none; font-weight: 600; font-size: 17px; padding: 15px; border-radius: 999px">Install</a>
<p style="color: #6b6b70; font-size: 15px; line-height: 1.45; margin-top: 32px">
Open this page in Safari on your iPhone, tap Install, and confirm. Spindle appears on your Home Screen.<br><br>
The app installs only on iPhones registered for this build. If it doesn't, send your iPhone's UDID to whoever looks after Spindle.
</p>
</main>
</body>
</html>
HTML

mkdir -p "$downloads/ios"
rsync -a --delete "$staged/" "$downloads/ios/"

step "Done"
echo "Spindle $version ($build) for iPhone is in $downloads/ios:"
echo
printf '  %-16s %s\n' "install.html" "the page to share: $base/install.html" \
    "manifest.plist" "what iOS reads to install the app" \
    "Spindle.ipa" "the app" \
    "icon-*.png" "its icon while it installs"
echo
if $dry_run; then
    echo "This was a dry run: the app isn't signed and won't install. Don't publish it."
else
    echo "Copy the ios folder into the server's downloads folder (App:DownloadsPath), replacing what's there."
fi
