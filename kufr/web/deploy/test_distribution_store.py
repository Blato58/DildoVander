"""Synthetic frozen archive publication; no real CAD/toolchain or deployment."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

import distribution_store as distribution
import release_store


def digest(data):
    return hashlib.sha256(data).hexdigest()


class DistributionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.downloads = self.root / 'downloads'
        self.metadata = {'kind': 'manufacturing', 'modelId': 'synthetic-catalog', 'modelAssetSha256': None,
                         'label': 'Synthetic historical archive fixture', 'version': 'F2'}

    def archive(self, name, source_text=b'synthetic original source'):
        source = self.root / name
        source.mkdir()
        sources = {'mechanism.py': digest(source_text)}
        manifest = json.dumps({'revision': 'F2', 'sources': sources}).encode()
        viewer = b'<html>frozen legacy viewer fixture inside ZIP only</html>'
        exports = {'manifest.json': digest(manifest), 'viewer.html': digest(viewer)}
        report = json.dumps({'result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'physical_test_performed': False,
                             'sources': sources, 'export_sha256': exports}).encode()
        index = json.dumps({**exports, 'verification.json': digest(report)}).encode()
        archive = source / 'F2-kouzelny-kufr.zip'
        with zipfile.ZipFile(archive, 'w') as package:
            for path, data in {'mechanism.py': source_text, 'exports/manifest.json': manifest, 'exports/viewer.html': viewer,
                               'exports/verification.json': report, 'exports/artifact-sha256.json': index}.items():
                package.writestr(path, data)
        for path, data in {'manifest.json': manifest, 'verification.json': report, 'artifact-sha256.json': index}.items():
            (source / path).write_bytes(data)
        (source / 'package-sha256.txt').write_text(distribution.sha256(archive) + '  ' + archive.name + '\n')
        return source, archive.name

    def test_old_zip_url_survives_independent_app_activation(self):
        source, name = self.archive('first')
        value = distribution.publish(source, name, self.downloads, self.metadata)
        legacy = self.downloads / 'legacy/workshop' / name
        self.assertEqual((source / name).read_bytes(), legacy.read_bytes())
        app = self.root / 'compiled-app'
        (app / 'assets').mkdir(parents=True)
        (app / 'assets/app-12345678.js').write_text('synthetic compiled application')
        (app / 'index.html').write_text('<html>synthetic application</html>')
        manifest = {'schemaVersion': 2, 'artifactType': 'application', 'applicationSourceRevision': 'a' * 40,
                    'applicationSourceDirty': False, 'apiContractVersion': 1, 'sha256': release_store.inventory(app)}
        (app / 'site-manifest.json').write_text(json.dumps(manifest))
        try:
            release_store.publish_application(app, self.root / 'application-host')
        except OSError as error:
            if getattr(error, 'winerror', None) == 1314:
                self.skipTest('OS disallows first application symlink; Linux CI runs activation')
            raise
        self.assertEqual(value['sha256'], distribution.sha256(legacy))
        self.assertEqual(value, json.loads((self.downloads / 'index.json').read_text())['artifacts'][0])
        self.assertEqual(value, distribution.publish(source, name, self.downloads, self.metadata))

    def test_new_archive_never_rewrites_the_old_url_or_bytes(self):
        source, name = self.archive('first')
        first = distribution.publish(source, name, self.downloads, self.metadata)
        changed, _ = self.archive('second', b'changed synthetic source')
        previous = (self.downloads / 'index.json').read_bytes()
        with self.assertRaisesRegex(ValueError, 'legacy URL is immutable'):
            distribution.publish(changed, name, self.downloads, self.metadata)
        self.assertEqual(previous, (self.downloads / 'index.json').read_bytes())
        second = distribution.publish(changed, name, self.downloads, self.metadata, legacy_alias=False)
        self.assertNotEqual(first['sha256'], second['sha256'])
        self.assertEqual(first['sha256'], distribution.sha256(self.downloads / 'legacy/workshop' / name))
        restored = distribution.rollback(self.downloads)
        self.assertEqual([first], restored['artifacts'])
        self.assertTrue((self.downloads / second['path']).exists())

    def test_invalid_metadata_paths_and_archive_hashes_are_rejected(self):
        source, name = self.archive('first')
        for metadata in ([], {**self.metadata, 'bytes': 1}, {**self.metadata, 'modelId': '../other'},
                         {**self.metadata, 'modelAssetSha256': True}, {**self.metadata, 'kind': 'script'}):
            with self.subTest(metadata=metadata), self.assertRaises(ValueError):
                distribution.publish(source, name, self.downloads, metadata)
        for path in ('../F2-kouzelny-kufr.zip', 'viewer.html', 'runtime.js', 'drawing.svg', '/etc/private.zip'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                distribution.publish(source, path, self.downloads, self.metadata)
        (source / name).write_bytes(b'tampered frozen ZIP')
        with self.assertRaisesRegex(ValueError, 'checksum differs'):
            distribution.publish(source, name, self.downloads, self.metadata)
        self.assertFalse((self.downloads / 'index.json').exists())

    def test_legacy_archive_cannot_claim_a_new_model_content_binding(self):
        source, name = self.archive('first')
        with self.assertRaisesRegex(ValueError, 'no evidence binding'):
            distribution.publish(source, name, self.downloads, {**self.metadata, 'modelAssetSha256': 'a' * 64})

    @unittest.skipUnless(shutil.which('node'), 'Node required for maintained model validation')
    def test_model_claim_is_checked_against_actual_validated_zip_bundle(self):
        fixture = release_store.REPO / 'kufr/mobilni-aplikace/src/features/workshop/fixture'
        manifest = json.loads((fixture / 'model-bundle.json').read_text())
        # Synthetic test bytes only: exercise production-format checks without
        # claiming that this fixture is an actual CAD-verified release.
        manifest['provenance']['kind'] = 'cad-export'
        identity = {key: value for key, value in manifest.items() if key != 'bundleHash'}
        manifest['bundleHash'] = digest(json.dumps(identity, ensure_ascii=False, sort_keys=True, separators=(',', ':')).encode())
        source = self.root / 'model-input'
        source.mkdir()
        name = 'model.zip'
        model = next(asset for asset in manifest['assets'] if asset['kind'] == 'model-json')
        metadata = {'kind': 'model-archive', 'modelId': manifest['modelId'], 'modelAssetSha256': model['sha256'],
                    'bundleHash': manifest['bundleHash'], 'label': 'Synthetic model contract fixture', 'version': manifest['revision']}
        def write_archive(extra=None, corrupt=False):
            with zipfile.ZipFile(source / name, 'w') as archive:
                archive.writestr('model-bundle.json', json.dumps(manifest))
                for asset in manifest['assets']:
                    archive.writestr(asset['path'], b'{}' if corrupt else (fixture / asset['path']).read_bytes())
                if extra:
                    archive.writestr(extra, b'undeclared')
        write_archive()
        for overrides in ({'modelId': 'different-model'}, {'modelAssetSha256': 'a' * 64}, {'bundleHash': 'b' * 64}):
            with self.subTest(overrides=overrides), self.assertRaisesRegex(ValueError, 'claim differs'):
                distribution.publish(source, name, self.downloads, {**metadata, **overrides})
        self.assertFalse((self.downloads / 'index.json').exists())
        value = distribution.publish(source, name, self.downloads, metadata)
        self.assertEqual(model['sha256'], value['modelAssetSha256'])
        self.assertEqual('not-verified', value['cadVerification'])
        before = (self.downloads / 'index.json').read_bytes()
        for extra, corrupt in (('extra.txt', False), ('../escape.json', False), (None, True)):
            write_archive(extra, corrupt)
            with self.subTest(extra=extra, corrupt=corrupt), self.assertRaises((ValueError, subprocess.CalledProcessError)):
                distribution.publish(source, name, self.downloads, metadata)
            self.assertEqual(before, (self.downloads / 'index.json').read_bytes())
        with zipfile.ZipFile(source / name, 'w') as archive:
            archive.writestr('unrelated.json', '{}')
        with self.assertRaises(subprocess.CalledProcessError):
            distribution.publish(source, name, self.downloads, metadata)
        self.assertEqual(before, (self.downloads / 'index.json').read_bytes())

    def test_offline_zip_cannot_claim_binding_from_a_loose_receipt(self):
        source = self.root / 'offline-input'
        source.mkdir()
        name = 'offline.zip'
        with zipfile.ZipFile(source / name, 'w') as archive:
            archive.writestr('viewer.html', '<html>unrelated model</html>')
            archive.writestr('viewer.html.manifest.json', json.dumps({'modelId': 'claimed', 'bundleHash': 'b' * 64}))
        metadata = {'kind': 'offline-viewer', 'modelId': 'claimed', 'modelAssetSha256': 'a' * 64,
                    'bundleHash': 'b' * 64, 'label': 'Unproven offline bundle', 'version': 'test'}
        with self.assertRaisesRegex(ValueError, 'Bound offline publication is unsupported'):
            distribution.publish(source, name, self.downloads, metadata)
        self.assertFalse((self.downloads / 'index.json').exists())

    def test_download_index_and_payload_have_strict_separate_routing(self):
        config = Path(__file__).with_name('site-locations.conf').read_text()
        self.assertIn('location = /downloads/index.json', config)
        self.assertIn('root /var/lib/dildovander/downloads/legacy;', config)
        self.assertIn('location /downloads/ { return 404; }', config)
        for block in config.split('location ~ ')[1:]:
            self.assertIn('Content-Disposition "attachment"', block)
            self.assertIn('application/octet-stream', block)
            self.assertIn('nosniff', block)
            self.assertNotIn('try_files /index.html', block)
        with self.assertRaises(ValueError):
            distribution.descriptor({'schemaVersion': True})


if __name__ == '__main__':
    unittest.main()
