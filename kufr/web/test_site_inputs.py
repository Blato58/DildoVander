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
                            ignore=shutil.ignore_patterns('__pycache__', '*.pyc', 'node_modules'))
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
        self.assertEqual((self.destination / 'simulator/dashboard-ui.js').read_bytes(),
                         (ROOT / 'kufr/simulator/dashboard-ui.js').read_bytes())
        self.assertIn('name="kufr-browser-mode" content="local-fake"',
                      (self.destination / 'companion/index.html').read_text(encoding='utf-8'))

    def test_missing_site_css_fails_assembly(self):
        (self.repo / 'kufr/web/site.css').unlink()
        with self.assertRaises(FileNotFoundError):
            self.assemble()

    def test_shared_framework_and_generated_tokens_ship_with_real_site(self):
        self.assemble()
        for name in ['ui.js', 'arcane-workshop.css', 'components/framework.css',
                     'tokens/palette.css', 'tokens/typography.css', 'tokens/layout.css', 'tokens/motion.css']:
            self.assertEqual((self.destination / 'design-system' / name).read_bytes(),
                             (ROOT / 'kufr/design-system' / name).read_bytes())

    def test_missing_framework_fails_before_publishing_incomplete_shell(self):
        (self.repo / 'kufr/design-system/ui.js').unlink()
        with self.assertRaisesRegex(ValueError, 'Shared UI runtime missing: ui.js'):
            self.assemble()

    def test_development_dependencies_are_never_published(self):
        dependency = self.repo / 'kufr/design-system/node_modules/development-only'
        dependency.mkdir(parents=True)
        (dependency / 'index.js').write_text('development-only', encoding='utf-8')
        self.assemble()
        self.assertFalse((self.destination / 'design-system/node_modules').exists())
        self.assertFalse((self.destination / 'design-system/tests').exists())
        self.assertFalse((self.destination / 'design-system/package-lock.json').exists())


if __name__ == '__main__':
    unittest.main()
