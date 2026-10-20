#!/usr/bin/env bash
# Releases the Mac app from this Mac, the way the Mac release workflow does, for when GitHub Actions can't run: tests,
# a signed and notarized build of the version on main, its upload to the server's downloads folder, a check of what
# the server then serves, and the mac-X.Y.Z tag.
#
#   apple/scripts/publish-mac.sh
#
# Run it from an up-to-date main whose version has release notes (as for a merge that the Mac tag workflow would
# release). It needs, on this Mac:
#   - the Developer ID certificate and the notarization profile "Spindle" (see docs/releasing.md);
#   - the update signing key in the keychain: apple/build.noindex/sparkle-2.10.0/bin/generate_keys -f KEY_FILE;
#   - SSH to the server: SPINDLE_UPLOAD_SSH names the host (e.g. from ~/.ssh/config), SPINDLE_DOWNLOADS_PATH the
#     downloads folder there, and SPINDLE_RELEASE_USER the account the workflow uploads with (MAC_UPLOAD_USER), which a
#     root login publishes as. SSH asks for the key's passphrase each time unless the key is in ssh-agent:
#     ssh-add --apple-use-keychain ~/.ssh/id_ed25519
#
# While Actions work, don't use it: the tag it pushes starts the Mac release workflow, which would build and publish
# the same version again.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
apple="$root/apple"
upload_ssh="${SPINDLE_UPLOAD_SSH:?Set SPINDLE_UPLOAD_SSH to the SSH host of the server.}"
downloads_path="${SPINDLE_DOWNLOADS_PATH:?Set SPINDLE_DOWNLOADS_PATH to the downloads folder on the server.}"
# The account the workflow uploads with. Over a root login, publishing runs as it, so the files are the same as CI's.
release_user="${SPINDLE_RELEASE_USER:?Set SPINDLE_RELEASE_USER to the account the workflow uploads with.}"
sparkle_tools="$apple/build.noindex/sparkle-2.10.0/bin"

# Python from python.org has no certificates of its own until its "Install Certificates" step runs, so its downloads
# fail with CERTIFICATE_VERIFY_FAILED. The ones macOS keeps work for it.
if [[ -z "${SSL_CERT_FILE:-}" && -f /etc/ssl/cert.pem ]]; then
    export SSL_CERT_FILE=/etc/ssl/cert.pem
fi

