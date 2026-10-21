"""Parameter-only test executes the real mechanism law without importing CAD."""
import ast
import copy
import hashlib
import json
import math
from pathlib import Path
import types
import unittest

from twin_manifest import build_manifest, canonical_bytes, geometry_digest

ROOT = Path(__file__).resolve().parent


class TwinManifestTests(unittest.TestCase):
    def setUp(self):
        self.parameters = json.loads((ROOT / "parameters.json").read_text(encoding="utf-8"))
        self.effects = json.loads((ROOT / "effects.json").read_text(encoding="utf-8"))
        tree = ast.parse((ROOT / "mechanism.py").read_text(encoding="utf-8"))
        functions = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name in ("smooth", "cam_law")]
        namespace = {"M": self.parameters["mechanism"], "math": math}
        exec(compile(ast.Module(body=functions, type_ignores=[]), "mechanism.py", "exec"), namespace)
        self.mechanism = types.SimpleNamespace(cam_law=namespace["cam_law"])
        self.names = [f"{name}_{slot}" for slot in range(1, 7)
                      for name in ("tray", "tray_left", "cam", "servo", "u059", "led", "wand", "stock_horn")]

    def build(self):
        return build_manifest(self.parameters, self.mechanism, self.effects, self.names)

    def test_stable_identity_and_authoritative_channels_pivots_law(self):
        manifest = self.build()
        self.assertEqual(manifest, self.build())
        identity = dict(manifest)
        revision = identity.pop("revision")
        self.assertEqual(revision, hashlib.sha256(canonical_bytes(identity)).hexdigest())
        for index, slot in enumerate(manifest["slots"]):
            self.assertEqual(slot["channels"], {"servoPca": index, "vibrationPca": index+6,
                                                "ledStart": index*6, "ledCount": 6})
            self.assertEqual(slot["camPivotMm"], [100+index*48, 133, 49])
            self.assertEqual(slot["servoPivotMm"], slot["camPivotMm"])
            self.assertIn(f"tray_left_{index+1}", slot["liftComponents"])
        samples = manifest["kinematics"]["samples"]
        self.assertEqual(samples[0]["liftMm"], 0)
        self.assertEqual(samples[-1]["liftMm"], self.parameters["mechanism"]["lift"])
        self.assertTrue(all(a["liftMm"] <= b["liftMm"] for a, b in zip(samples, samples[1:])))

    def test_missing_mesh_is_not_silently_remapped(self):
        self.names.remove("servo_4")
        with self.assertRaisesRegex(ValueError, "slot 4"):
            self.build()

    def test_effect_timing_does_not_change_cad_revision(self):
        before = self.build()["revision"]
        self.effects["main"][0]["duration_ms"] += 1
        self.assertEqual(before, self.build()["revision"])
        self.parameters["mechanism"]["pitch"] += 1
        self.assertNotEqual(before, self.build()["revision"])

    def test_incompatible_profile_is_rejected(self):
        self.effects["count"] = 5
        with self.assertRaises(ValueError):
            self.build()

    def test_audio_mapping_uses_only_supplied_real_cad_components(self):
        self.names.extend(["speaker_left", "amplifier_left", "amplifier_right", "speaker_not_in_parameters"])
        self.assertEqual(self.build()["audio"]["speakers"], ["speaker_left"])
        self.assertEqual(self.build()["audio"]["amplifiers"], ["amplifier_left", "amplifier_right"])
        self.names.remove("speaker_left")
        self.assertEqual(self.build()["audio"]["speakers"], [])

    def test_geometry_digest_order_independent_and_vertex_change_changes_revision(self):
        meshes = [{"name": "tray_1", "vertices": [0, 0, 0], "triangles": [0, 1, 2]},
                  {"name": "cam_1", "vertices": [1, 2, 3], "triangles": [0, 1, 2]}]
        digest = geometry_digest(meshes)
        self.assertEqual(digest, geometry_digest(list(reversed(meshes))))
        changed = copy.deepcopy(meshes)
        changed[0]["vertices"][2] = 0.001
        self.assertNotEqual(digest, geometry_digest(changed))
        first = build_manifest(self.parameters, self.mechanism, self.effects, self.names, geometry_digest=digest)
        second = build_manifest(self.parameters, self.mechanism, self.effects, self.names, geometry_digest=geometry_digest(changed))
        self.assertEqual(first["provenance"]["geometryDigest"], digest)
        self.assertNotEqual(first["revision"], second["revision"])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            geometry_digest([meshes[0], meshes[0]])


if __name__ == "__main__":
    unittest.main()
