#!/usr/bin/env bash
# Takes the raw iPhone screenshots for the App Store listing into apple/appstore/screenshots/ (6.9", 1320x2868): a local
# API with a database of its own (SpindleAppStore), the made-up account of android/scripts/play-demo-data.py (the same
# data as the Google Play pictures), and the debug app on an iPhone 17 Pro Max simulator, opened on each screen with its
# launch options (apple/README.md). Needs the local PostgreSQL (scripts/local-db.sh) and Xcode. A dark set goes into
# the dark/ folder. Branded framing is a separate step.
set -euo pipefail

apple="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$apple/.." && pwd)"
out="$apple/appstore/screenshots"
port=5264
bundle=com.spindletrack.iphone
derived="$apple/build.noindex/DerivedData-appstore"
log="$apple/build.noindex/appstore-api.log"

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

# The local PostgreSQL's password, from infra/.env of the main checkout (a worktree has none of its own).
main="$(dirname "$(git -C "$root" rev-parse --path-format=absolute --git-common-dir)")"
password="${POSTGRES_PASSWORD:-$(sed -n 's/^POSTGRES_PASSWORD=//p' "$main/infra/.env" 2>/dev/null)}"
[[ -n "$password" ]] || fail "Set POSTGRES_PASSWORD, or run scripts/local-db.sh first."
mkdir -p "$apple/build.noindex" "$out/dark"
! curl -sf "http://localhost:$port/health" >/dev/null || fail "Something already answers on port $port."

# The iPhone 17 Pro Max simulator (6.9"): SPINDLE_SIM_UDID picks one, otherwise the one on the newest iOS.
udid="${SPINDLE_SIM_UDID:-$(xcrun simctl list devices available -j | python3 -c '
import json, sys
found = [(runtime, d["udid"]) for runtime, ds in json.load(sys.stdin)["devices"].items() for d in ds if d["name"] == "iPhone 17 Pro Max"]
print(max(found)[1] if found else "")')}"
if [[ -z "$udid" ]]; then
  runtime="$(xcrun simctl list runtimes -j | python3 -c 'import json,sys; print([r["identifier"] for r in json.load(sys.stdin)["runtimes"] if r["isAvailable"] and r["platform"] == "iOS"][-1])')"
  udid="$(xcrun simctl create "iPhone 17 Pro Max" com.apple.CoreSimulator.SimDeviceType.iPhone-17-Pro-Max "$runtime")"
fi
xcrun simctl bootstatus "$udid" -b >/dev/null

echo "==> Starting the API on http://localhost:$port with the SpindleAppStore database"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$port" Database__MigrateOnStartup=true \
  ConnectionStrings__Default="Host=localhost;Port=15432;Username=spindle;Password=$password;Database=SpindleAppStore" \
  dotnet run --project "$root/src/Spindle.Api" --no-launch-profile >"$log" 2>&1 &
api_pid=$!
cleanup() {
  kill "$api_pid" 2>/dev/null || true
  xcrun simctl status_bar "$udid" clear >/dev/null 2>&1 || true
  xcrun simctl ui "$udid" appearance light >/dev/null 2>&1 || true
}
trap cleanup EXIT
for _ in $(seq 90); do curl -sf "http://localhost:$port/health" >/dev/null && break; sleep 2; done
curl -sf "http://localhost:$port/health" >/dev/null || fail "The API didn't start; see $log."

echo "==> Creating the made-up account"
# The made-up "now" (16:30 today, as in the Android pictures): the data is built around it and the app is told to run
# from it (SPINDLE_AUTOMATION_NOW), so the timer, the calendar and the status bar agree.
now="$(python3 "$root/android/scripts/play-demo-now.py")"
account="$(python3 "$root/android/scripts/play-demo-data.py" "http://localhost:$port" "$now")"
email="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["email"])' <<<"$account")"
user_password="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["password"])' <<<"$account")"

