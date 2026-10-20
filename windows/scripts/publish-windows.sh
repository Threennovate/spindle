#!/usr/bin/env bash
# Releases the Windows app from this Mac: tests, an installer and update packages for x64 and Arm64 of the version on
# main, their upload to the server's downloads folder with the update feeds last, a check of what the server then
# serves, and the windows-X.Y.Z tag.
#
#   windows/scripts/publish-windows.sh
#
# Run it from an up-to-date main whose version (<Version> in windows/Directory.Build.props) has release notes
# (windows/ReleaseNotes/X.Y.Z.md). It needs .NET (global.json), Python 3, and SSH to the server, as
# apple/scripts/publish-mac.sh does: SPINDLE_UPLOAD_SSH names the host (e.g. from ~/.ssh/config), SPINDLE_DOWNLOADS_PATH
# the downloads folder there, and SPINDLE_RELEASE_USER the account that owns the published files, which a root login
# publishes as. The server needs Python 3.11+ and rsync.
#
# The builds aren't signed yet: Windows shows SmartScreen's "unknown publisher" prompt on the first install. Updates
# that the installed app downloads itself don't show it.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
windows="$root/windows"
upload_ssh="${SPINDLE_UPLOAD_SSH:?Set SPINDLE_UPLOAD_SSH to the SSH host of the server.}"
downloads_path="${SPINDLE_DOWNLOADS_PATH:?Set SPINDLE_DOWNLOADS_PATH to the downloads folder on the server.}"
release_user="${SPINDLE_RELEASE_USER:?Set SPINDLE_RELEASE_USER to the account that owns the published files.}"

# Python from python.org has no certificates of its own until its "Install Certificates" step runs.
if [[ -z "${SSL_CERT_FILE:-}" && -f /etc/ssl/cert.pem ]]; then
    export SSL_CERT_FILE=/etc/ssl/cert.pem
fi

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
property() { sed -n "s:^ *<$1>\(.*\)</$1>\$:\1:p" "$windows/Directory.Build.props" | head -1; }

cd "$root"

step "Checking what is released"
[[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first."
[[ "$(git branch --show-current)" == main ]] || fail "Run this from main."
git fetch --quiet origin main --tags
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail "Your main must match origin/main."
version="$(property Version)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Can't read <Version> from windows/Directory.Build.props."
[[ -s "windows/ReleaseNotes/$version.md" ]] || fail "Add windows/ReleaseNotes/$version.md first."
tag="windows-$version"
! git show-ref --verify --quiet "refs/tags/$tag" || fail "$tag is already tagged, so $version is released. Raise <Version>."
feed="$(property SpindleUpdatesUrl)"
[[ "$feed" =~ ^(https://[a-z0-9.-]+/downloads/)spindle-windows$ ]] || fail "<SpindleUpdatesUrl> must be https://HOST/downloads/spindle-windows."
prefix="${BASH_REMATCH[1]}"
commit="$(git rev-parse HEAD)"
echo "Releasing $tag from ${commit:0:7} to $prefix"
ssh -o ConnectTimeout=15 "$upload_ssh" "test -d '$downloads_path' && { [ \"\$(id -u)\" != 0 ] || id '$release_user' >/dev/null; }" \
    || fail "Can't reach $downloads_path on $upload_ssh over SSH, or the server has no $release_user account."

step "Testing"
bash scripts/check.sh windows

step "Building the installers and update packages"
bash windows/scripts/release-windows.sh

step "Packaging only the new public files"
bundle="$windows/artifacts/release/bundle"
python3 windows/scripts/windows-release-bundle.py create "$windows/artifacts/release/downloads" "$bundle" "$tag" "$commit" "$prefix"

step "Uploading to $upload_ssh and publishing, the feeds last"
stage="$downloads_path/.incoming-$tag-$(date +%s)"
ssh "$upload_ssh" "mkdir -p '$stage'"
rsync -r "$bundle/" "$upload_ssh:$stage/"
rsync windows/scripts/windows-release-bundle.py "$upload_ssh:$stage/publish.py"
ssh "$upload_ssh" "set -e
    if [ \"\$(id -u)\" = 0 ]; then
        chown -R '$release_user' '$stage'
        runuser -u '$release_user' -- python3 '$stage/publish.py' publish '$stage' '$downloads_path'
    else
        python3 '$stage/publish.py' publish '$stage' '$downloads_path'
    fi
    rm -rf '$stage'"

step "Checking what the server now serves"
check="$(mktemp -d)"
for name in spindle-windows/releases.win-x64.json spindle-windows/releases.win-arm64.json Spindle-Setup.exe Spindle-Setup-arm64.exe; do
    curl --fail --silent --show-error --retry 3 "$prefix$name" -o "$check/file"
    cmp "$check/file" "$bundle/downloads/$name"
done
type="$(curl --fail --silent --show-error --head "${prefix}Spindle-Setup.exe" | tr -d '\r' | sed -n 's/^[Cc]ontent-[Tt]ype: //p')"
[[ "$type" == application/vnd.microsoft.portable-executable* ]] \
    || echo "warning: the server sends Spindle-Setup.exe as '$type', so the web shows no Windows download until the backend with Windows downloads is deployed."
rm -rf "$check"

step "Tagging $tag"
git tag -a "$tag" -m "Windows $version"
git push origin "refs/tags/$tag"

printf '\nPublished %s. In an installed older version, Settings → About → Check for updates shows it arrive.\n' "$tag"
