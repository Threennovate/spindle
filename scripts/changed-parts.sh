#!/usr/bin/env bash
# Which checks a change needs, one per line (backend, web, windows, mac, android), from the changed paths on stdin.
# scripts/check.sh and the "Web and backend" workflow both use it, so a branch runs the same checks here and in CI.
#
#   git diff --name-only origin/main | scripts/changed-parts.sh
set -uo pipefail

changed="$(cat)"
has() { grep -qE "$1" <<<"$changed"; }

# The web app has its own checks; the backend tests build without it (-p:SkipWebApp=true).
grep -vE '^src/web/' <<<"$changed" |
  grep -qE '^(src/|tests/|Dockerfile$|global\.json$|Directory\.|\.github/workflows/web-ci\.yml$|scripts/changed-parts\.sh$)' && echo backend
# The web tests read the shared vectors in tests/vectors too.
has '^(src/web/|tests/vectors/|Dockerfile$|\.github/workflows/web-ci\.yml$|scripts/changed-parts\.sh$)' && echo web
has '^windows/' && echo windows
has '^apple/' && echo mac
has '^(android/|openapi/|\.github/workflows/android-)' && echo android
exit 0
