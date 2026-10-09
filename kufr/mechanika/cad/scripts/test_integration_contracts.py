"""Regressions for hinge connectivity, posing and regeneration provenance."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import bounds, load_parameters, overlap
from reference.case import build, hinge_axis
from printable import frame, mechanics
from scripts import generate, verify_delivery


class IntegrationContracts(unittest.TestCase):
    def test_cancelled_delivery_invalidates_archive_and_pass_receipt(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            exports = root / "exports"
            exports.mkdir()
            archive = exports / "kufr-complete-provisional.zip"
            digest = archive.with_suffix(".zip.sha256")
            receipt = exports / "delivery-validation.json"
            archive.write_bytes(b"partial archive")
            digest.write_text("stale digest")
            receipt.write_text('{"status":"PASS"}')
            with patch.object(verify_delivery, "ROOT", root), patch.object(verify_delivery, "main", side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):
                    verify_delivery.run()
            self.assertFalse(archive.exists())
            self.assertFalse(digest.exists())
            self.assertEqual(json.loads(receipt.read_text())["status"], "FAIL")

    def test_service_heads_are_flush_over_both_rail_and_wood_brace(self):
        p = load_parameters()
        parts, items, metadata = frame.build(p, [[7.4, 7.4, 8], [41, 120.6, 8]])
        self.assertEqual({f["head_bottom_z_mm"] for f in metadata["service_floor_fasteners"]}, {0, 5})
        body, _, _ = build(p)
        for bolt in [i for i in items if i.kind == "metal"]:
            for obstacle in [i for i in items if i.part] + body:
                self.assertLess(overlap(bolt.shape, obstacle.shape), .01, (bolt.name, obstacle.name))
        self.assertTrue(all(s.isValid() and len(s.Solids()) == 1 for s in parts.values()))

    def test_fixed_hinge_has_connected_material_reaching_pin(self):
        p = load_parameters()
        body, lid, _ = build(p)
        axis = hinge_axis(p)
        for leaf in [i for i in body + lid if "leaf" in i.name]:
            self.assertTrue(leaf.shape.isValid())
            self.assertEqual(len(leaf.shape.Solids()), 1)
        for fixed in [i for i in body if "fixed_leaf" in i.name]:
            self.assertGreater(bounds(fixed.shape)[4], axis[1])
            pin = next(i for i in body if i.name == fixed.name.replace("fixed_leaf", "pin"))
            self.assertLess(fixed.shape.intersect(pin.shape).Volume(), 1e-6)

    def test_selected_module_reaches_raised_pose(self):
        p = load_parameters()
        _, home, metadata = mechanics.build(p)
        raised = mechanics.pose(p, home, metadata["parameters"]["angle_high_deg"], module=3)
        before = {i.name: bounds(i.shape) for i in home}
        target = [i for i in raised if i.metadata["module"] == 3 and "wand_decorative" in i.name]
        self.assertEqual(len(target), 1)
        self.assertGreater(bounds(target[0].shape)[5] - before[target[0].name][5], 20)
        for item in raised:
            if item.metadata["module"] != 3:
                for a, b in zip(before[item.name], bounds(item.shape)):
                    self.assertAlmostEqual(a, b, places=7)

    def test_regeneration_removes_obsolete_geometry_and_skipped_renders_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for relative in ["exports/stl/obsolete.stl", "exports/step/old.step", "renders/old.png", "renders/viewer.html", "components/outdated.step", "assemblies/previous.step", "reference/body.step", "printable/source.py", "docs/assembly.md"]:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("fixture", encoding="utf-8")
            with patch.object(generate, "ROOT", root):
                generate.clear_previous_generated_geometry()
            self.assertFalse((root / "exports/stl/obsolete.stl").exists())
            self.assertFalse((root / "renders/old.png").exists())
            self.assertFalse((root / "renders/viewer.html").exists())
            self.assertTrue((root / "reference/body.step").exists())
            self.assertTrue((root / "printable/source.py").exists())
            self.assertTrue((root / "docs/assembly.md").exists())


if __name__ == "__main__":
    unittest.main()
