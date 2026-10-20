#!/usr/bin/env bash
set -euo pipefail
windows_root="$(cd "$(dirname "$0")/.." && pwd)"
for runtime in win-x64 win-arm64; do
    output="$windows_root/artifacts/$runtime"
    mkdir -p "$output"
    dotnet publish "$windows_root/Spindle.Windows/Spindle.Windows.csproj" -c Release -r "$runtime" --self-contained true --disable-build-servers -m:1 -p:PublishSingleFile=false -o "$output"
    cp "$windows_root/scripts/package-README.txt" "$output/README.txt"
    python3 - "$output" "$runtime" <<'PY'
import pathlib, sys, zipfile
folder = pathlib.Path(sys.argv[1])
architecture = sys.argv[2].removeprefix('win-')
required = ['Spindle.exe', 'coreclr.dll', 'hostfxr.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'README.txt']
for name in required:
    if not (folder / name).is_file():
        raise SystemExit(f'Missing Windows runtime file: {name}')
archive = folder.parent / f'Spindle-Windows-{architecture}.zip'
with zipfile.ZipFile(archive, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as result:
    for file in sorted(folder.rglob('*')):
        if file.is_file():
            result.write(file, file.relative_to(folder))
with zipfile.ZipFile(archive) as result:
    failed = result.testzip()
    if failed:
        raise SystemExit(f'Corrupt ZIP entry: {failed}')
print(f'Created {archive} ({archive.stat().st_size:,} bytes)')
PY
done
