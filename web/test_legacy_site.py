"""Retained legacy publisher/recovery and HTTPS inventory regression coverage."""
import hashlib
import io
import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile


def module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


builder = module('build-site')
publisher = module('publish-site')
reader = module('verify-site')
SOURCE_SHA = 'a' * 40
LOCK_WORKER_SCRIPT = """
import sys
from pathlib import Path
from test_legacy_site import publisher
print('ready', flush=True)
if len(sys.argv) == 2:
    with publisher.publication_lock(Path(sys.argv[1])):
        print('acquired', flush=True)
"""


def payload(root):
    names = ['index.html', 'site.css', 'workshop/index.html', 'workshop/viewer.html',
             'workshop/F2-kouzelny-kufr.zip', 'companion/index.html', 'simulator/index.html',
             'design-system/arcane-workshop.css']
    root.mkdir()
    for name in names:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(name.encode())
    manifest = {'schema': 1, 'source_sha': SOURCE_SHA, 'package': 'F2-kouzelny-kufr.zip',
                'sha256': {name: builder.digest(root / name) for name in names}}
    (root / 'site-manifest.json').write_text(json.dumps(manifest))


class PublicationTests(unittest.TestCase):
    @unittest.skipIf(os.name == 'nt', 'Atomic directory symlink replacement is a Linux deployment contract')
    def test_atomic_publish_retains_previous_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            self.assertEqual(publisher.publish(source, site, '100-1', SOURCE_SHA), '')
            self.assertEqual(publisher.publish(source, site, '101-1', SOURCE_SHA), '100-1')
            self.assertTrue((site / 'current').samefile(site / 'releases/101-1'))
            publisher.restore_activation(site, '101-1', '100-1')
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_first_activation_failure_restores_absent_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            previous = publisher.publish(source, site, '100-1', SOURCE_SHA)
            publisher.restore_activation(site, '100-1', previous)
            self.assertFalse((site / 'current').exists())
            self.assertFalse((site / 'current').is_symlink())
            self.assertTrue((site / 'releases/100-1/index.html').is_file())

    def test_failed_activation_rollback_refuses_an_unrelated_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            publisher.publish(source, site, '100-1', SOURCE_SHA)
            with self.assertRaisesRegex(ValueError, 'Current changed'):
                publisher.restore_activation(site, '101-1', '')
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_wrong_provenance_or_changed_payload_never_switches_current(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            publisher.publish(source, site, '100-1', SOURCE_SHA)
            with self.assertRaises(ValueError):
                publisher.publish(source, site, '101-1', 'b' * 40)
            (source / 'workshop/viewer.html').write_bytes(b'corrupt')
            with self.assertRaises(ValueError):
                publisher.publish(source, site, '101-1', SOURCE_SHA)
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_uninventoried_files_and_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'source'
            payload(source)
            (source / 'obsolete.zip').write_bytes(b'old')
            with self.assertRaises(ValueError):
                publisher.verify(source, SOURCE_SHA)
            (source / 'obsolete.zip').unlink()
            try:
                (source / 'shortcut').symlink_to(source / 'index.html')
            except OSError as error:
                self.skipTest('Host does not permit symlinks: ' + str(error))
            with self.assertRaises(ValueError):
                publisher.verify(source, SOURCE_SHA)

    def test_release_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                publisher.switch(Path(directory), '../outside')

    @unittest.skipIf(os.name == 'nt', 'Multiple atomic directory symlink replacements require Linux')
    def test_retention_protects_active_and_previous_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            for identifier in ('100-1', '101-1', '102-1', '103-1', '104-1'):
                publisher.publish(source, site, identifier, SOURCE_SHA)
            removed = publisher.prune_releases(site, '100-1', retain=3)
            self.assertEqual(set(removed), {'101-1', '102-1'})
            self.assertEqual({path.name for path in (site / 'releases').iterdir()},
                             {'100-1', '103-1', '104-1'})
            self.assertTrue((site / 'current').samefile(site / 'releases/104-1'))
            with self.assertRaisesRegex(ValueError, 'Current changed'):
                publisher.prune_releases(site, expected_release='103-1')
            publisher.restore_activation(site, '104-1', '100-1')
            self.assertTrue((site / 'current').samefile(site / 'releases/100-1'))

    def test_retention_preserves_unrecognized_directories_and_outside_symlinks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, site = root / 'source', root / 'site'
            payload(source)
            site.mkdir()
            publisher.publish(source, site, '100-1', SOURCE_SHA)
            outside = root / 'outside'
            outside.mkdir()
            sentinel = outside / 'keep.txt'
            sentinel.write_text('private unrelated data')
            unknown = site / 'releases/90-1'
            unknown.mkdir()
            (unknown / 'keep.txt').write_text('unrecognized data')
            (site / 'releases/91-1').symlink_to(outside, target_is_directory=True)
            malformed = site / 'releases/92-1'
            malformed.mkdir()
            (malformed / 'site-manifest.json').write_text(json.dumps(
                {'schema': 1, 'source_sha': SOURCE_SHA, 'package': 'F2.zip', 'sha256': []}))
            self.assertEqual(publisher.prune_releases(site), [])
            self.assertTrue(sentinel.is_file())
            self.assertTrue((unknown / 'keep.txt').is_file())
            self.assertTrue((site / 'releases/91-1').is_symlink())
            self.assertTrue((malformed / 'site-manifest.json').is_file())
            with self.assertRaises(ValueError):
                publisher.prune_releases(site, '../outside')
            with self.assertRaises(ValueError):
                publisher.prune_releases(site, retain=1)

    def test_invalid_manifest_shapes_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'source'
            payload(source)
            for manifest in ([], {'schema': 1, 'source_sha': SOURCE_SHA, 'package': 'F2.zip', 'sha256': []}):
                (source / 'site-manifest.json').write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, 'manifest shape'):
                    publisher.verify(source, SOURCE_SHA)

    def test_publication_worker_imports_the_retained_publisher(self):
        # Exercise the exact subprocess startup on every platform, even where
        # the production fcntl lock itself cannot run.
        child = subprocess.run([sys.executable, '-c', LOCK_WORKER_SCRIPT],
                               cwd=Path(__file__).parent, capture_output=True, text=True, timeout=5)
        self.assertEqual(child.returncode, 0, child.stderr)
        self.assertEqual(child.stdout.strip(), 'ready')

    @unittest.skipIf(os.name != 'posix', 'Production CLI publication lock is a Linux contract')
    def test_publication_lock_serializes_mutations_and_rejects_symlink_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            site = Path(directory)
            with publisher.publication_lock(site):
                child = subprocess.Popen([sys.executable, '-c', LOCK_WORKER_SCRIPT, str(site)],
                                         cwd=Path(__file__).parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                try:
                    ready = child.stdout.readline().strip()
                    if ready != 'ready':
                        _, errors = child.communicate(timeout=5)
                        self.fail('Publication lock worker failed before readiness: ' + errors)
                    with self.assertRaises(subprocess.TimeoutExpired):
                        child.communicate(timeout=0.2)
                except BaseException:
                    if child.poll() is None:
                        child.kill()
                    child.communicate()
                    raise
            output, errors = child.communicate(timeout=5)
            self.assertEqual(child.returncode, 0, errors)
            self.assertEqual(output.strip(), 'acquired')
            (site / '.publication.lock').unlink()
            outside = site / 'private.txt'
            outside.write_text('keep')
            (site / '.publication.lock').symlink_to(outside)
            with self.assertRaises(OSError):
                with publisher.publication_lock(site):
                    self.fail('Symlink lock must never be opened')
            self.assertEqual(outside.read_text(), 'keep')




class PublicReadbackTests(unittest.TestCase):
    def test_readback_checks_application_chunks_and_images(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'site'
            payload(root)
            manifest_path = root / 'site-manifest.json'
            manifest = json.loads(manifest_path.read_bytes())
            additions = {'companion/assets/app.js': b'application',
                         'workshop/images/open.png': b'image'}
            for name, data in additions.items():
                manifest['sha256'][name] = hashlib.sha256(data).hexdigest()
            manifest_path.write_text(json.dumps(manifest))
            requested = []

            def response(url, timeout):
                name = url.removeprefix('https://example.test/') or 'index.html'
                requested.append(name)
                data = additions.get(name)
                if name in ('ios/', 'healthz'):
                    data = b'ok'
                elif data is None:
                    data = (root / name).read_bytes()
                stream = io.BytesIO(data)
                stream.status = 200
                return stream

            with patch.object(reader, 'urlopen', side_effect=response):
                reader.verify('https://example.test', root)
            self.assertIn('companion/assets/app.js', requested)
            self.assertIn('workshop/images/open.png', requested)
            additions['companion/assets/app.js'] = b'stale application'
            with patch.object(reader, 'urlopen', side_effect=response):
                with self.assertRaisesRegex(ValueError, 'companion/assets/app.js'):
                    reader.verify('https://example.test', root)



if __name__ == '__main__':
    unittest.main()
