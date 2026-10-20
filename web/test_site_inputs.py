"""Check optional real compiled inputs independently of CAD."""
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from test_site import builder, SOURCE_SHA


class SiteInputTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get('KUFR_SITE_WEB_DIST'), 'real dist provided by app CI')
    def test_real_compiled_app_is_packaged_without_legacy_sources(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary)
            destination = repo / 'application'
            dist = Path(os.environ['KUFR_SITE_WEB_DIST'])
            with patch.object(builder.subprocess, 'check_output', return_value=SOURCE_SHA):
                builder.build(repo, destination, dist)
            self.assertEqual((dist / 'index.html').read_bytes(), (destination / 'index.html').read_bytes())
            for legacy in ('companion', 'simulator', 'keepsake', 'workshop', 'design-system'):
                self.assertFalse((destination / legacy).exists(), legacy)


if __name__ == '__main__':
    unittest.main()
