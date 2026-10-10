"""The package acceptance gate must reject partial or stale wire audits."""
from copy import deepcopy
import unittest
import tempfile
from pathlib import Path
import json

from verify import require_complete_wire_audit, require_anchor_assumption_screen
from artifacts import ASSEMBLY_NAMES, GENERATION_SNAPSHOT


class AnchorAcceptanceTests(unittest.TestCase):
    def test_failed_or_missing_anchor_screen_cannot_receive_cad_acceptance(self):
        for result in ({}, {"normal_load_assumption_screen_passed": False}):
            with self.subTest(result=result), self.assertRaises(AssertionError):
                require_anchor_assumption_screen(result)
        require_anchor_assumption_screen({"normal_load_assumption_screen_passed": True})


class WireAcceptanceTests(unittest.TestCase):
    def complete(self):
        return {
            "status": "PASS", "collisions": [], "bounds_failures": [],
            "sources_unchanged_during_audit": True, "truncated": False,
            "bundle_fit": {"status": "PASS"},
            "self_geometry": {"status": "PASS", "collisions": [],
                              "coverage": {"rest_wire_count": 135, "transport_wire_count": 135,
                                           "motion_angles": [0, 80, 160], "modules": list(range(1, 7))}},
            "coverage": {"rest_covers": True, "transport": True,
                         "motion_angles": [0, 80, 160], "modules": list(range(1, 7)),
                         "capsule_elbows": True},
        }

    def test_complete_sampled_audit_is_accepted(self):
        require_complete_wire_audit(self.complete())

    def test_nominal_pass_does_not_hide_failed_or_incomplete_checks(self):
        changes = [
            ("status", "STALE"), ("collisions", [{"wire": "S1-S"}]),
            ("bounds_failures", [{"wire": "S1-S"}]),
            ("sources_unchanged_during_audit", False), ("truncated", True),
            ("bundle_fit", {"status": "FAIL"}),
            ("self_geometry", {"status": "FAIL"}),
        ]
        for key, value in changes:
            with self.subTest(key=key):
                result = self.complete()
                result[key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)

    def test_self_geometry_cannot_omit_transport_or_motion(self):
        for key, value in [("rest_wire_count", 134), ("transport_wire_count", 0),
                           ("motion_angles", [0]), ("modules", [1])]:
            with self.subTest(self_coverage=key):
                result = self.complete()
                result["self_geometry"]["coverage"][key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)
        for key, value in [("rest_covers", False), ("transport", False),
                           ("motion_angles", [0]), ("modules", [1]),
                           ("capsule_elbows", False)]:
            with self.subTest(coverage=key):
                result = deepcopy(self.complete())
                result["coverage"][key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)


class ExportAcceptanceTests(unittest.TestCase):
    manifest = {"assemblies": list(ASSEMBLY_NAMES), "parts": [{"name": "body_1"}]}

    def test_prior_source_outputs_are_rejected_by_verification_even_with_new_manifest_sources(self):
        from test_packaging import generate_fixture, verify_fixture
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            sources=generate_fixture(root,self.manifest)
            (root/'input.py').write_bytes(b'next revision')
            # Updating only the manifest must not make the previous viewer/CSV/images current.
            path=root/'exports/manifest.json'
            manifest=json.loads(path.read_bytes())
            manifest['sources']=sources()
            path.write_text(json.dumps(manifest),encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'stale sources'):
                verify_fixture(root,sources)
            self.assertFalse((root/'exports/verification.json').exists())

    def test_modified_generation_outputs_are_rejected_before_audit(self):
        from test_packaging import generate_fixture, verify_fixture
        for changed in ('viewer.html','print-bom.csv','images/open.png'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                root=Path(directory).resolve()
                sources=generate_fixture(root,self.manifest)
                (root/'exports'/changed).write_bytes(b'changed before verification')
                with self.assertRaisesRegex(ValueError,'changed after snapshot'):
                    verify_fixture(root,sources)
                self.assertFalse((root/'exports/verification.json').exists())

    def test_incomplete_generator_cannot_bless_preexisting_output(self):
        from test_packaging import generate_fixture
        for omitted in ('viewer.html','print-bom.csv','images/open.png'):
            with self.subTest(omitted=omitted), tempfile.TemporaryDirectory() as directory:
                root=Path(directory).resolve()
                generate_fixture(root,self.manifest)
                (root/'exports/verification.json').write_bytes(b'old acceptance')
                stale=(root/'exports'/omitted).read_bytes()
                unrelated=root/'exports/obsolete.txt'
                unrelated.write_bytes(b'keep')
                with self.assertRaises(FileNotFoundError):
                    generate_fixture(root,self.manifest,omitted=[omitted])
                self.assertFalse((root/'exports'/GENERATION_SNAPSHOT).exists())
                self.assertFalse((root/'exports/verification.json').exists())
                self.assertEqual((root/'exports'/omitted).read_bytes(),stale)
                self.assertEqual(unrelated.read_bytes(),b'keep')

    def test_export_change_during_audit_cannot_receive_verified_hash(self):
        from test_packaging import generate_fixture, verify_fixture
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            sources=generate_fixture(root,self.manifest)
            def replace_viewer():
                (root/'exports/viewer.html').write_bytes(b'changed during audit')
            with self.assertRaisesRegex(ValueError,'changed after snapshot'):
                verify_fixture(root,sources,during_audit=replace_viewer)
            self.assertFalse((root/'exports/verification.json').exists())

    def test_missing_generation_receipt_rejects_preexisting_outputs(self):
        from test_packaging import generate_fixture, verify_fixture
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            sources=generate_fixture(root,self.manifest)
            (root/'exports'/GENERATION_SNAPSHOT).unlink()
            with self.assertRaises(FileNotFoundError):
                verify_fixture(root,sources)
            self.assertFalse((root/'exports/verification.json').exists())

if __name__ == "__main__":
    unittest.main()
