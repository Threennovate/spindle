# Sourced by apple/scripts/publish-mac.sh and windows/scripts/publish-windows.sh: the server they publish to, from the
# git-ignored infra/.env unless the environment already sets it.
#
#   SPINDLE_UPLOAD_SSH=user@host           # or a Host from ~/.ssh/config
#   SPINDLE_DOWNLOADS_PATH=/path/to/downloads
#   SPINDLE_RELEASE_USER=account           # owns the published files
#
# Only these three are read; the rest of infra/.env stays out of the scripts' environment.
release_settings="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/infra/.env"
for key in SPINDLE_UPLOAD_SSH SPINDLE_DOWNLOADS_PATH SPINDLE_RELEASE_USER; do
    if [[ -z "${!key:-}" && -f "$release_settings" ]]; then
        value="$(sed -n "s/^$key=//p" "$release_settings" | tail -1)"
        value="${value%\"}"; value="${value#\"}"; value="${value%\'}"; value="${value#\'}"
        [[ -z "$value" ]] || export "$key=$value"
    fi
done
unset key value release_settings
