"""Offline checks of the private migration template; never contact production."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

FOLDER = Path(__file__).with_name('private-pipeline')
SPEC = importlib.util.spec_from_file_location('prepared_readback', FOLDER / 'verify-application.py')
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)
SHA = 'a' * 40


class PrivatePipelinePreparationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.local = self.root / 'app'
        (self.local / 'assets').mkdir(parents=True)
        (self.local / 'index.html').write_bytes(b'<html>synthetic app fixture</html>')
        (self.local / 'assets/app-12345678.js').write_bytes(b'export{}')
        self.manifest = {'schemaVersion': 2, 'artifactType': 'application', 'applicationSourceRevision': SHA,
                         'applicationSourceDirty': False, 'apiContractVersion': 1,
                         'sha256': HELPER.STORE.inventory(self.local)}
        (self.local / 'site-manifest.json').write_text(json.dumps(self.manifest))

    def fetch(self, base, path):
        headers = {'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer', 'X-Robots-Tag': 'noindex'}
        if path == '/site-manifest.json':
            return 200, headers, json.dumps(self.manifest).encode()
        if path.startswith('/assets/app-'):
            return 200, {'Cache-Control': 'public, immutable'}, (self.local / path.lstrip('/')).read_bytes()
        if 'application-deployment-probe' in path:
            return 404, {}, b'missing'
        if path == '/.well-known/apple-app-site-association':
            return 200, {'Content-Type': 'application/json'}, b'{"webcredentials":{"apps":["TEAM.test.app"]}}'
        if path == '/healthz':
            return 200, {}, b'healthy'
        return 200, headers, (self.local / 'index.html').read_bytes()

    def test_canonical_readback_checks_actual_bytes_without_downloads_or_mutations(self):
        paths = []
        def fetch(base, path):
            paths.append(path)
            return self.fetch(base, path)
        result = HELPER.verify('https://application.example.invalid', self.local, SHA, fetch)
        self.assertEqual(2, result['verifiedFiles'])
        self.assertNotIn('/downloads/index.json', paths)
        self.assertFalse(any(path.startswith('/models/') for path in paths))
        self.assertIn('/keepsake/', paths)
        self.assertIn('/.well-known/apple-app-site-association', paths)

    def test_readback_fails_on_redirect_fallback_wrong_asset_and_missing_native_domain(self):
        for broken in ('/', '/app/v1/application-deployment-probe', '/assets/app-12345678.js', '/.well-known/apple-app-site-association'):
            def fetch(base, path):
                if path == broken:
                    return 302, {'Location': '/signin'}, b''
                return self.fetch(base, path)
            with self.subTest(path=broken), self.assertRaises(ValueError):
                HELPER.verify('https://application.example.invalid', self.local, SHA, fetch)
        with self.assertRaises(ValueError):
            HELPER.local_manifest(self.local, 'b' * 40)
        for value in ('http://application.example.invalid', 'https://127.0.0.1', 'https://application.example.invalid/other',
                      'https://user:secret@application.example.invalid', 'https://application.example.invalid:8443'):
            with self.subTest(origin=value), self.assertRaises(ValueError):
                HELPER.origin(value)

    def test_preflight_requires_existing_managed_schema2_rollback(self):
        host = self.root / 'host'
        (host / 'releases').mkdir(parents=True)
        with self.assertRaisesRegex(ValueError, 'First migration'):
            HELPER.preflight(self.local, SHA, host)
        legacy = host / 'releases/legacy'
        legacy.mkdir()
        (legacy / 'site-manifest.json').write_text('{"schemaVersion":1}')
        try:
            (host / 'current').symlink_to(legacy, target_is_directory=True)
        except OSError:
            self.skipTest('OS does not permit directory symlinks')
        with self.assertRaises(ValueError):
            HELPER.preflight(self.local, SHA, host)
        (host / 'current').unlink()
        import shutil
        target = host / 'releases/known-schema2'
        shutil.copytree(self.local, target)
        (host / 'current').symlink_to(target, target_is_directory=True)
        result = HELPER.preflight(self.local, SHA, host)
        self.assertEqual('known-schema2', result['APP_PRIOR_RELEASE'])
        self.assertEqual(SHA, result['APP_PRIOR_SOURCE_SHA'])
        self.assertEqual(hashlib.sha256((self.local / 'site-manifest.json').read_bytes()).hexdigest(), result['APP_TARGET_RELEASE'])

    def test_template_preserves_trust_gates_and_removes_legacy_runtime_contract(self):
        template = (FOLDER / 'kufr-site-deploy.yml.template').read_text()
        for value in ('artifact: arcane-site', 'uses: ./.github/workflows/reuse-build.yml',
                      'SOURCE_ARTIFACT_DIGEST:', 'checkMain(', 'ref: ${{ github.sha }}',
                      'environment: backend-production', 'group: kufr-backend-production',
                      'release_store.py application publish', 'release_store.py application rollback',
                      'APP_PRIOR_RELEASE != env.APP_TARGET_RELEASE', 'Verify restored application after rollback'):
            self.assertIn(value, template)
        for value in ('publish-site.py', 'verify-site.py', 'PUBLIC_IPV4', 'prune-releases', 'unlink(',
                      'distribution_store.py', 'release_store.py model', 'npm ', 'Regenerate', 'build-site.py',
                      'repository: Blato58/DildoVander', 'application-source-tools'):
            self.assertNotIn(value, template)


if __name__ == '__main__':
    unittest.main()
