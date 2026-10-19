"""Offline source/package invariants; these checks do not establish guest E2E."""
import base64
import hashlib
import importlib.util
from pathlib import Path
import re
import tempfile
import unittest

PORTAL = Path(__file__).resolve().parent
REPO = PORTAL.parents[2]


class PortalSourceTests(unittest.TestCase):
    def test_fragment_removed_before_assets_with_matching_csp(self):
        html = (PORTAL / 'index.html').read_text(encoding='utf-8')
        script = re.search(r'<script>(.*?)</script>', html, re.S)
        self.assertLess(script.start(), html.index('<link '))
        self.assertIn("history.replaceState(null,'',location.pathname+location.search)", script.group(1))
        digest = base64.b64encode(hashlib.sha256(script.group(1).encode()).digest()).decode()
        self.assertIn("'sha256-" + digest + "'", html)
        self.assertIn('no-referrer', html)

    def test_no_persistent_storage_or_untrusted_html(self):
        js = (PORTAL / 'app.js').read_text(encoding='utf-8')
        theme = (PORTAL / 'theme.js').read_text(encoding='utf-8')
        for script in (js, theme):
            for forbidden in ('localStorage', 'sessionStorage', 'indexedDB', 'serviceWorker', 'innerHTML', 'console.'):
                self.assertNotIn(forbidden, script)
        self.assertIn("cache: 'no-store'", js)
        self.assertIn('mutationKeys.set(mutationIdentity, crypto.randomUUID())', js)
        self.assertIn("headers['X-DV10-CSRF'] = csrfToken", js)

    def test_packager_copies_only_runtime_sources(self):
        spec = importlib.util.spec_from_file_location('site_build', REPO / 'kufr/web/build-site.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder)
            module.build_keepsake(REPO, output)
            expected = {'index.html', 'app.js', 'keepsake.css', 'theme.js', 'evening/index.html', 'evening/evening.js', 'evening/evening.css', 'identity.js', 'chronicle.html', 'chronicle.js', 'chronicle.css', 'duel.html', 'duel.js', 'duel.css'}
            runtime = output / 'keepsake'
            self.assertEqual(expected, {p.relative_to(runtime).as_posix() for p in runtime.rglob('*') if p.is_file()})
            for name in expected:
                self.assertEqual((runtime / name).read_bytes(), (PORTAL / name).read_bytes())

    def test_public_page_has_only_get_and_base_allowlist(self):
        js = (PORTAL / 'evening/evening.js').read_text(encoding='utf-8')
        self.assertIn("credentials: 'omit'", js)
        for forbidden in ('localStorage', 'sessionStorage', 'indexedDB', 'serviceWorker', 'innerHTML', '/bootstrap', 'Idempotency-Key', 'X-DV10-CSRF', "method: 'POST'"):
            self.assertNotIn(forbidden, js)
        self.assertIn("['base_image', 'export_story', 'export_card', 'export_zip']", js)
        self.assertIn("/guest/v1/evening?limit=24", js)

    def test_standalone_root_is_public_evening(self):
        source = (REPO / 'kufr/web/build-site.py').read_text(encoding='utf-8')
        self.assertIn("shutil.copyfile(destination / 'keepsake/evening/index.html', destination / 'index.html')", source)


if __name__ == '__main__':
    unittest.main()
