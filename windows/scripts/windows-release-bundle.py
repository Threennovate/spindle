"""Package a Windows release's new public files and publish them on the server, each update feed last.

create DOWNLOADS BUNDLE TAG COMMIT PREFIX | verify BUNDLE | publish BUNDLE DOWNLOADS

DOWNLOADS holds what release-windows.sh made: the installers at the top (Spindle-Setup.exe for x64,
Spindle-Setup-arm64.exe) and, under spindle-windows/, each architecture's Velopack feed (releases.win-x64.json,
releases.win-arm64.json) with the version's full and delta packages. A feed also names the previous full package, which
is already on the server and isn't uploaded again; publishing checks that it is there, unchanged.

PREFIX is where the server serves its downloads, e.g. https://app.example.com/downloads/.
"""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tempfile

CHANNELS = {'win-x64': 'Spindle-Setup.exe', 'win-arm64': 'Spindle-Setup-arm64.exe'}
FOLDER = 'spindle-windows'
PACKAGE = re.compile(r'Spindle\.Desktop-(\d+\.\d+\.\d+)-(win-x64|win-arm64)-(full|delta)\.nupkg')


def digest(path, algorithm='sha256'):
    with path.open('rb') as source:
        return hashlib.file_digest(source, algorithm).hexdigest()


def version(text):
    return tuple(int(part) for part in text.split('.'))


def files(folder):
    found = {}
    for path in folder.rglob('*'):
        if path.is_symlink():
            raise ValueError('Release bundles cannot contain symbolic links')
        if path.is_file():
            name = path.relative_to(folder).as_posix()
            setup = name in CHANNELS.values()
            feed = re.fullmatch(rf'{FOLDER}/releases\.(win-x64|win-arm64)\.json', name)
            package = name.startswith(FOLDER + '/') and PACKAGE.fullmatch(name.removeprefix(FOLDER + '/'))
            if not (setup or feed or package):
                raise ValueError(f'Unexpected release file: {name}')
            found[name] = digest(path)
    return found


def feed_assets(path):
    assets = json.loads(path.read_text(encoding='utf-8-sig'))['Assets']
    for asset in assets:
        if not PACKAGE.fullmatch(asset['FileName']) or asset['PackageId'] != 'Spindle.Desktop':
            raise ValueError(f"Unexpected feed entry: {asset['FileName']}")
    return assets


def latest(assets):
    return max((asset for asset in assets if asset['Type'] == 'Full'), key=lambda asset: version(asset['Version']))


def matches(path, asset):
    return path.stat().st_size == asset['Size'] and digest(path).upper() == asset['SHA256'].upper()


def verify(bundle):
    manifest = json.loads((bundle / 'manifest.json').read_text())
    if not re.fullmatch(r'windows-\d+\.\d+\.\d+', manifest['tag']) or not re.fullmatch(r'[0-9a-f]{40}', manifest['commit']):
        raise ValueError('Invalid release identity')
    if not re.fullmatch(r'https://[a-z0-9.-]+/downloads/', manifest['prefix']):
        raise ValueError('Invalid downloads address')
    contents = bundle / 'downloads'
    if files(contents) != manifest['sha256']:
        raise ValueError('Release files differ from the prepared checksums')
    released = manifest['tag'].removeprefix('windows-')
    for channel, setup in CHANNELS.items():
        feed = contents / FOLDER / f'releases.{channel}.json'
        if not feed.is_file() or not (contents / setup).is_file():
            raise ValueError(f'The release has no feed or installer for {channel}')
        assets = feed_assets(feed)
        newest = latest(assets)
        if newest['Version'] != released:
            raise ValueError(f'Latest {channel} version does not match the release tag')
        for asset in assets:
            if f'-{channel}-' not in asset['FileName']:
                raise ValueError(f"{channel} feed names another architecture's package: {asset['FileName']}")
            path = contents / FOLDER / asset['FileName']
            if asset['Version'] == released and not path.is_file():
                raise ValueError(f"The release's package is absent from the bundle: {asset['FileName']}")
            if path.is_file() and not matches(path, asset):
                raise ValueError(f"Package differs from its feed entry: {asset['FileName']}")
    return manifest


def replace(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as output:
        temporary = Path(output.name)
        try:
            with source.open('rb') as input_file:
                shutil.copyfileobj(input_file, output)
            output.flush()
            os.fsync(output.fileno())
            temporary.chmod(0o644)
            os.replace(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)


def publish(bundle, target):
    manifest = verify(bundle)
    contents = bundle / 'downloads'
    released = version(manifest['tag'].removeprefix('windows-'))
    target.mkdir(parents=True, exist_ok=True)
    with (target / '.windows-release.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        feeds = [f'{FOLDER}/releases.{channel}.json' for channel in CHANNELS]
        for name in feeds:
            existing = target / name
            if existing.exists():
                previous = version(latest(feed_assets(existing))['Version'])
                if previous > released:
                    raise ValueError('Refusing to replace a newer release')
                if previous == released and digest(existing) != manifest['sha256'][name]:
                    raise ValueError('Version already published with another feed')
        packages = sorted(name for name in manifest['sha256'] if name.endswith('.nupkg'))
        for name in packages:
            existing = target / name
            if existing.exists() and digest(existing) != manifest['sha256'][name]:
                raise ValueError(f'Refusing to overwrite an existing version: {name}')
        # Earlier packages the feeds still name must be on the server as the feeds describe them.
        for name in feeds:
            for asset in feed_assets(contents / name):
                path = f"{FOLDER}/{asset['FileName']}"
                if path not in manifest['sha256'] and not (target / path).is_file():
                    raise ValueError(f"The feed names a package the server doesn't have: {asset['FileName']}")
                if path not in manifest['sha256'] and not matches(target / path, asset):
                    raise ValueError(f"The server's copy differs from the feed: {asset['FileName']}")
        for name in packages + list(CHANNELS.values()) + feeds:
            replace(contents / name, target / name)
    return manifest


def main():
    mode, *args = sys.argv[1:]
    if mode == 'create':
        source, destination, tag, commit, prefix = args
        bundle = Path(destination)
        bundle.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, bundle / 'downloads')
        manifest = {'tag': tag, 'commit': commit, 'prefix': prefix, 'sha256': files(bundle / 'downloads')}
        (bundle / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        verify(bundle)
    elif mode == 'verify':
        manifest = verify(Path(args[0]))
        print(f"Verified {manifest['tag']}, commit {manifest['commit']}")
    elif mode == 'publish':
        manifest = publish(*map(Path, args))
        print(f"Published {manifest['tag']}")
    else:
        raise ValueError('Expected create, verify or publish')


if __name__ == '__main__':
    main()
