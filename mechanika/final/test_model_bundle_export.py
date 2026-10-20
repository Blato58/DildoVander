"""Data export checks run without CAD, VTK, numpy or OpenCascade."""
import json
import tempfile
import unittest
from pathlib import Path
from export_model_bundle import model_data, write_model_bundle
from preview_ui_fixture import fixture_payload


class ModelBundleExportTests(unittest.TestCase):
    def test_stable_ids_do_not_depend_on_order(self):
        payload = fixture_payload()
        second = dict(payload["meshes"][0], name="second_stable_part")
        payload["meshes"].append(second)
        first = model_data(payload, "fixture")
        payload["meshes"].reverse()
        reordered = model_data(payload, "fixture")
        self.assertEqual({m["name"]: m["id"] for m in first["meshes"]},
                         {m["name"]: m["id"] for m in reordered["meshes"]})

    def test_export_is_data_only_and_does_not_claim_verification(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            manifest = write_model_bundle(fixture_payload(), directory, source_revision="fixture-source", fixture=True)
            self.assertEqual(manifest["provenance"]["cadVerification"], "not-verified")
            self.assertEqual(set(p.name for p in directory.iterdir()), {"model-bundle.json", "model.json"})
            model = json.loads((directory / "model.json").read_text(encoding="utf-8"))
            self.assertEqual(model["meshes"][0]["id"], "ui_fixture_block")
            self.assertNotIn("<script", (directory / "model.json").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
