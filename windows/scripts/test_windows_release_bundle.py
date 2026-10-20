import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('bundle', Path(__file__).with_name('windows-release-bundle.py'))
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

PREFIX = 'https://app.spindletrack.com/downloads/'


def asset(name, version, kind, content):
    return {'PackageId': 'Spindle.Desktop', 'Version': version, 'Type': kind, 'FileName': name, 'Size': len(content),
            'SHA256': hashlib.sha256(content).hexdigest().upper(), 'SHA1': hashlib.sha1(content).hexdigest().upper()}


class WindowsReleaseBundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.target = self.root / 'published'

    def make_bundle(self, version, previous=None, setup=b'setup'):
        """A release as release-windows.sh leaves it: new packages, feeds naming the previous full package too."""
        made = len(list(self.root.iterdir()))
        downloads = self.root / f'downloads-{version}-{made}'
        folder = downloads / 'spindle-windows'
        folder.mkdir(parents=True)
        for channel, installer in bundle.CHANNELS.items():
            (downloads / installer).write_bytes(setup + channel.encode())
            assets = []
            full = f'Spindle.Desktop-{version}-{channel}-full.nupkg'
            (folder / full).write_bytes(f'full {version} {channel}'.encode() + setup.removeprefix(b'setup'))
            assets.append(asset(full, version, 'Full', (folder / full).read_bytes()))
            if previous:
                delta = f'Spindle.Desktop-{version}-{channel}-delta.nupkg'
                (folder / delta).write_bytes(f'delta {version} {channel}'.encode())
                assets.append(asset(delta, version, 'Delta', (folder / delta).read_bytes()))
                earlier = f'Spindle.Desktop-{previous}-{channel}-full.nupkg'
                assets.append(asset(earlier, previous, 'Full', f'full {previous} {channel}'.encode()))
            (folder / f'releases.{channel}.json').write_text(json.dumps({'Assets': assets}))
        destination = self.root / f'bundle-{version}-{made}'
        with patch('sys.argv', ['bundle', 'create', str(downloads), str(destination), f'windows-{version}', 'a' * 40, PREFIX]):
            bundle.main()
        return destination

    def publish(self, source):
        with patch('sys.argv', ['bundle', 'publish', str(source), str(self.target)]):
            bundle.main()

    def test_publishes_a_first_release_and_a_retry(self):
        (self.target / 'ios').mkdir(parents=True)
        (self.target / 'ios/app.ipa').write_bytes(b'ios')
        first = self.make_bundle('1.0.0')
        self.publish(first)
        self.publish(first)
        self.assertEqual((self.target / 'Spindle-Setup.exe').read_bytes(), b'setupwin-x64')
        self.assertTrue((self.target / 'spindle-windows/releases.win-arm64.json').is_file())
        self.assertEqual((self.target / 'ios/app.ipa').read_bytes(), b'ios')

    def test_a_new_version_keeps_the_earlier_package_its_delta_starts_from(self):
        self.publish(self.make_bundle('1.0.0'))
        self.publish(self.make_bundle('1.1.0', previous='1.0.0'))
        feed = json.loads((self.target / 'spindle-windows/releases.win-x64.json').read_text())
        self.assertEqual({a['Version'] for a in feed['Assets']}, {'1.0.0', '1.1.0'})
        self.assertTrue((self.target / 'spindle-windows/Spindle.Desktop-1.0.0-win-x64-full.nupkg').is_file())

    def test_refuses_an_older_version(self):
        self.publish(self.make_bundle('1.0.0'))
        self.publish(self.make_bundle('1.1.0', previous='1.0.0'))
        with self.assertRaisesRegex(ValueError, 'newer release'):
            self.publish(self.make_bundle('1.0.0'))

    def test_refuses_the_same_version_built_again(self):
        self.publish(self.make_bundle('1.0.0'))
        with self.assertRaisesRegex(ValueError, 'another feed'):
            self.publish(self.make_bundle('1.0.0', setup=b'rebuilt'))

    def test_refuses_a_feed_naming_a_package_the_server_lacks(self):
        with self.assertRaisesRegex(ValueError, "doesn't have"):
            self.publish(self.make_bundle('1.1.0', previous='1.0.0'))
        self.assertFalse((self.target / 'spindle-windows/releases.win-x64.json').exists())

    def test_refuses_an_earlier_package_that_differs_from_the_feed(self):
        self.publish(self.make_bundle('1.0.0'))
        (self.target / 'spindle-windows/Spindle.Desktop-1.0.0-win-x64-full.nupkg').write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'differs from the feed'):
            self.publish(self.make_bundle('1.1.0', previous='1.0.0'))

    def test_a_changed_file_fails_verification(self):
        prepared = self.make_bundle('1.0.0')
        (prepared / 'downloads/Spindle-Setup.exe').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'checksums'):
            self.publish(prepared)
        self.assertFalse(self.target.joinpath('Spindle-Setup.exe').exists())

    def test_refuses_unexpected_files(self):
        downloads = self.root / 'unexpected'
        (downloads / 'spindle-windows').mkdir(parents=True)
        (downloads / 'spindle-windows/RELEASES-win-x64').write_text('legacy')
        with self.assertRaisesRegex(ValueError, 'Unexpected release file'):
            bundle.files(downloads)

    def test_feed_version_must_match_the_tag(self):
        prepared = self.make_bundle('1.0.0')
        manifest = json.loads((prepared / 'manifest.json').read_text())
        manifest['tag'] = 'windows-1.0.1'
        (prepared / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, 'release tag'):
            self.publish(prepared)


if __name__ == '__main__':
    unittest.main()
