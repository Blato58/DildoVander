"""Exercise real site inputs without generating or accepting manufacturing data."""
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

from test_site import builder, SOURCE_SHA


ROOT = Path(__file__).resolve().parents[2]


class SiteInputTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name) / 'repo'
        for folder in ['web', 'design-system', 'simulator']:
            shutil.copytree(ROOT / 'kufr' / folder, self.repo / 'kufr' / folder,
                            ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
        out = self.repo / 'kufr/mechanika/final/exports'
        out.mkdir(parents=True)
        (out / 'viewer.html').write_text('Synthetic CAD fixture', encoding='utf-8')
        (out / 'F2-kouzelny-kufr.zip').write_bytes(b'Synthetic CAD fixture')
        if os.environ.get('KUFR_SITE_WEB_DIST'):
            self.dist = Path(os.environ['KUFR_SITE_WEB_DIST'])
        else:
            self.dist = self.repo / 'companion-fixture'
            self.dist.mkdir()
            (self.dist / 'index.html').write_text('<head></head>', encoding='utf-8')
        self.destination = self.repo / 'site-with-synthetic-cad'

    def assemble(self):
        # Only the CAD boundary is synthetic; source assets and the CI web build are real.
        with patch.object(builder, 'verified_exports', return_value=({'revision': 'F2'},
                          ['viewer.html', 'F2-kouzelny-kufr.zip'])), \
                patch.object(builder.subprocess, 'check_output', return_value=SOURCE_SHA):
            builder.build(self.repo, self.destination, self.dist)

    def test_real_site_assets_and_companion_assemble(self):
        self.assemble()
        self.assertEqual((self.destination / 'site.css').read_bytes(),
                         (ROOT / 'kufr/web/site.css').read_bytes())
        homepage = (self.destination / 'index.html').read_text(encoding='utf-8')
        for marker in ['__REVISION__', '__PACKAGE_NAME__', '__PACKAGE_SIZE__', '__SOURCE_SHORT__']:
            self.assertNotIn(marker, homepage)
        self.assertIn('data-static-preview',
                      (self.destination / 'simulator/index.html').read_text(encoding='utf-8'))
        self.assertIn('name="kufr-browser-mode" content="local-fake"',
                      (self.destination / 'companion/index.html').read_text(encoding='utf-8'))

    def test_missing_site_css_fails_assembly(self):
        (self.repo / 'kufr/web/site.css').unlink()
        with self.assertRaises(FileNotFoundError):
            self.assemble()


if __name__ == '__main__':
    unittest.main()
