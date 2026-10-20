"""Exercise evidence transfer and packaging with synthetic, non-CAD exports."""
from contextlib import ExitStack
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from artifacts import (ASSEMBLY_NAMES, GENERATION_SNAPSHOT, VERIFICATION_EXPORTS,
                       assert_verified_exports, export_names, generated_export_names)
import refresh_package as refresh
import source_inputs


def digest(content):
    return hashlib.sha256(content).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False) + "\n", encoding="utf-8")


class PresentationPolicyTests(unittest.TestCase):
    def test_nested_unknown_inputs_are_hashed_but_generated_outputs_are_excluded(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("nested/machine.yaml", "nested/wire.csv", "source.unknown", "exports/model.stl",
                         ".venv/tool.py", "__pycache__/cached.pyc", "fit-debug.json", ".gitignore"):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"input")
            with patch.object(source_inputs, "ROOT", root), \
                    patch("viewer_style.snapshot_styles"), patch("inspect_data.assert_snapshot_current"):
                before = source_inputs.source_hashes()
                self.assertEqual(set(before), {"nested/machine.yaml", "nested/wire.csv", "source.unknown"})
                (root / "nested/machine.yaml").write_bytes(b"changed machine")
                after = source_inputs.source_hashes()
                self.assertNotEqual(before["nested/machine.yaml"], after["nested/machine.yaml"])
                self.assertFalse(source_inputs.presentation_only("nested/machine.yaml"))
                (root / "nested/wire.csv").write_bytes(b"changed wire")
                self.assertNotEqual(after["nested/wire.csv"], source_inputs.source_hashes()["nested/wire.csv"])

    def test_policy_import_needs_no_cad_or_sibling_import_path(self):
        code = (
            "import importlib.util, sys; "
            "spec=importlib.util.spec_from_file_location('policy', sys.argv[1]); "
            "module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module); "
            "assert module.presentation_only('viewer_document.py'); "
            "assert 'cadquery' not in sys.modules; assert 'geometry' not in sys.modules"
        )
        result = subprocess.run([sys.executable, "-I", "-c", code, str(Path(source_inputs.__file__).resolve())],
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_unknown_or_mechanical_inputs_fail_closed(self):
        for name in ("preview.py", "twin_manifest.py", "test_twin_manifest.py", "effects.json",
                     "effects.plan.json", "parameters.json", "viewer_new_geometry.py", "new.js",
                     "../viewer.js", "reference/inspect-sources.json"):
            with self.subTest(name=name):
                self.assertFalse(source_inputs.presentation_only(name))
        for name in ("viewer_document.py", "viewer_style.py", "viewer.js", "test_twin_viewer.cjs",
                     "workshop-design-system.css", "README.md", "VIEWER.md"):
            self.assertTrue(source_inputs.presentation_only(name))

    def test_validation_policy_excludes_generation_and_presentation_sources(self):
        for name in ("verify.py", "test_f2_cad.py", "test_new_verifier.py", "test_safety.cjs"):
            self.assertTrue(source_inputs.validation_only(name), name)
        for name in ("geometry.py", "artifacts.py", "test_viewer_document.py", "test_twin_viewer.cjs",
                     "viewer.js", "nested/test_f2_cad.py", "test_geometry.yaml", "test_BAD.py"):
            self.assertFalse(source_inputs.validation_only(name), name)


class RefreshPackageTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name).resolve()
        self.out = self.root / "exports"
        self.out.mkdir()
        self.payload = {"meshes": [{"name": "body", "vertices": [[1, 2, 3]], "faces": [[0, 0, 0]]}],
                        "parameters": {"lift": 12}, "twinManifest": {"revision": "saved-twin"}}
        (self.root / "geometry.py").write_bytes(b"unchanged mechanical input")
        (self.root / "viewer.js").write_bytes(b"old UI")
        (self.root / "verify.py").write_bytes(b"old verifier")
        self.saved_sources = self.sources()
        self.manifest = {"revision": "F2", "assemblies": list(ASSEMBLY_NAMES),
                         "parts": [{"name": "body"}], "sources": self.saved_sources}
        for name in export_names(self.manifest):
            path = self.out / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic export " + name.encode("ascii"))
        write_json(self.out / "manifest.json", self.manifest)
        write_json(self.out / "slicer-validation.json", {
            "status": "SKIP_SLICER_UNAVAILABLE", "parts": [],
            "manifest_sha256": digest((self.out / "manifest.json").read_bytes()),
        })
        self.analytical = {
            "schema": 1, "status": "PASS_ANALYTICAL", "physical_print_performed": False,
            "manifest_sha256": digest((self.out / "manifest.json").read_bytes()),
            "parts": [{"name": "body", "status": "PASS", "issue_count": 0, "issues": [],
                       "stl_sha256": digest((self.out / "stl/body.stl").read_bytes()),
                       "unsupported_count": 0, "floating_start_count": 0, "thin_wall_count": 0,
                       "flat_bed_contact": True, "issues_truncated": False}],
        }
        write_json(self.out / "printability.json", self.analytical)
        (self.out / "viewer.html").write_text(self.render(self.payload), encoding="utf-8")
        write_json(self.out / GENERATION_SNAPSHOT, {
            "schema": 1, "sources": self.saved_sources,
            "sha256": self.hash_exports(generated_export_names(self.manifest)),
        })
        self.report = {
            "result": "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE", "physical_test_performed": False,
            "sources": self.saved_sources, "mechanics": {"existing_check": "PASS"},
            "cad_sha256": self.hash_exports(name for name in export_names(self.manifest)
                                            if name.endswith((".stl", ".step"))),
            "parts": [{"name": "body", "sha256": digest((self.out / "stl/body.stl").read_bytes())}],
            "export_sha256": self.hash_exports(set(export_names(self.manifest)) - VERIFICATION_EXPORTS),
        }
        write_json(self.out / "verification.json", self.report)
        write_json(self.out / "artifact-sha256.json", {
            **self.report["export_sha256"], "verification.json": digest((self.out / "verification.json").read_bytes()),
        })
        (self.out / "regeneration-cache.json").write_bytes(b"obsolete toolchain cache")
        self.original_report_hash = digest((self.out / "verification.json").read_bytes())
        self.immutable = {name: (self.out / name).read_bytes() for name in export_names(self.manifest)
                          if name not in {"manifest.json", "verification.json", "artifact-sha256.json",
                                          GENERATION_SNAPSHOT, "viewer.html", "slicer-validation.json",
                                          "printability.json"}}
        stack = self.enterContext(ExitStack())
        stack.enter_context(patch.object(refresh, "ROOT", self.root))
        stack.enter_context(patch.object(refresh, "prepared_sources", side_effect=self.sources))
        stack.enter_context(patch.object(refresh, "source_hashes", side_effect=self.sources))
        stack.enter_context(patch.object(refresh, "render_document", side_effect=self.render))

    def sources(self):
        return {path.name: digest(path.read_bytes()) for path in self.root.iterdir() if path.is_file()}

    def hash_exports(self, names):
        return {name: digest((self.out / name).read_bytes()) for name in sorted(names)}

    def render(self, payload):
        return "<!doctype html><p>" + (self.root / "viewer.js").read_text() + "</p>" + refresh.MODEL_MARKER + json.dumps(payload) + ";window.viewer=true;</script>"

    def change_ui(self):
        (self.root / "viewer.js").write_bytes(b"new UI")

    def run_refresh(self):
        return refresh.refresh(self.saved_sources, manufacturing_key="a" * 64)

    def test_refresh_preserves_geometry_and_transfers_receipts_and_zip(self):
        self.change_ui()
        self.assertTrue(self.run_refresh())
        self.assertFalse((self.out / "regeneration-cache.json").exists())
        for name, content in self.immutable.items():
            self.assertEqual((self.out / name).read_bytes(), content, name)
        self.assertEqual(refresh.model_payload((self.out / "viewer.html").read_text()), self.payload)
        manifest = json.loads((self.out / "manifest.json").read_bytes())
        report = json.loads((self.out / "verification.json").read_bytes())
        self.assert_current_print_report_bindings()
        assert_verified_exports(self.out, manifest, report)
        self.assertEqual(report["mechanics"], self.report["mechanics"])
        transfer = report["presentation_refresh"]
        self.assertFalse(transfer["mechanical_validation_performed"])
        self.assertEqual(transfer["original_report_sha256"], self.original_report_hash)
        self.assertEqual(transfer["original_sources"], self.saved_sources)
        archive_path = self.out / "F2-kouzelny-kufr.zip"
        with zipfile.ZipFile(archive_path) as archive:
            self.assertEqual(archive.read("exports/viewer.html"), (self.out / "viewer.html").read_bytes())
            self.assertEqual(archive.read("viewer.js"), b"new UI")
            self.assertEqual(archive.getinfo("viewer.js").date_time, (1980, 1, 1, 0, 0, 0))
        self.assertEqual((self.out / "package-sha256.txt").read_text(),
                         digest(archive_path.read_bytes()) + "  F2-kouzelny-kufr.zip\n")

    def test_unchanged_sources_do_not_rewrite_outputs(self):
        before = self.hash_exports(export_names(self.manifest))
        self.assertFalse(self.run_refresh())
        self.assertEqual(self.hash_exports(export_names(self.manifest)), before)

    def test_manufacturing_drift_is_rejected_before_any_output_write(self):
        (self.root / "geometry.py").write_bytes(b"changed mechanical input")
        before = self.hash_exports(export_names(self.manifest))
        with self.assertRaisesRegex(ValueError, "Manufacturing sources changed"):
            self.run_refresh()
        self.assertEqual(self.hash_exports(export_names(self.manifest)), before)

    def test_tampered_verified_export_is_rejected(self):
        self.change_ui()
        (self.out / "stl/body.stl").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "Generated CAD changed"):
            self.run_refresh()
        self.assertEqual(digest((self.out / "verification.json").read_bytes()), self.original_report_hash)

    def test_hash_consistent_but_stale_analytical_manifest_binding_is_rejected(self):
        self.change_ui()
        write_json(self.out / "printability.json", {**self.analytical, "manifest_sha256": "f" * 64})
        generation = json.loads((self.out / GENERATION_SNAPSHOT).read_bytes())
        generation["sha256"] = self.hash_exports(generated_export_names(self.manifest))
        write_json(self.out / GENERATION_SNAPSHOT, generation)
        self.report["export_sha256"] = self.hash_exports(set(export_names(self.manifest)) - VERIFICATION_EXPORTS)
        write_json(self.out / "verification.json", self.report)
        write_json(self.out / "artifact-sha256.json", {
            **self.report["export_sha256"], "verification.json": digest((self.out / "verification.json").read_bytes()),
        })
        with self.assertRaisesRegex(ValueError, "Saved analytical printability evidence belongs to another manifest"):
            self.run_refresh()

    def test_invalid_saved_index_and_model_are_rejected(self):
        self.change_ui()
        write_json(self.out / "artifact-sha256.json", {})
        with self.assertRaisesRegex(ValueError, "artifact index"):
            self.run_refresh()
        for text in ("<script>const model={};", refresh.MODEL_MARKER + '{"meshes":[]}no-semicolon'):
            with self.assertRaises(ValueError):
                refresh.model_payload(text)

    def test_render_must_preserve_saved_geometry(self):
        self.change_ui()
        with patch.object(refresh, "render_document", return_value=self.render({"meshes": []})):
            with self.assertRaisesRegex(ValueError, "changed saved model geometry"):
                self.run_refresh()
        self.assertEqual(digest((self.out / "verification.json").read_bytes()), self.original_report_hash)

    def test_real_current_renderer_preserves_synthetic_saved_geometry(self):
        from viewer_document import render_document

        rendered = refresh.model_payload(render_document(self.payload))
        self.assertEqual(rendered["meshes"], self.payload["meshes"])
        self.assertEqual(rendered["twinManifest"], self.payload["twinManifest"])
        self.assertEqual(rendered["parameters"], self.payload["parameters"])
        self.assertIn("showManifest", rendered)

    def test_multiple_transfers_keep_original_validation_identity(self):
        self.change_ui()
        self.run_refresh()
        self.saved_sources = self.sources()
        previous_hash = digest((self.out / "verification.json").read_bytes())
        (self.root / "viewer.js").write_bytes(b"second UI")
        self.run_refresh()
        report = json.loads((self.out / "verification.json").read_bytes())
        self.assertEqual(report["presentation_refresh"]["original_report_sha256"], self.original_report_hash)
        self.assertEqual(report["presentation_refresh"]["previous_report_sha256"], previous_hash)

    def add_accepted_transports(self):
        for name in ("trusted-result.json", "F2-kouzelny-kufr.zip", "package-sha256.txt"):
            (self.out / name).write_bytes(b"old accepted transport")

    def assert_current_print_report_bindings(self):
        manifest_digest = digest((self.out / "manifest.json").read_bytes())
        for name in ("printability.json", "slicer-validation.json"):
            report = json.loads((self.out / name).read_bytes())
            self.assertEqual(report["manifest_sha256"], manifest_digest, name)
        analytical = json.loads((self.out / "printability.json").read_bytes())
        self.assertEqual(analytical, {**self.analytical, "manifest_sha256": manifest_digest})

    def assert_no_acceptance(self):
        for name in ("verification.json", "artifact-sha256.json", "regeneration-cache.json",
                     "trusted-result.json", "F2-kouzelny-kufr.zip", "package-sha256.txt"):
            self.assertFalse((self.out / name).exists(), name)

    def test_validation_refresh_rebinds_generation_but_removes_all_acceptance(self):
        original_generation = (self.out / GENERATION_SNAPSHOT).read_bytes()
        original_viewer = (self.out / "viewer.html").read_bytes()
        self.add_accepted_transports()
        (self.root / "verify.py").write_bytes(b"new verification gate")
        with patch.object(refresh, "render_document", side_effect=AssertionError("No presentation change")):
            self.assertTrue(refresh.refresh_generation(self.saved_sources))
        self.assert_no_acceptance()
        manifest = json.loads((self.out / "manifest.json").read_bytes())
        generation = json.loads((self.out / GENERATION_SNAPSHOT).read_bytes())
        slicing = json.loads((self.out / "slicer-validation.json").read_bytes())
        self.assertEqual(manifest["sources"], self.sources())
        self.assertEqual(generation["sources"], self.sources())
        self.assertEqual(slicing["manifest_sha256"], digest((self.out / "manifest.json").read_bytes()))
        self.assert_current_print_report_bindings()
        self.assertEqual(generation["sha256"], self.hash_exports(generated_export_names(manifest)))
        transfer = generation["validation_refresh"]
        self.assertTrue(transfer["verification_required"])
        self.assertEqual(transfer["changed_validation_sources"], ["verify.py"])
        self.assertEqual(transfer["original_generation_snapshot_sha256"], digest(original_generation))
        self.assertEqual(transfer["original_sources"], self.saved_sources)
        self.assertEqual((self.out / "viewer.html").read_bytes(), original_viewer)
        for name, content in self.immutable.items():
            self.assertEqual((self.out / name).read_bytes(), content, name)

    def test_validation_refresh_rejects_manufacturing_or_combined_presentation_drift(self):
        (self.root / "verify.py").write_bytes(b"new verifier")
        self.change_ui()
        before = self.hash_exports(export_names(self.manifest))
        with self.assertRaisesRegex(ValueError, "Generation sources changed; regenerate: viewer.js"):
            refresh.refresh_generation(self.saved_sources)
        self.assertEqual(self.hash_exports(export_names(self.manifest)), before)
        (self.root / "geometry.py").write_bytes(b"changed CAD")
        with self.assertRaisesRegex(ValueError, "Generation sources changed; regenerate: geometry.py"):
            refresh.refresh_generation(self.saved_sources)

    def test_validation_refresh_retires_independent_model_proof_without_rewriting_geometry(self):
        from export_model_bundle import write_model_bundle
        from model_verification import certify_bundle, evidence_for_success, write_generation_receipt
        from preview_ui_fixture import fixture_payload
        bundle = self.out / "model-release"
        payload = fixture_payload()
        payload["twinManifest"] = None
        write_model_bundle(payload, bundle, source_revision="synthetic-refresh-test")
        write_generation_receipt(bundle, self.root, self.out / "model-generation.json")
        synthetic_report = self.out / "synthetic-model-report.json"
        write_json(synthetic_report, {"result": "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE", "physical_test_performed": False,
                                    "sources": self.saved_sources, "modelEvidence": evidence_for_success(self.out, self.root, self.saved_sources)})
        certify_bundle(bundle, synthetic_report, self.root)
        geometry_before = (bundle / "model.json").read_bytes()
        (self.root / "verify.py").write_bytes(b"new verification gate")
        self.assertTrue(refresh.refresh_generation(self.saved_sources))
        self.assert_no_acceptance()
        model_manifest = json.loads((bundle / "model-bundle.json").read_bytes())
        self.assertEqual(model_manifest["provenance"]["cadVerification"], "not-verified")
        self.assertFalse((bundle / "cad-verification.json").exists())
        self.assertEqual((bundle / "model.json").read_bytes(), geometry_before)
        evidence_for_success(self.out, self.root, self.sources())

    def test_validation_publish_failure_cannot_leave_an_old_pass_or_archive(self):
        self.add_accepted_transports()
        (self.root / "verify.py").write_bytes(b"new verifier")
        original_replace = Path.replace

        def fail_snapshot_publish(path, target):
            if path.name == GENERATION_SNAPSHOT:
                raise OSError("simulated snapshot publication failure")
            return original_replace(path, target)

        with patch.object(Path, "replace", new=fail_snapshot_publish):
            with self.assertRaisesRegex(OSError, "simulated snapshot publication failure"):
                refresh.refresh_generation(self.saved_sources)
        self.assert_no_acceptance()

    def test_validation_refresh_requires_the_saved_hash_evidence(self):
        (self.root / "verify.py").write_bytes(b"new verifier")
        (self.out / "stl/body.stl").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "Generated CAD changed"):
            refresh.refresh_generation(self.saved_sources)
        self.assertEqual(digest((self.out / "verification.json").read_bytes()), self.original_report_hash)

    def test_validation_refresh_exact_hit_does_not_change_receipts(self):
        before = self.hash_exports(export_names(self.manifest))
        self.assertFalse(refresh.refresh_generation(self.saved_sources))
        self.assertEqual(self.hash_exports(export_names(self.manifest)), before)


if __name__ == "__main__":
    unittest.main()