step() { printf '\n==> %s\n' "$*"; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

cd "$root"

step "Checking what is released"
[[ -z "$(git status --porcelain)" ]] || fail "Commit or stash your changes first."
[[ "$(git branch --show-current)" == main ]] || fail "Run this from main."
git fetch --quiet origin main --tags
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail "Your main must match origin/main."
version="$(sed -n 's/^    MARKETING_VERSION: "\([^"]*\)"/\1/p' apple/project.yml)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Can't read MARKETING_VERSION from apple/project.yml."
[[ -s "apple/ReleaseNotes/$version.md" ]] || fail "Add apple/ReleaseNotes/$version.md first."
tag="mac-$version"
! git show-ref --verify --quiet "refs/tags/$tag" || fail "$tag is already tagged, so $version is released. Raise MARKETING_VERSION."
site="$(sed -n 's/^ *SPINDLE_SERVER_URL: \(https:[^ ]*\)$/\1/p' apple/project.yml | head -1)"
[[ -n "$site" ]] || fail "Can't read the Release SPINDLE_SERVER_URL from apple/project.yml."
# Spindle Cloud's former addresses, whose feed entries the new feed takes over.
read -r -a former <<< "$(sed -n 's/^ *SPINDLE_FORMER_SERVER_URLS: "\{0,1\}\([^"]*\)"\{0,1\}$/\1/p' apple/project.yml | head -1)"
commit="$(git rev-parse HEAD)"
echo "Releasing $tag from ${commit:0:7} to $site"

# The workflow keeps these in GitHub; here they come from this Mac.
public_key="${SPINDLE_UPDATES_PUBLIC_KEY:-$(gh variable get SPARKLE_PUBLIC_KEY --env mac-release)}"
[[ -n "$public_key" ]] || fail "Can't read the update public key (GitHub variable SPARKLE_PUBLIC_KEY of environment mac-release)."
if [[ -x "$sparkle_tools/generate_keys" ]]; then
    [[ "$("$sparkle_tools/generate_keys" -p 2>/dev/null)" == "$public_key" ]] \
        || fail "The keychain has no update signing key, or another one. Import it: $sparkle_tools/generate_keys -f KEY_FILE"
fi
profile="${SPINDLE_MAC_PROVISIONING_PROFILE:-}"
if [[ -z "$profile" ]]; then
    for candidate in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"/*.provisionprofile; do
        plist="$(mktemp)"
        if security cms -D -i "$candidate" > "$plist" 2>/dev/null && python3 apple/scripts/mac-passkey-profile.py "$plist" >/dev/null 2>&1; then
            profile="$candidate"
        fi
        rm -f "$plist"
        [[ -z "$profile" ]] || break
    done
fi
[[ -f "$profile" ]] || fail "No Developer ID profile for the Mac app's passkeys. Set SPINDLE_MAC_PROVISIONING_PROFILE."
ssh -o ConnectTimeout=15 "$upload_ssh" "test -d '$downloads_path' && { [ \"\$(id -u)\" != 0 ] || id '$release_user' >/dev/null; }" \
    || fail "Can't reach $downloads_path on $upload_ssh over SSH, or the server has no $release_user account."

step "Testing"
swift test --package-path apple/SpindleKit
python3 -m unittest discover -s apple/scripts -p 'test_*.py'
xcodebuild test -quiet -project apple/Spindle.xcodeproj -scheme SpindleMac -destination 'platform=macOS' \
    -onlyUsePackageVersionsFromResolvedFile \
    CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO CODE_SIGN_ENTITLEMENTS= ENABLE_HARDENED_RUNTIME=NO

step "Restoring the published update history"
# A fresh folder, as on the workflow's runner, so nothing left over from an earlier release on this Mac goes out.
publish="$apple/build.noindex/publish"
rm -rf "$publish"
downloads="$publish/downloads"
python3 apple/scripts/fetch-mac-history.py "$site" "$downloads/spindle-mac" ${former[@]+"${former[@]}"}

step "Building the signed and notarized release"
SPINDLE_DOWNLOADS="$downloads" \
    SPINDLE_MAC_PROVISIONING_PROFILE="$profile" \
    SPINDLE_UPDATES_PUBLIC_KEY="$public_key" \
    bash apple/scripts/release-mac.sh

step "Packaging only the public release files"
bundle="$publish/bundle"
python3 apple/scripts/mac-release-bundle.py create "$downloads" "$bundle" "$tag" "$commit" "$site/downloads/"

step "Uploading to $upload_ssh and publishing, the feed last"
stage="$downloads_path/.incoming-$tag-$(date +%s)"
ssh "$upload_ssh" "mkdir -p '$stage'"
rsync -r "$bundle/" "$upload_ssh:$stage/"
rsync apple/scripts/mac-release-bundle.py "$upload_ssh:$stage/publish.py"
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
curl --fail --silent --show-error --retry 3 "$site/downloads/spindle-mac/appcast.xml" -o "$check/appcast.xml"
cmp "$check/appcast.xml" "$bundle/downloads/spindle-mac/appcast.xml"
curl --fail --silent --show-error --retry 3 "$site/downloads/Spindle.dmg" -o "$check/Spindle.dmg"
cmp "$check/Spindle.dmg" "$bundle/downloads/Spindle.dmg"
rm -rf "$check"

step "Tagging $tag"
git tag -a "$tag" -m "Mac $version"
git push origin "refs/tags/$tag"

printf '\nPublished %s. In an older version, choose Check for Updates… to see it arrive.\n' "$tag"
