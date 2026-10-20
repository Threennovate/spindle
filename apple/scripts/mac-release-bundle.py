"""Package public downloads and publish the exact tested bundle, feed last.

create DOWNLOADS BUNDLE TAG COMMIT [PREFIX] | verify BUNDLE | publish BUNDLE DOWNLOADS

PREFIX is where the server serves its downloads, e.g. https://app.example.com/downloads/; the manifest keeps it, so
publishing on the server checks the feed against the address it was made for.
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
import xml.etree.ElementTree as ET

SPARKLE = '{http://www.andymatuschak.org/xml-namespaces/sparkle}'
# For bundles made before the manifest named its address.
PREFIX = 'https://app.spindletrack.com/downloads/'


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def files(folder):
    found = {}
    for path in folder.rglob('*'):
        if path.is_symlink():
            raise ValueError('Release bundles cannot contain symbolic links')
        if path.is_file():
            name = path.relative_to(folder).as_posix()
            if not re.fullmatch(r'(Spindle\.dmg|spindle-mac/[A-Za-z0-9_.-]+\.(dmg|delta|xml|md|html))', name):
                raise ValueError(f'Unexpected release file: {name}')
            found[name] = digest(path)
    return found


def verify(bundle):
    manifest = json.loads((bundle / 'manifest.json').read_text())
    if not re.fullmatch(r'mac-\d+\.\d+\.\d+', manifest['tag']) or not re.fullmatch(r'[0-9a-f]{40}', manifest['commit']):
        raise ValueError('Invalid release identity')
    prefix = manifest.get('prefix', PREFIX)
    if not re.fullmatch(r'https://[a-z0-9.-]+/downloads/', prefix):
        raise ValueError('Invalid downloads address')
    contents = bundle / 'downloads'
    if files(contents) != manifest['sha256']:
        raise ValueError('Release files differ from the prepared checksums')
    feed = ET.parse(contents / 'spindle-mac/appcast.xml')
    items = list(feed.getroot().iter('item'))
    latest = max(items, key=lambda item: int(item.findtext(SPARKLE + 'version')))
    version = manifest['tag'].removeprefix('mac-')
    if latest.findtext(SPARKLE + 'shortVersionString') != version:
        raise ValueError('Latest feed version does not match the release tag')
    for enclosure in feed.getroot().iter('enclosure'):
        url = enclosure.get('url', '')
        if not url.startswith(prefix):
            raise ValueError('Feed points at a different server')
        name = url.removeprefix(prefix)
        if name not in manifest['sha256']:
            raise ValueError(f'Feed archive is absent from the bundle: {name}')
        if (contents / name).stat().st_size != int(enclosure.get('length')) or not enclosure.get(SPARKLE + 'edSignature'):
            raise ValueError('Invalid archive length or missing signature')
    archive = contents / latest.find('enclosure').get('url').removeprefix(prefix)
    if digest(archive) != digest(contents / 'Spindle.dmg'):
        raise ValueError('First-install download differs from the latest update')
    return manifest, int(latest.findtext(SPARKLE + 'version'))


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


def main():
    mode, *args = sys.argv[1:]
    if mode == 'create':
        source, destination, tag, commit, *rest = args
        bundle = Path(destination)
        bundle.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, bundle / 'downloads')
        manifest = {'tag': tag, 'commit': commit, 'prefix': rest[0] if rest else PREFIX, 'sha256': files(bundle / 'downloads')}
        (bundle / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        verify(bundle)
    elif mode == 'verify':
        manifest, build = verify(Path(args[0]))
        print(f"Verified {manifest['tag']} ({build}), commit {manifest['commit']}")
    elif mode == 'publish':
        bundle, target = map(Path, args)
        manifest, build = verify(bundle)
        target.mkdir(parents=True, exist_ok=True)
        with (target / '.mac-release.lock').open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            feed = target / 'spindle-mac/appcast.xml'
            if feed.exists():
                previous = max(int(item.findtext(SPARKLE + 'version')) for item in ET.parse(feed).getroot().iter('item'))
                if previous > build:
                    raise ValueError('Refusing to replace a newer release')
                if previous == build and digest(feed) != manifest['sha256']['spindle-mac/appcast.xml']:
                    raise ValueError('Build number already published with another feed')
            names = sorted(set(manifest['sha256']) - {'Spindle.dmg', 'spindle-mac/appcast.xml'})
            for name in names:
                existing = target / name
                if existing.exists() and digest(existing) != manifest['sha256'][name]:
                    raise ValueError(f'Refusing to overwrite an existing version: {name}')
            for name in names + ['Spindle.dmg', 'spindle-mac/appcast.xml']:
                replace(bundle / 'downloads' / name, target / name)
        print(f"Published {manifest['tag']} ({build})")
    else:
        raise ValueError('Expected create, verify or publish')


if __name__ == '__main__':
    main()
