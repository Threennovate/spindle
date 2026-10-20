#!/usr/bin/env bash
# Runs the checks GitHub Actions runs on a pull request, on this Mac, for when Actions can't (e.g. no minutes left).
#
#   scripts/check.sh              what the branch changed since main: backend and web for src/ and tests/, the Mac
#                                 app for apple/
#   scripts/check.sh all          everything
#   scripts/check.sh backend web  only these (backend, web, mac, android)
#
# The backend tests need the local database (scripts/local-db.sh). Prints a summary and fails if any check failed.
set -uo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

parts=("$@")
if [[ ${#parts[@]} -eq 0 ]]; then
  base="$(git merge-base HEAD origin/main 2>/dev/null || echo origin/main)"
  changed="$( (git diff --name-only "$base"; git diff --name-only; git ls-files --others --exclude-standard) | sort -u)"
  grep -qE '^(src/|tests/|Dockerfile|global\.json|Directory\.)' <<<"$changed" && parts+=(backend web)
  grep -q '^windows/' <<<"$changed" && parts+=(windows)
  grep -q '^apple/' <<<"$changed" && parts+=(mac)
  grep -qE '^(android/|openapi/|\.github/workflows/android-)' <<<"$changed" && parts+=(android)
  if [[ ${#parts[@]} -eq 0 ]]; then
    echo "Nothing here needs checking: the branch changes neither src/, tests/, apple/ nor android/."
    exit 0
  fi
elif [[ "${parts[0]}" == "all" ]]; then
  parts=(backend web mac windows android)
fi

results=()
failed=0

step() {
  local name="$1"
  shift
  echo
  echo "==> $name"
  if "$@"; then
    results+=("ok      $name")
  else
    results+=("FAILED  $name")
    failed=1
  fi
}

for part in "${parts[@]}"; do
  case "$part" in
    backend)
      step "Backend tests" dotnet test --project tests/Spindle.Tests -p:SkipWebApp=true -p:OpenApiGenerateDocuments=false
      ;;
    web)
      pushd src/web >/dev/null || exit 1
      # A worktree may share its node_modules with another checkout, which npm ci would wipe.
      [[ -e node_modules ]] || step "Web install" npm ci
      step "Web lint" npm run lint
      step "Web typecheck" npm run typecheck
      step "Web tests" npm test
      step "Web build" npm run build
      popd >/dev/null || exit 1
      ;;
    mac)
      step "Release script tests" python3 -m unittest discover -s apple/scripts -p 'test_*.py'
      # macOS runs them with its own bash 3.2, which reads some quoting differently from newer ones.
      step "Release scripts parse in bash 3.2" sh -c 'for script in apple/scripts/*.sh; do /bin/bash -n "$script" || exit 1; done'
      step "SpindleKit tests" swift test --package-path apple/SpindleKit
      step "Mac app tests" xcodebuild test -quiet -project apple/Spindle.xcodeproj \
        -scheme SpindleMac -destination 'platform=macOS' -onlyUsePackageVersionsFromResolvedFile \
        CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO CODE_SIGN_ENTITLEMENTS= ENABLE_HARDENED_RUNTIME=NO
      step "iPhone app build" xcodebuild build -quiet -project apple/Spindle.xcodeproj \
        -scheme SpindleiOS -destination 'generic/platform=iOS Simulator' -onlyUsePackageVersionsFromResolvedFile \
        CODE_SIGNING_ALLOWED=NO
      ;;
    windows)
      step "Windows release script tests" python3 -m unittest discover -s windows/scripts -p 'test_*.py'
      step "Windows app build" dotnet build windows/Spindle.Windows -c Release
      step "Windows Core checks" dotnet run --project windows/Spindle.Core.Tests -c Release
      step "Windows activity checks" dotnet run --project windows/Spindle.Activity.Tests -c Release
      step "Windows UI checks" dotnet run --project windows/Spindle.Windows.Tests -c Release
      step "Windows platform checks" dotnet run --project windows/scripts/PlatformSmoke -c Release
      ;;
    android)
      step "Android API model generation" python3 android/scripts/generate-models.py --check
      pushd android >/dev/null || exit 1
      step "Android build, tests and lint" ./gradlew :app:assembleDebug :app:testDebugUnitTest :app:lintDebug --console=plain
      popd >/dev/null || exit 1
      ;;
    *)
      echo "Unknown part: $part (use backend, web, mac, windows, android or all)" >&2
      exit 2
      ;;
  esac
done

echo
echo "Checks on $(git rev-parse --short HEAD) ($(git branch --show-current)):"
printf '  %s\n' "${results[@]}"
exit "$failed"
