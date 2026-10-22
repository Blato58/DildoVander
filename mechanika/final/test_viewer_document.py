"""Exercise the same embedding path as preview.py without importing CAD libraries."""
import json
import hashlib
from pathlib import Path
import tempfile
import re
import subprocess
import zipfile
import unittest
from unittest.mock import patch

import viewer_document
import refresh_viewer
import viewer_runtime
from preview_ui_fixture import fixture_payload


class ViewerDocumentTests(unittest.TestCase):
    def test_reference_document_is_themed_offline_and_preserves_escaped_evidence(self):
        result = viewer_document.render_reference_document([
            {"name": "Synthetic reference", "sha256": "abc123", "text": "</script><img src=x>"},
            {"name": "Synthetic wiring", "sha256": "def456", "text": "Unchanged source text"},
        ])
        self.assertIn('data-theme-select', result)
        self.assertIn(json.dumps('Stínový čaroděj'), result)
        self.assertIn(json.dumps('Mistr hůlek'), result)
        self.assertNotIn('Lord Voldemort', result)
        self.assertNotIn('Olivander', result)
        self.assertLess(result.index('globalThis.DildoVanderTheme'), result.index('<style>'))
        self.assertIn('&lt;/script&gt;&lt;img src=x&gt;', result)
        self.assertIn('Unchanged source text', result)
        self.assertNotIn('<img src=x>', result)
        self.assertNotIn('@import', result)

    def test_real_document_embeds_compiler_plan_trace_renderer_and_escapes_data(self):
        result = viewer_document.render_document({"meshes": [], "note": "</script><img src=x>"})
        self.assertIn('id="show-inspector"', result)
        self.assertIn('ShowTimeline.mount(document, model.showPlan, model.showManifest)', result)
        self.assertIn('"semanticsVersion":"dv02-phase-1"', result)
        self.assertIn('\\u003c/script>\\u003cimg src=x>', result)
        self.assertNotIn('__SHOW_TIMELINE_SCRIPT__', result)
        self.assertNotIn('__MODEL_DATA__', result)
        self.assertNotIn('@import', result)
        self.assertIn('globalThis.VapourUI =', result)
        self.assertIn('globalThis.VapourViewerUi =', result)
        self.assertNotIn('__WORKSHOP_UI_RUNTIME__', result)
        self.assertIn('globalThis.DildoVanderTheme =', result)
        self.assertLess(result.index('globalThis.DildoVanderTheme ='), result.index('<style>'))
        self.assertNotRegex(result, r'<script[^>]+src=')
        self.assertNotRegex(result, re.compile(r'^\s*(?:import|export)\s', re.MULTILINE))

    def test_offline_theme_bootstrap_persists_without_network(self):
        document = viewer_document.render_document({"meshes": []})
        runtime = next(source for source in re.findall(r'<script>(.*?)</script>', document, re.DOTALL)
                       if 'globalThis.DildoVanderTheme =' in source)
        prelude = """
const assert = require('node:assert/strict');
const stored = new Map();
globalThis.fetch = () => { throw new Error('Offline theme requested network'); };
globalThis.document = {documentElement:{dataset:{},style:{}},querySelector:()=>({})};
globalThis.localStorage = {getItem:key=>stored.get(key),setItem:(key,value)=>stored.set(key,value)};
"""
        checks = """
assert.equal(document.documentElement.dataset.theme, 'lord-voldemort');
DildoVanderTheme.setTheme('olivander');
assert.equal(document.documentElement.dataset.theme, 'olivander');
assert.equal(stored.get('dildovander.theme'), 'olivander');
"""
        result = subprocess.run(['node', '-e', prelude + runtime + checks], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_actual_offline_bundle_executes_shared_controls_without_network(self):
        document = viewer_document.render_document({"meshes": []})
        runtime = next(source for source in re.findall(r'<script>(.*?)</script>', document, re.DOTALL)
                       if 'globalThis.VapourUI =' in source)
        checks = """
const assert = require('node:assert/strict');
globalThis.fetch = () => { throw new Error('Offline UI requested network'); };
const elements = new Map();
const document = {getElementById(id) {
  if (!elements.has(id)) elements.set(id, {});
  return elements.get(id);
}};
VapourViewerUi.mountViewerUi(document);
assert.match(elements.get('mode-actions').innerHTML, /data-viewer-mode="operation"/);
assert.match(elements.get('sequence-actions').innerHTML, /aw-button--danger/);
assert.match(elements.get('model-status').innerHTML, /aw-badge/);
VapourViewerUi.renderViewerError(document.getElementById('error'), '<img src=x>');
assert.match(elements.get('error').innerHTML, /&lt;img src=x&gt;/);
assert.equal(elements.get('error').hidden, false);
"""
        with tempfile.TemporaryDirectory() as directory:
            script = Path(directory) / 'offline-runtime.cjs'
            script.write_text(runtime + checks, encoding='utf-8')
            result = subprocess.run(['node', str(script)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_packaged_runtime_snapshot_regenerates_without_shared_tree(self):
        expected = viewer_runtime.bundle_ui_runtime()
        self.assertIn('globalThis.DildoVanderTheme =', expected)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive_path = root / 'synthetic-ui-only.zip'
            with zipfile.ZipFile(archive_path, 'w') as archive:
                archive.write(viewer_runtime.snapshot_runtime(), 'workshop-ui-runtime.js')
                archive.write(viewer_runtime.ROOT / 'viewer_ui.js', 'viewer_ui.js')
                archive.writestr('viewer.html', viewer_document.render_document(fixture_payload()))
            with zipfile.ZipFile(archive_path) as archive:
                self.assertIsNone(archive.testzip())
                self.assertIn(b'SYNTETICK', archive.read('viewer.html'))
                for filename in ['workshop-ui-runtime.js', 'viewer_ui.js']:
                    (root / filename).write_bytes(archive.read(filename))
            snapshot = root / 'workshop-ui-runtime.js'
            with patch.object(viewer_runtime, 'ROOT', root), \
                 patch.object(viewer_runtime, 'SHARED_RUNTIME', root / 'missing' / 'ui.js'), \
                 patch.object(viewer_runtime, 'SNAPSHOT', snapshot):
                self.assertEqual(viewer_runtime.bundle_ui_runtime(), expected)

    def test_runtime_rejects_unbundled_dependency(self):
        with tempfile.TemporaryDirectory() as directory:
            module = Path(directory) / 'unexpected.js'
            module.write_text("import { other } from './other.js';\nexport function example() {}", encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'Unbundled'):
                viewer_runtime.inline_module(module, 'Example')

    def test_refresh_uses_existing_meshes_labels_provenance_and_preserves_source(self):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / "viewer.html", Path(directory) / "preview.html"
            original = '<script>const model={"meshes":[{"name":"actual-part"}]};old renderer</script>'
            source.write_text(original, encoding="utf-8")
            self.assertEqual(refresh_viewer.refresh_preview(source, output), 1)
            self.assertEqual(source.read_text(encoding="utf-8"), original)
            rendered = output.read_text(encoding="utf-8")
            self.assertIn('"viewerPreview":{"sourceName":"viewer.html","dataDate":', rendered)
            self.assertIn('"name":"actual-part"', rendered)
            self.assertNotIn('old renderer', rendered)
            with self.assertRaisesRegex(ValueError, "must not overwrite"):
                refresh_viewer.refresh_preview(source, source)

    def test_refresh_rejects_missing_or_empty_model_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / "viewer.html", Path(directory) / "preview.html"
            for content in ['<script>alert(1)</script>', '<script>const model={"meshes":[]};</script>']:
                source.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    refresh_viewer.refresh_preview(source, output)
            self.assertFalse(output.exists())

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
