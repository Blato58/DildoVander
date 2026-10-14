"""Exercise the same embedding path as preview.py without importing CAD libraries."""
import json
import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import viewer_document


class ViewerDocumentTests(unittest.TestCase):
    def test_real_document_embeds_compiler_plan_trace_renderer_and_escapes_data(self):
        result = viewer_document.render_document({"meshes": [], "note": "</script><img src=x>"})
        self.assertIn('id="show-inspector"', result)
        self.assertIn('ShowTimeline.mount(document, model.showPlan, model.showManifest)', result)
        self.assertIn('"semanticsVersion":"dv02-phase-1"', result)
        self.assertIn('\\u003c/script>\\u003cimg src=x>', result)
        self.assertNotIn('__SHOW_TIMELINE_SCRIPT__', result)
        self.assertNotIn('__MODEL_DATA__', result)
        self.assertNotIn('@import', result)

    def test_stale_plan_is_rejected_before_rendering(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'effects.plan.json').write_text('{}', encoding='utf-8')
            manifest = {'compiledHash': 'wrong'}
            manifest['bundleHash'] = hashlib.sha256(b'{"compiledHash":"wrong"}\n').hexdigest()
            (root / 'effects.manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            with patch.object(viewer_document, 'ROOT', root):
                with self.assertRaisesRegex(ValueError, 'Stale DV02 viewer input'):
                    viewer_document.render_document({})

    def test_corrupted_bundle_manifest_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'effects.plan.json').write_text('{}', encoding='utf-8')
            (root / 'effects.manifest.json').write_text('{"bundleHash":"wrong"}', encoding='utf-8')
            with patch.object(viewer_document, 'ROOT', root):
                with self.assertRaisesRegex(ValueError, 'Invalid DV02 bundle manifest hash'):
                    viewer_document.render_document({})


if __name__ == '__main__':
    unittest.main()
