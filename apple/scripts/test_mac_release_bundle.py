import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('bundle', Path(__file__).with_name('mac-release-bundle.py'))
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


class ReleaseBundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.prepared = self.make_bundle('prepared', 40, '1.1.0')
        self.target = self.root / 'published'

    def make_bundle(self, name, build, version, prefix=None):
        folder = self.root / name
        downloads = folder / 'downloads'
        (downloads / 'spindle-mac').mkdir(parents=True)
        archive = f'spindle-mac/Spindle-{version}.dmg'
        (downloads / archive).write_bytes(b'disk image')
        (downloads / 'Spindle.dmg').write_bytes(b'disk image')
        (downloads / 'spindle-mac/appcast.xml').write_text(f'''<rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel><item>
          <sparkle:version>{build}</sparkle:version><sparkle:shortVersionString>{version}</sparkle:shortVersionString>
          <enclosure url="{prefix or bundle.PREFIX}{archive}" length="10" sparkle:edSignature="fixture" />
        </item></channel></rss>''')
        manifest = {'tag': f'mac-{version}', 'commit': 'a' * 40, 'sha256': bundle.files(downloads)}
        if prefix:
            manifest['prefix'] = prefix
        (folder / 'manifest.json').write_text(json.dumps(manifest))
        return folder

    def test_a_bundle_for_another_address_is_checked_against_it(self):
        moved = self.make_bundle('moved', 41, '1.2.0', 'https://app.example.com/downloads/')
        self.publish(moved)
        self.assertEqual((self.target / 'Spindle.dmg').read_bytes(), b'disk image')

    def test_feed_must_match_the_bundles_address(self):
        moved = self.make_bundle('mismatch', 41, '1.2.0', 'https://app.example.com/downloads/')
        manifest = json.loads((moved / 'manifest.json').read_text())
        manifest['prefix'] = 'https://other.example.com/downloads/'
        (moved / 'manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            self.publish(moved)

    def publish(self, source):
        with patch('sys.argv', ['bundle', 'publish', str(source), str(self.target)]):
            bundle.main()

    def test_publish_and_retry_preserve_other_downloads(self):
        (self.target / 'ios').mkdir(parents=True)
        (self.target / 'ios/app.ipa').write_bytes(b'ios')
        self.publish(self.prepared)
        self.publish(self.prepared)
        self.assertEqual((self.target / 'Spindle.dmg').read_bytes(), b'disk image')
        self.assertEqual((self.target / 'ios/app.ipa').read_bytes(), b'ios')

    def test_modified_artifact_is_rejected(self):
        (self.prepared / 'downloads/Spindle.dmg').write_bytes(b'tampered')
        with self.assertRaises(ValueError):
            self.publish(self.prepared)
        self.assertFalse(self.target.exists())

    def test_downgrade_is_rejected(self):
        self.publish(self.prepared)
        with self.assertRaises(ValueError):
            self.publish(self.make_bundle('older', 39, '1.0.0'))

    def test_versioned_archive_cannot_be_overwritten(self):
        (self.target / 'spindle-mac').mkdir(parents=True)
        (self.target / 'spindle-mac/Spindle-1.1.0.dmg').write_bytes(b'other release')
        with self.assertRaises(ValueError):
            self.publish(self.prepared)
        self.assertFalse((self.target / 'spindle-mac/appcast.xml').exists())

    def test_partial_upload_does_not_publish_feed(self):
        original = bundle.replace

        def fail_latest(source, target):
            if target.name == 'Spindle.dmg':
                raise OSError('simulated disk full')
            original(source, target)

        with patch.object(bundle, 'replace', side_effect=fail_latest), self.assertRaises(OSError):
            self.publish(self.prepared)
        self.assertFalse((self.target / 'spindle-mac/appcast.xml').exists())

    def test_symlink_and_private_files_are_rejected(self):
        downloads = self.prepared / 'downloads'
        (downloads / 'private.key').write_text('secret')
        with self.assertRaises(ValueError):
            bundle.verify(self.prepared)
        (downloads / 'private.key').unlink()
        (downloads / 'linked.dmg').symlink_to(downloads / 'Spindle.dmg')
        with self.assertRaises(ValueError):
            bundle.verify(self.prepared)


if __name__ == '__main__':
    unittest.main()
