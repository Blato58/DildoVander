"""Local immutable publication and rollback checks; no production side effects."""
import hashlib
import json
import os
import shutil
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import release_store as store


class ReleaseStoreTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.models = self.root / 'models'
        self.application = self.root / 'application'

    def bundle(self, name):
        bundle = self.root / name
        bundle.mkdir()
        (bundle / 'model.json').write_text(json.dumps({'fixture': name}))
        checksum = store.digest(bundle / 'model.json')
        manifest = {'modelId': 'stable-model', 'bundleHash': checksum,
                    'assets': [{'path': 'model.json'}]}
        (bundle / 'model-bundle.json').write_text(json.dumps(manifest))
        return bundle, manifest

    def app(self, name):
        app = self.root / name
        (app / 'assets').mkdir(parents=True)
        (app / 'index.html').write_text(name)
        (app / f'assets/app-{hashlib.sha256(name.encode()).hexdigest()[:8]}.js').write_text(name)
        manifest = {'schemaVersion': 2, 'artifactType': 'application', 'apiContractVersion': 1,
                    'applicationSourceDirty': False,
                    'applicationSourceRevision': 'a' * 40, 'sha256': store.inventory(app)}
        (app / 'site-manifest.json').write_text(json.dumps(manifest))
        return app

    def fake_validator(self, path):
        # Publication unit tests use labelled small payloads, never CAD evidence.
        return json.loads((path / 'model-bundle.json').read_text())

    def test_model_publication_is_immutable_and_rollback_is_independent(self):
        first, first_manifest = self.bundle('first')
        second, second_manifest = self.bundle('second')
        with patch.object(store, 'validate_model', side_effect=self.fake_validator):
            first_pointer = store.publish_model(first, self.models)
            store.publish_model(second, self.models)
            store.publish_model(second, self.models)  # Retry must retain the prior release for rollback.
            self.assertEqual(first_pointer, store.rollback_model(self.models))
            self.assertEqual(first_pointer, json.loads((self.models / 'active.json').read_text()))
            self.assertTrue((self.models / 'bundles' / second_manifest['bundleHash']).exists())
            self.assertEqual(first_manifest['bundleHash'], first_pointer['bundleHash'])

    @unittest.skipIf(os.name == 'nt', 'POSIX hosting permission contract')
    def test_private_upload_modes_are_repaired_before_publication_and_reuse(self):
        bundle, manifest = self.bundle('private-upload')
        nested = bundle / 'evidence'
        nested.mkdir()
        (nested / 'proof.json').write_text('{"synthetic":true}')
        for path in (bundle, nested):
            path.chmod(0o700)
        for path in bundle.rglob('*'):
            if path.is_file():
                path.chmod(0o600)
        original = store.inventory(bundle)
        private = self.root / 'private-secret'
        private.write_text('synthetic secret outside public store')
        private.chmod(0o600)
        target = self.models / 'bundles' / manifest['bundleHash']

        def check(pointer):
            for path in (self.models, self.models / 'bundles', target, target / 'evidence'):
                self.assertEqual(0o755, path.stat().st_mode & 0o777)
            for path in (*[p for p in target.rglob('*') if p.is_file()], self.models / 'active.json'):
                self.assertEqual(0o644, path.stat().st_mode & 0o777)
            self.assertEqual(original, store.inventory(target))

        old_umask = os.umask(0o077)
        try:
            first = store.publish_model(bundle, self.models, validator=self.fake_validator, check=check)
            target.chmod(0o700)
            (target / 'model.json').chmod(0o600)
            (self.models / 'active.json').chmod(0o600)
            self.assertEqual(first, store.publish_model(bundle, self.models, validator=self.fake_validator, check=check))
        finally:
            os.umask(old_umask)
        self.assertEqual(0o700, bundle.stat().st_mode & 0o777)
        self.assertEqual(0o600, private.stat().st_mode & 0o777)

    @unittest.skipIf(os.name == 'nt', 'POSIX hosting permission contract')
    def test_rollback_repairs_validated_public_model_modes(self):
        first, manifest = self.bundle('first')
        second, _ = self.bundle('second')
        store.publish_model(first, self.models, validator=self.fake_validator)
        store.publish_model(second, self.models, validator=self.fake_validator)
        target = self.models / 'bundles' / manifest['bundleHash']
        target.chmod(0o700)
        (target / 'model.json').chmod(0o600)
        def check(pointer):
            self.assertEqual(0o755, target.stat().st_mode & 0o777)
            self.assertEqual(0o644, (target / 'model.json').stat().st_mode & 0o777)
        store.rollback_model(self.models, validator=self.fake_validator, check=check)

    @unittest.skipIf(os.name == 'nt', 'POSIX hosting permission contract')
    def test_rollback_rejects_symlink_store_before_changing_outside_modes(self):
        first, _ = self.bundle('first')
        second, _ = self.bundle('second')
        store.publish_model(first, self.models, validator=self.fake_validator)
        store.publish_model(second, self.models, validator=self.fake_validator)
        external = self.root / 'outside-store'
        (self.models / 'bundles').rename(external)
        (self.models / 'bundles').symlink_to(external, target_is_directory=True)
        paths = (external, *external.rglob('*'))
        for path in paths:
            path.chmod(0o700 if path.is_dir() else 0o600)
        modes = {path: path.stat().st_mode for path in paths}
        active = (self.models / 'active.json').read_bytes()
        with self.assertRaisesRegex(ValueError, 'canonical public store'):
            store.rollback_model(self.models, validator=self.fake_validator)
        self.assertEqual(modes, {path: path.stat().st_mode for path in paths})
        self.assertEqual(active, (self.models / 'active.json').read_bytes())

    def test_failed_model_validation_preserves_current_reference(self):
        first, _ = self.bundle('first')
        with patch.object(store, 'validate_model', side_effect=self.fake_validator):
            store.publish_model(first, self.models)
        before = (self.models / 'active.json').read_bytes()
        second, _ = self.bundle('second')
        with patch.object(store, 'validate_model', side_effect=ValueError('corrupt')):
            with self.assertRaisesRegex(ValueError, 'corrupt'):
                store.publish_model(second, self.models)
        self.assertEqual(before, (self.models / 'active.json').read_bytes())
        self.assertFalse((self.models / '.publication-lock').exists())

    def test_incomplete_or_tampered_app_never_activates(self):
        app = self.app('bad')
        (app / 'index.html').write_text('tampered')
        with self.assertRaisesRegex(ValueError, 'provenance'):
            store.publish_application(app, self.application)
        self.assertFalse((self.application / 'current').exists())

    def test_concurrent_publication_is_rejected(self):
        self.models.mkdir()
        (self.models / '.publication-lock').mkdir()
        bundle, _ = self.bundle('first')
        with self.assertRaisesRegex(ValueError, 'Another publication'):
            store.publish_model(bundle, self.models)

    def test_backend_activation_lock_prevents_publisher_reference_changes(self):
        bundle, _ = self.bundle('first')
        self.models.mkdir()
        lock = self.models / '.activation-lock'
        lock.write_text('synthetic backend selector owns this lock')
        with patch.object(store, 'validate_model', side_effect=self.fake_validator):
            with self.assertRaisesRegex(ValueError, 'Another model activation'):
                store.publish_model(bundle, self.models)
        self.assertTrue(lock.exists())
        self.assertFalse((self.models / 'active.json').exists())
        self.assertFalse((self.models / '.publication-lock').exists())

    @unittest.skipUnless(shutil.which('node'), 'Node required for maintained loader integration')
    def test_real_loader_rejects_fixture_before_publication(self):
        fixture = store.REPO / 'mobilni-aplikace/src/features/workshop/fixture'
        if not fixture.is_dir():
            self.skipTest('Workshop fixture is not present')
        with self.assertRaises(store.subprocess.CalledProcessError):
            store.publish_model(fixture, self.models)
        self.assertFalse((self.models / 'active.json').exists())

    @unittest.skipUnless(shutil.which('node'), 'Node required for maintained loader integration')
    def test_manifest_provenance_changes_publish_distinct_immutable_releases(self):
        fixture = store.REPO / 'mobilni-aplikace/src/features/workshop/fixture'
        if not fixture.is_dir():
            self.skipTest('Workshop fixture is not present')
        # Temporary unverified export-format packages exercise publication only.
        # They are labelled synthetic and never become a production artifact.
        packages = []
        for name in ('synthetic-export-a', 'synthetic-export-b'):
            package = self.root / name
            shutil.copytree(fixture, package)
            manifest_path = package / 'model-bundle.json'
            manifest = json.loads(manifest_path.read_text())
            manifest['provenance'] = {'kind': 'cad-export', 'sourceRevision': name,
                                      'cadVerification': 'not-verified'}
            identity = {key: value for key, value in manifest.items() if key != 'bundleHash'}
            canonical = json.dumps(identity, ensure_ascii=False, sort_keys=True, separators=(',', ':'))
            manifest['bundleHash'] = hashlib.sha256(canonical.encode('utf-8')).hexdigest()
            manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
            packages.append(package)
        self.assertEqual(store.digest(packages[0] / 'model.json'), store.digest(packages[1] / 'model.json'))
        first = store.publish_model(packages[0], self.models)
        second = store.publish_model(packages[1], self.models)
        self.assertNotEqual(first['bundleHash'], second['bundleHash'])
        self.assertNotEqual(first['manifestPath'], second['manifestPath'])
        self.assertEqual(second, store.publish_model(packages[1], self.models))
        self.assertEqual(first, store.rollback_model(self.models))
        self.assertEqual(2, len(list((self.models / 'bundles').iterdir())))

    @unittest.skipUnless(shutil.which('node'), 'Node required for maintained loader integration')
    def test_undeclared_model_files_are_rejected_by_actual_loader(self):
        fixture = store.REPO / 'mobilni-aplikace/src/features/workshop/fixture'
        if not fixture.is_dir():
            self.skipTest('Workshop fixture is not present')
        package = self.root / 'unsafe-fixture'
        shutil.copytree(fixture, package)
        (package / 'extra.txt').write_text('undeclared test material')
        command = ['node', str(store.REPO / 'mobilni-aplikace/scripts/validate-model-bundle.mjs'),
                   '--bundle', str(package), '--allow-fixture']
        result = store.subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Undeclared file', result.stderr)

    @unittest.skipIf(os.name == 'nt', 'Atomic directory symlink replacement targets Linux hosting')
    def test_app_activation_and_rollback_retain_old_chunk_urls(self):
        # Windows without Developer Mode cannot create directory symlinks.
        probe = self.root / 'symlink-probe'
        try:
            probe.symlink_to(self.root, target_is_directory=True)
            probe.unlink()
        except OSError:
            self.skipTest('OS does not permit directory symlinks; Linux CI exercises activation')
        first = self.app('first')
        second = self.app('second')
        first_id = store.publish_application(first, self.application)
        store.publish_application(second, self.application)
        store.publish_application(second, self.application)
        self.assertEqual('second', (self.application / 'current/index.html').read_text())
        store.rollback_application(self.application)
        self.assertEqual('first', (self.application / 'current/index.html').read_text())
        self.assertEqual(first_id, (self.application / 'current').resolve().name)
        self.assertEqual(2, len(list((self.application / 'assets').glob('*.js'))))


if __name__ == '__main__':
    unittest.main()
