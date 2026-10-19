"""CAD-independent shared classification and model-proof inventory checks."""
import tempfile
import unittest
from pathlib import Path

from model_verification import model_source_hashes
from source_inputs import file_hashes, presentation_only, validation_only


class SourceInputsTests(unittest.TestCase):
    def test_reviewed_viewer_and_bundle_leaves_are_presentation_only(self):
        for name in ("viewer_engine_core.js", "twin_core.js", "timeline_core.js",
                     "twin_media_core.js", "twin_media_controller.js",
                     "export_model_bundle.py", "model_verification.py", "certify_model_bundle.py",
                     "test_model_bundle_export.py", "test_model_verification.py"):
            with self.subTest(name=name):
                self.assertTrue(presentation_only(name))
                self.assertFalse(validation_only(name))
                self.assertFalse(presentation_only("nested/" + name))

    def test_geometry_generation_and_unknown_inputs_keep_conservative_classification(self):
        for name in ("preview.py", "artifacts.py", "regenerate.py", "part_cache.py",
                     "model.py", "unknown-tool.js", "nested/machine.yaml"):
            self.assertFalse(presentation_only(name), name)
        self.assertTrue(validation_only("verify.py"))
        self.assertFalse(presentation_only("verify.py"))

    def test_model_proof_uses_complete_shared_inventory_except_reviewed_presentation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            names = ("model.py", "verify.py", "test_new_geometry.py", "machine.yaml",
                     "nested/viewer.js", "viewer_engine_core.js", "model_verification.py")
            for name in names:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(name, encoding="utf-8")
            (root / "exports").mkdir()
            (root / "exports" / "model.json").write_text("generated", encoding="utf-8")
            expected = {name: digest for name, digest in file_hashes(root).items() if not presentation_only(name)}
            self.assertEqual(model_source_hashes(root), expected)
            self.assertEqual(set(expected), {"model.py", "verify.py", "test_new_geometry.py", "machine.yaml", "nested/viewer.js"})
            before = model_source_hashes(root)
            (root / "viewer_engine_core.js").write_text("UI revision", encoding="utf-8")
            self.assertEqual(model_source_hashes(root), before)
            (root / "machine.yaml").write_text("changed mechanical input", encoding="utf-8")
            self.assertNotEqual(model_source_hashes(root), before)


if __name__ == "__main__":
    unittest.main()
