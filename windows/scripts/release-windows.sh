#!/usr/bin/env bash
# Builds a Windows release on this Mac (or any machine with .NET): both architectures published self-contained,
# packaged with Velopack into an installer and update packages, with a delta from the version the server has now.
#
#   windows/scripts/release-windows.sh
#
# windows/artifacts/release/downloads gets the files to add to the server's downloads folder:
# Spindle-Setup.exe, Spindle-Setup-arm64.exe and spindle-windows/ with each architecture's feed and packages. The
# version is <Version> in windows/Directory.Build.props, its notes windows/ReleaseNotes/X.Y.Z.md, and the feed it
# extends <SpindleUpdatesUrl>. Set SPINDLE_WINDOWS_FEED to read the current feed from elsewhere, e.g. a local server.
#
# The builds aren't signed yet: Windows shows SmartScreen's "unknown publisher" prompt on the first install.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
windows="$root/windows"
output="$windows/artifacts/release"

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }
property() { sed -n "s:^ *<$1>\(.*\)</$1>\$:\1:p" "$windows/Directory.Build.props" | head -1; }

version="$(property Version)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Can't read <Version> from windows/Directory.Build.props."
notes="$windows/ReleaseNotes/$version.md"
[[ -s "$notes" ]] || fail "Add windows/ReleaseNotes/$version.md first."
feed="${SPINDLE_WINDOWS_FEED:-$(property SpindleUpdatesUrl)}"
[[ -n "$feed" ]] || fail "Can't read <SpindleUpdatesUrl> from windows/Directory.Build.props."

cd "$root"
dotnet tool restore >/dev/null
rm -rf "$output"
mkdir -p "$output/downloads/spindle-windows"

for runtime in win-x64 win-arm64; do
    printf '\n==> %s %s\n' "$runtime" "$version"
    app="$output/app-$runtime"
    packages="$output/packages-$runtime"
    dotnet publish windows/Spindle.Windows/Spindle.Windows.csproj -c Release -r "$runtime" --self-contained true \
        --disable-build-servers -m:1 -p:PublishSingleFile=false -o "$app"
    for required in Spindle.exe coreclr.dll hostfxr.dll libSkiaSharp.dll libHarfBuzzSharp.dll Velopack.dll; do
        [[ -f "$app/$required" ]] || fail "Missing Windows runtime file: $required"
    done

    # The latest published full package, so the new version comes with a delta from it. None before the first release.
    dotnet vpk download http --url "$feed" --channel "$runtime" --allowEmptyChannel -o "$packages"
    previous=("$packages"/*.nupkg)

    # packId names the install folder, %LocalAppData%\Spindle.Desktop; %LocalAppData%\Spindle keeps the app's data,
    # which uninstalling must not remove. Never change it: installed copies would stop finding their updates.
    dotnet vpk '[win]' pack --packId Spindle.Desktop --packVersion "$version" --packDir "$app" --mainExe Spindle.exe \
        --packTitle Spindle --packAuthors Threennovate --icon windows/Spindle.Windows/Assets/spindle.ico \
        --releaseNotes "$notes" --channel "$runtime" --runtime "$runtime" --noPortable --outputDir "$packages"

    # The new packages and the feed. The earlier full package stays where it is on the server.
    for package in "$packages"/Spindle.Desktop-"$version"-"$runtime"-*.nupkg; do
        cp "$package" "$output/downloads/spindle-windows/"
    done
    cp "$packages/releases.$runtime.json" "$output/downloads/spindle-windows/"
    setup="Spindle-Setup.exe"
    [[ "$runtime" == win-x64 ]] || setup="Spindle-Setup-arm64.exe"
    cp "$packages/Spindle.Desktop-$runtime-Setup.exe" "$output/downloads/$setup"
    [[ -e "${previous[0]}" ]] && echo "Delta from $(basename "${previous[0]}")" || echo "First release: no delta"
done

printf '\nBuilt Windows %s in %s/downloads\n' "$version" "$output"
