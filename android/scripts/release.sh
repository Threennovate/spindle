#!/usr/bin/env bash
# Builds the Android app bundle (.aab) to upload to Google Play, signed with the upload key. docs/releasing.md
# says what it needs.
#
#   android/scripts/release.sh             from a clean checkout: checks, then a signed bundle
#   android/scripts/release.sh --dry-run   the same with uncommitted changes, and unsigned without a key: to try the build
#
# The version code is the commit count plus 3000, as the Mac app's build numbers, so it rises with every commit on
# main. SPINDLE_VERSION_CODE sets another, e.g. if Play has already seen a higher one.
set -euo pipefail

dry_run=false
case "${1:-}" in
    "") ;;
    --dry-run) dry_run=true ;;
    *) echo "Usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

android="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$android/.." && pwd)"

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

step "Checking what the release needs"
cd "$root"
if ! $dry_run; then
    [[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first: a release is made from a commit."
    if [[ ! -f "$android/keystore.properties" && -z "${SPINDLE_UPLOAD_STORE_FILE:-}" ]]; then
        fail "No upload key: copy android/keystore.properties.example to android/keystore.properties and fill it in."
    fi
fi
version_name="$(sed -nE 's/^ *versionName = "([^"]+)".*/\1/p' "$android/app/build.gradle.kts")"
version_code="${SPINDLE_VERSION_CODE:-$(( $(git rev-list --count HEAD) + 3000 ))}"
[[ -n "$version_name" ]] || fail "Couldn't read versionName from android/app/build.gradle.kts."
echo "Spindle $version_name ($version_code) from $(git rev-parse --short HEAD) on $(git branch --show-current)"

step "Running the Android checks"
"$root/scripts/check.sh" android

step "Building the bundle"
cd "$android"
./gradlew :app:bundleRelease -PversionCode="$version_code" --console=plain --quiet
bundle="$android/app/build/outputs/bundle/release/app-release.aab"
[[ -f "$bundle" ]] || fail "Gradle didn't produce $bundle."

out="$android/build/release"
mkdir -p "$out"
if $dry_run; then
    target="$out/Spindle-$version_name-$version_code-dry-run.aab"
else
    step "Checking the signature"
    signature="$(jarsigner -verify -verbose -certs "$bundle")"
    grep -q "jar verified" <<<"$signature" || fail "The bundle isn't signed."
    grep -q "CN=Android Debug" <<<"$signature" && fail "The bundle is signed with the debug key, not the upload key."
    target="$out/Spindle-$version_name-$version_code.aab"
fi
cp "$bundle" "$target"

echo
echo "Bundle: $target"
$dry_run || echo "Upload it in Play Console: Test and release → Testing → Internal testing → Create new release."
