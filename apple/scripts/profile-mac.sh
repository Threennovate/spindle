#!/usr/bin/env bash
# Measures the Mac app on this Mac's own account and agent logs: builds it as a release is built, with the signposts
# in SpindleKit's Performance.swift, opens it under Instruments while you use it, then sums up what was recorded
# (scripts/profile-summary.py). The recording and its summary stay in build.noindex/profiles, to compare before and
# after a change.
#
#   apple/scripts/profile-mac.sh [seconds] [name]      e.g. apple/scripts/profile-mac.sh 120 before
#
# The build is signed ad hoc rather than with Developer ID, so macOS may ask whether it can use Spindle's sign-in in
# the Keychain (allow it), or the app may ask you to sign in. Updates are off in it. Quit Spindle first.
set -euo pipefail

apple="$(cd "$(dirname "$0")/.." && pwd)"
seconds="${1:-120}"
name="${2:-$(date +%Y%m%d-%H%M%S)}"
profiles="$apple/build.noindex/profiles"
trace="$profiles/$name.trace"
derived="$apple/build.noindex/DerivedData-profile"
app="$derived/Build/Products/Release/Spindle.app"

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

# The Mac app under its name now or before (ThreeTrack), not the iPhone app in a simulator, which has the same name.
running='/(Spindle|ThreeTrack)\.app/Contents/MacOS/(Spindle|ThreeTrack)$'
pgrep -qf "$running" && fail "Spindle is running. Quit it first, so the measured copy has the logs and the menu bar to itself."
[[ -e "$trace" ]] && fail "$trace exists already. Choose another name."
mkdir -p "$profiles"

echo "==> Building Spindle (Release, with signposts)"
xcodebuild build -quiet \
    -project "$apple/Spindle.xcodeproj" -scheme SpindleMac -configuration Release \
    -destination "platform=macOS,arch=$(uname -m)" ONLY_ACTIVE_ARCH=YES \
    -derivedDataPath "$derived" \
    -clonedSourcePackagesDirPath "$apple/build.noindex/DerivedData/SourcePackages" \
    CODE_SIGN_IDENTITY=- CODE_SIGN_ENTITLEMENTS= ENABLE_HARDENED_RUNTIME=NO SPINDLE_UPDATES_PUBLIC_KEY=

cat <<EOF

==> Recording for $seconds seconds. Use the app as you usually do, for example:
    1. Wait a few seconds on the timer with a timer running.
    2. Go to Calendar (week, agent activity on), back to Timer, then to Calendar again.
    3. Drag an entry in the calendar up and down for a few seconds, and let go.
    4. Go to Reports, change the period twice, go to Timer and back to Reports.
    5. Leave the window alone for the rest of the time: the menu bar keeps ticking.
    The recording stops by itself; quitting Spindle stops it sooner.

EOF
# Started here and attached to, because xctrace's --launch looks the app up by its bundle ID and can open another
# build of it, e.g. a Debug one in Xcode's DerivedData.
"$app/Contents/MacOS/Spindle" >/dev/null 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
# Stops early, without failing, when Spindle is quit.
xcrun xctrace record --template 'Time Profiler' --instrument 'Points of Interest' --instrument Hangs \
    --time-limit "${seconds}s" --output "$trace" --attach "$pid" >/dev/null || true
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
[[ -e "$trace" ]] || fail "Instruments saved no recording."

echo "==> Summary (also in $profiles/$name.txt; open the .trace in Instruments for the detail)"
python3 "$apple/scripts/profile-summary.py" "$trace" | tee "$profiles/$name.txt"