echo "==> Building the debug app for the API on port $port"
(cd "$apple" && xcodebuild build -project Spindle.xcodeproj -scheme SpindleiOS -configuration Debug \
  -derivedDataPath "$derived" -destination "id=$udid" SPINDLE_SERVER_URL="http://localhost:$port" -quiet)
xcrun simctl install "$udid" "$derived/Build/Products/Debug-iphonesimulator/Spindle.app"

# A tidy status bar: the made-up time, full battery and signal.
status_bar() {
  xcrun simctl status_bar "$udid" override --time "$(python3 -c 'import sys; from datetime import datetime; print(datetime.fromisoformat(sys.argv[1]).strftime("%Y-%m-%dT%H:%M:00.000%z"))' "$now")" --operatorName " " --batteryState charged --batteryLevel 100 --cellularBars 4 --wifiBars 3
}
status_bar

shot() {
  local path="$1"; shift
  xcrun simctl terminate "$udid" "$bundle" >/dev/null 2>&1 || true
  status_bar
  # The sign-in stays in memory in an automated run, so each launch starts from the options alone.
  env SIMCTL_CHILD_SPINDLE_AUTOMATION_NOW="$now" SIMCTL_CHILD_SPINDLE_AUTOMATION_EMAIL="$email" SIMCTL_CHILD_SPINDLE_AUTOMATION_PASSWORD="$user_password" "$@" \
    xcrun simctl launch "$udid" "$bundle" >/dev/null
  sleep "${SPINDLE_SHOT_WAIT:-8}"
  xcrun simctl io "$udid" screenshot --type=png "$path" >/dev/null
  echo "wrote ${path#"$root"/}"
}
# The Lock Screen with the timer's Live Activity. Nothing in a script can lock the simulator (Xcode has no Simulator app to
# send Cmd-L to, and simctl has no lock command), so this asks you to: press the lock button in the simulator window or
# panel, then Return. The first time on a simulator, tap Allow on "Allow Live Activities from Spindle?". Without a
# terminal (or with SPINDLE_SKIP_LOCK_SCREEN=1) the picture is left as it was.
lock_screen() {
  local path="$1"
  if [[ -n "${SPINDLE_SKIP_LOCK_SCREEN:-}" || ! -t 0 ]]; then echo "skipped ${path#"$root"/} (needs a person to lock the simulator)"; return; fi
  shot "$path" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=timer
  # SPINDLE_LOCK_WAIT=<seconds> waits that long instead of for Return, for a person (or tool) that locks it meanwhile.
  if [[ -n "${SPINDLE_LOCK_WAIT:-}" ]]; then
    echo "Lock the simulator now (allow Live Activities if it asks); taking the picture in $SPINDLE_LOCK_WAIT seconds."
    sleep "$SPINDLE_LOCK_WAIT"
  else
    read -rp "Lock the simulator (allow Live Activities if it asks), wait for the timer to show, then press Return: "
  fi
  xcrun simctl io "$udid" screenshot --type=png "$path" >/dev/null
  echo "wrote ${path#"$root"/}"
}
set_of() {
  local dir="$1"
  shot "$dir/1-timer.png" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=timer
  shot "$dir/2-calendar.png" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=calendar
  [[ "$dir" == "$out" ]] && lock_screen "$dir/3-lock-screen.png"
  shot "$dir/4-reports.png" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=reports SIMCTL_CHILD_SPINDLE_AUTOMATION_REPORT=summary SIMCTL_CHILD_SPINDLE_AUTOMATION_SCROLL=breakdown
  shot "$dir/5-projects.png" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=projects
}
xcrun simctl ui "$udid" appearance light >/dev/null
# A first launch after the install can still sit on the sign-in screen, so it is not the first picture.
shot "$(mktemp -d)/warm-up.png" SIMCTL_CHILD_SPINDLE_AUTOMATION_TAB=timer >/dev/null
set_of "$out"
xcrun simctl ui "$udid" appearance dark >/dev/null
set_of "$out/dark"
