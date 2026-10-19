"""Single compiled app packaging without CAD sources or toolchain."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('builder', Path(__file__).with_name('build-site.py'))
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)
SOURCE_SHA = 'a' * 40


class SiteTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name)
        self.dist = self.repo / 'dist'
        (self.dist / 'assets').mkdir(parents=True)
        (self.dist / 'index.html').write_text('<html>compiled app</html>', encoding='utf-8')
        (self.dist / 'assets/app-123.js').write_text('export {}', encoding='utf-8')
        self.destination = self.repo / 'release'

    def build(self):
        with patch.object(builder.subprocess, 'check_output', side_effect=[SOURCE_SHA, '']):
            builder.build(self.repo, self.destination, self.dist)

    def test_dist_only_without_mechanical_or_legacy_sources(self):
        self.build()
        self.assertEqual((self.destination / 'index.html').read_bytes(), (self.dist / 'index.html').read_bytes())
        self.assertEqual({'assets', 'index.html', 'site-manifest.json'}, {p.name for p in self.destination.iterdir()})
        manifest = json.loads((self.destination / 'site-manifest.json').read_text())
        self.assertEqual(SOURCE_SHA, manifest['applicationSourceRevision'])
        self.assertFalse(manifest['applicationSourceDirty'])
        self.assertEqual('independent', manifest['modelRelease'])
        self.assertNotIn('cad_result', manifest)
        for name, checksum in manifest['sha256'].items():
            self.assertEqual(checksum, builder.digest(self.destination / name))

    def test_existing_destination_is_never_overwritten(self):
        self.destination.mkdir()
        with self.assertRaisesRegex(ValueError, 'destination must be new'):
            self.build()

    def test_missing_entry_rejected_before_output_exists(self):
        (self.dist / 'index.html').unlink()
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.destination.exists())

    def test_private_material_and_duplicate_shells_rejected(self):
        for name in ('.env', 'owner.key', 'identity.sqlite', 'workshop/index.html'):
            with self.subTest(name=name):
                path = self.dist / name
                path.parent.mkdir(exist_ok=True)
                path.write_text('do not publish', encoding='utf-8')
                with self.assertRaises(ValueError):
                    self.build()
                self.assertFalse(self.destination.exists())
                path.unlink()

    def test_recovery_build_requires_the_same_compiled_application(self):
        result = subprocess.run([sys.executable, str(Path(builder.__file__)),
                                 '--destination', str(self.destination)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn('--web-dist', result.stderr)
        self.assertFalse(self.destination.exists())


if __name__ == '__main__':
    unittest.main()
