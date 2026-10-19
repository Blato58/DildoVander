"""Synthetic provenance tests. These reports never claim a real CAD run occurred."""
import copy
import json
import tempfile
import unittest
from pathlib import Path
from export_model_bundle import write_model_bundle
from model_verification import (certify_bundle, evidence_for_success, file_hash, model_source_hashes,
                                prepare_validation_refresh, write_generation_receipt)
from source_inputs import file_hashes
from preview_ui_fixture import fixture_payload


class ModelVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.sources = self.root / "sources"
        self.sources.mkdir()
        (self.sources / "geometry.py").write_text("# synthetic geometry source\n", encoding="utf-8")
        (self.sources / "parameters.json").write_text('{"fixture":true}', encoding="utf-8")
        (self.sources / "viewer.js").write_text("// synthetic UI\n", encoding="utf-8")
        (self.sources / "verify.py").write_text("# synthetic verifier\n", encoding="utf-8")
        self.out = self.sources / "exports"
        self.bundle = self.out / "model-release"
        payload = fixture_payload()
        payload["twinManifest"] = None
        write_model_bundle(payload, self.bundle, source_revision="synthetic-unit-test-report")
        write_generation_receipt(self.bundle, self.sources, self.out / "model-generation.json")
        self.full_sources = file_hashes(self.sources)
        self.report = {"result": "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE", "physical_test_performed": False,
                       "sources": self.full_sources, "modelEvidence": evidence_for_success(self.out, self.sources, self.full_sources)}
        self.report_path = self.out / "verification.json"
        self.report_path.write_text(json.dumps(self.report), encoding="utf-8")

    def test_verified_artifact_binds_exact_data_and_ignores_only_ui_updates(self):
        original = json.loads((self.bundle / "model-bundle.json").read_text(encoding="utf-8"))
        (self.sources / "viewer.js").write_text("// later UI revision\n", encoding="utf-8")
        manifest = certify_bundle(self.bundle, self.report_path, self.sources)
        self.assertEqual(manifest["provenance"]["cadVerification"], "verified")
        self.assertNotEqual(manifest["bundleHash"], original["bundleHash"])
        self.assertEqual(manifest["assets"][0]["sha256"], original["assets"][0]["sha256"])
        self.assertEqual(manifest["assets"][1]["kind"], "verification-json")
        self.assertEqual((self.bundle / "cad-verification.json").read_bytes(), self.report_path.read_bytes())
        self.assertNotIn("viewer.js", model_source_hashes(self.sources))

    def test_mechanical_changes_and_unknown_dependencies_invalidate_evidence(self):
        (self.sources / "geometry.py").write_text("# changed geometry\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "exact asset bytes"):
            certify_bundle(self.bundle, self.report_path, self.sources)
        (self.sources / "unknown-tool.js").write_text("// conservative unknown dependency\n", encoding="utf-8")
        self.assertIn("unknown-tool.js", model_source_hashes(self.sources))

    def test_data_mutation_fails_without_changing_the_manifest(self):
        before = (self.bundle / "model-bundle.json").read_bytes()
        with (self.bundle / "model.json").open("ab") as stream:
            stream.write(b" ")
        with self.assertRaises(ValueError):
            certify_bundle(self.bundle, self.report_path, self.sources)
        self.assertEqual((self.bundle / "model-bundle.json").read_bytes(), before)

    def test_rejects_failed_physical_unbound_or_missing_report(self):
        for mutate in (lambda r: r.update(result="FAIL"), lambda r: r.update(physical_test_performed=True),
                       lambda r: r.pop("modelEvidence"), lambda r: r["modelEvidence"].update(modelAssetSha256="0" * 64),
                       lambda r: r["sources"].update({"geometry.py": "0" * 64})):
            altered = copy.deepcopy(self.report)
            mutate(altered)
            self.report_path.write_text(json.dumps(altered), encoding="utf-8")
            with self.assertRaises(ValueError):
                certify_bundle(self.bundle, self.report_path, self.sources)

    def test_generation_evidence_is_not_created_when_the_model_or_sources_drift(self):
        (self.sources / "parameters.json").write_text('{"changed":true}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "changed since CAD generation"):
            evidence_for_success(self.out, self.sources, self.full_sources)

    def test_validation_transfer_preserves_exact_model_but_requires_new_report(self):
        certify_bundle(self.bundle, self.report_path, self.sources)
        model_before = (self.bundle / "model.json").read_bytes()
        manifest_before = (self.bundle / "model-bundle.json").read_bytes()
        (self.sources / "verify.py").write_text("# changed synthetic verifier\n", encoding="utf-8")
        current_sources = file_hashes(self.sources)
        replacements = prepare_validation_refresh(self.out, self.sources, self.full_sources, current_sources)
        self.assertEqual((self.bundle / "model-bundle.json").read_bytes(), manifest_before)
        for name, content in replacements.items():
            (self.out / name).write_bytes(content)
        (self.bundle / "cad-verification.json").unlink()
        manifest = json.loads((self.bundle / "model-bundle.json").read_bytes())
        self.assertEqual(manifest["provenance"]["cadVerification"], "not-verified")
        self.assertEqual(len(manifest["assets"]), 1)
        self.assertEqual((self.bundle / "model.json").read_bytes(), model_before)
        with self.assertRaisesRegex(ValueError, "exact asset bytes"):
            certify_bundle(self.bundle, self.report_path, self.sources)
        fresh_report = {**self.report, "sources": current_sources,
                        "modelEvidence": evidence_for_success(self.out, self.sources, current_sources)}
        self.report_path.write_text(json.dumps(fresh_report), encoding="utf-8")
        self.assertEqual(certify_bundle(self.bundle, self.report_path, self.sources)["provenance"]["cadVerification"], "verified")

    def test_validation_transfer_rejects_published_paths_and_changed_model_data(self):
        with self.assertRaisesRegex(ValueError, "working exports"):
            prepare_validation_refresh(self.root / "published", self.sources, self.full_sources, self.full_sources)
        (self.sources / "verify.py").write_text("# changed verifier", encoding="utf-8")
        before = (self.bundle / "model-bundle.json").read_bytes()
        with (self.bundle / "model.json").open("ab") as stream:
            stream.write(b" ")
        with self.assertRaisesRegex(ValueError, "changed before generation snapshot"):
            prepare_validation_refresh(self.out, self.sources, self.full_sources, file_hashes(self.sources))
        self.assertEqual((self.bundle / "model-bundle.json").read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
