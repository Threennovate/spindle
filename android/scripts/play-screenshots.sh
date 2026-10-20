#!/usr/bin/env bash
# Takes the phone screenshots for the Google Play store listing into android/play/screenshots/: a local API with a
# database of its own (SpindlePlay), a made-up account from play-demo-data.py, and the debug app on the running
# emulator, opened on each screen with its launch options (android/README.md). Needs the local PostgreSQL
# (scripts/local-db.sh) and one emulator or device in adb.
set -euo pipefail

android="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$android/.." && pwd)"
out="$android/play/screenshots"
port=5262
adb="${ANDROID_HOME:-$HOME/Library/Android/sdk}/platform-tools/adb"

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

# The local PostgreSQL's password, from infra/.env of the main checkout (a worktree has none of its own).
main="$(dirname "$(git -C "$root" rev-parse --path-format=absolute --git-common-dir)")"
password="${POSTGRES_PASSWORD:-$(sed -n 's/^POSTGRES_PASSWORD=//p' "$main/infra/.env" 2>/dev/null)}"
[[ -n "$password" ]] || fail "Set POSTGRES_PASSWORD, or run scripts/local-db.sh first."
# ANDROID_SERIAL picks one of several (adb and Gradle both read it), e.g. the emulator while a phone is plugged in.
[[ -n "${ANDROID_SERIAL:-}" || "$("$adb" devices | grep -c 'device$')" == 1 ]] || fail "Start one emulator (or connect one device), or set ANDROID_SERIAL."

mkdir -p "$android/build"
echo "==> Starting the API on http://localhost:$port with the SpindlePlay database"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://localhost:$port" Database__MigrateOnStartup=true \
  ConnectionStrings__Default="Host=localhost;Port=15432;Username=spindle;Password=$password;Database=SpindlePlay" \
  dotnet run --project "$root/src/Spindle.Api" --no-launch-profile >"$android/build/play-api.log" 2>&1 &
api_pid=$!
night="$("$adb" shell cmd uimode night | grep -q 'yes' && echo yes || echo no)"
restore() {
  kill "$api_pid" 2>/dev/null
  "$adb" shell settings put global sysui_demo_allowed 0 >/dev/null 2>&1 || true
  "$adb" shell cmd uimode night "$night" >/dev/null 2>&1 || true
  "$adb" shell settings put global auto_time 1 >/dev/null 2>&1 || true
  "$adb" shell "date @$(date +%s)" >/dev/null 2>&1 || true
}
trap restore EXIT

# A made-up "now" of 16:30 today (or the real time, if that is earlier), so the day on the calendar is a normal working
# day; the emulator's clock is set to it below, so the running timer's elapsed time agrees.
now="$(python3 "$android/scripts/play-demo-now.py")"
for _ in $(seq 90); do curl -sf "http://localhost:$port/health" >/dev/null && break; sleep 2; done
curl -sf "http://localhost:$port/health" >/dev/null || fail "The API didn't start; see android/build/play-api.log."

echo "==> Creating the made-up account"
account="$(python3 "$android/scripts/play-demo-data.py" "http://localhost:$port" "$now")"
email="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["email"])' <<<"$account")"
user_password="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["password"])' <<<"$account")"

echo "==> Installing the debug app"
(cd "$android" && ./gradlew :app:installDebug --console=plain --quiet)

# Light, with the made-up time on the emulator's clock (it needs root; the clock is put back on exit).
"$adb" root >/dev/null; "$adb" wait-for-device
"$adb" shell cmd uimode night no >/dev/null
"$adb" shell settings put global auto_time 0
"$adb" shell settings put global auto_time_zone 0
"$adb" shell "date @$(python3 -c 'import sys; from datetime import datetime; print(int(datetime.fromisoformat(sys.argv[1]).timestamp()))' "$now")" >/dev/null

# A tidy status bar: the made-up time (the calendar's now line shows the same), full battery and signal, no notification icons (the app's times follow the clock above).
"$adb" shell settings put global sysui_demo_allowed 1
"$adb" shell settings put system time_12_24 24 # the app shows 24-hour times, so the status bar does too
demo() { "$adb" shell am broadcast -a com.android.systemui.demo -e command "$@" >/dev/null; }
demo enter # first: the other commands only count in demo mode
now_hhmm="$(python3 "$android/scripts/play-demo-now.py" --hhmm)"
demo clock -e hhmm "$now_hhmm"
demo battery -e level 100 -e plugged false
demo network -e wifi show -e level 4 -e fully true
demo network -e mobile show -e level 4 -e datatype none -e fully true
demo notifications -e visible false

# Awake and unlocked, whatever the emulator did while it waited (a sleeping screen is captured black).
wake() {
  "$adb" shell svc power stayon true
  "$adb" shell dumpsys power | grep -q 'mWakefulness=Awake' || "$adb" shell input keyevent KEYCODE_POWER
  "$adb" shell wm dismiss-keyguard
}

mkdir -p "$out"
shot() {
  local name="$1"; shift
  [[ -z "${SPINDLE_SHOTS:-}" || " $SPINDLE_SHOTS " == *" $name "* ]] || return 0 # e.g. SPINDLE_SHOTS="3-split"
  "$adb" shell pm clear com.spindletrack.android.debug >/dev/null
  wake
  # Granted beforehand, so Android's question doesn't cover the screen when the running timer asks for it.
  "$adb" shell pm grant com.spindletrack.android.debug android.permission.POST_NOTIFICATIONS
  "$adb" shell am start -n com.spindletrack.android.debug/com.spindletrack.android.MainActivity \
    --es server "http://10.0.2.2:$port" --es email "$email" --es password "'$user_password'" "$@" >/dev/null
  sleep "${SPINDLE_SHOT_WAIT:-10}"
  [[ -z "${before_shot:-}" ]] || "$before_shot"
  "$adb" exec-out screencap -p >"$out/$name.png"
  echo "wrote android/play/screenshots/$name.png"
}
scroll() { "$adb" shell input swipe 540 "$1" 540 "$2" 800; sleep 2; }
scroll_report() { scroll 2100 740; }    # the breakdown's donut in full, with its legend below

shot 1-timer --es tab timer
shot 2-calendar --es tab calendar
shot 3-split --es tab timer --es edit first # the latest finished entry: the split one, in the editor
before_shot=scroll_report shot 4-reports --es tab reports --es report summary
shot 5-projects --es tab projects

demo exit
