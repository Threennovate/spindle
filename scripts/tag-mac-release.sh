#!/usr/bin/env bash
# Push a release tag for the already committed version on main. This publishes the Mac app.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
[[ $# == 1 && "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Usage: $0 X.Y.Z"
[[ -z "$(git status --porcelain)" ]] || fail 'Commit or stash changes first.'
[[ "$(git branch --show-current)" == main ]] || fail 'Run this from main.'
git fetch origin main --tags
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail 'Your main must match origin/main.'
version=$(sed -n 's/^    MARKETING_VERSION: "\([^"]*\)"/\1/p' apple/project.yml)
[[ "$1" == "$version" ]] || fail "The committed project version is $version, not $1."
[[ -s "apple/ReleaseNotes/$version.md" ]] || fail 'Add release notes before tagging.'
tag="mac-$version"
if git show-ref --verify --quiet "refs/tags/$tag"; then fail "Tag $tag already exists."; fi
printf 'Starting automatic build and publication of %s from %s\n' "$tag" "$(git rev-parse HEAD)"
git tag -a "$tag" -m "Mac $version"
git push origin "refs/tags/$tag"
