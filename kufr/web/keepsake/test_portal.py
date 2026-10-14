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
        for forbidden in ('localStorage', 'sessionStorage', 'indexedDB', 'serviceWorker', 'innerHTML', 'console.'):
            self.assertNotIn(forbidden, js)
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
            self.assertEqual({'index.html', 'app.js', 'keepsake.css'}, {p.name for p in (output / 'keepsake').iterdir()})
            for path in (output / 'keepsake').iterdir():
                self.assertEqual(path.read_bytes(), (PORTAL / path.name).read_bytes())


if __name__ == '__main__':
    unittest.main()
