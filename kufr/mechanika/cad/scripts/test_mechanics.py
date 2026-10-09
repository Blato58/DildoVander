"""Geometry regression checks for the measured-case six-module mechanism."""
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import bounds, load_parameters, overlap
from printable.mechanics import build, mechanics_parameters, pose, print_shape, verification


class MechanicsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.p = load_parameters()
        cls.parts, cls.items, cls.metadata = build(cls.p)

    def test_actual_envelopes_and_six_unique_modules(self):
        parameters = mechanics_parameters(self.p)
        self.assertEqual(parameters["servo_body_xyz_mm"], [27, 22, 11.5])
        self.assertEqual(parameters["vibration_body_xyz_mm"], [32, 24, 8])
        self.assertEqual(len({i.name for i in self.items}), len(self.items))
        self.assertEqual({i.metadata["module"] for i in self.items}, set(range(1, 7)))
        self.assertEqual(sum("led_chip" in i.name for i in self.items), 36)
        self.assertTrue(all(i.part in self.parts for i in self.items if i.kind == "print"))

    def test_stowed_height_closure_and_collision_samples(self):
        report = verification(self.p)
        self.assertEqual(report["closure_samples"], 101)
        self.assertEqual(report["solid_collision_samples"], 21)
        self.assertGreater(report["stowed_lid_clearance_mm"], 3)
        self.assertGreater(report["neighbour_sweep_gap_mm"], 0)

    def test_optional_single_module_pose_and_home_idempotence(self):
        parameters = mechanics_parameters(self.p)
        home = pose(self.p, self.items, parameters["angle_low_deg"])
        raised = pose(self.p, self.items, parameters["angle_high_deg"], module=2)
        for source, at_home, at_high in zip(self.items, home, raised):
            for a, b in zip(bounds(source.shape), bounds(at_home.shape)):
                self.assertAlmostEqual(a, b, places=6)
            if source.metadata["module"] != 2:
                self.assertIs(source, at_high)

    def test_flat_link_has_vertical_holes_and_thickness_on_bed(self):
        link = print_shape("servo_link", self.parts["servo_link"], self.p)
        b = link.BoundingBox()
        self.assertAlmostEqual(b.zmin, 0, places=6)
        self.assertAlmostEqual(b.zlen, 4, places=6)

    def test_front_and_rear_floor_attachment_positions(self):
        pads = [i for i in self.items if i.part == "base_floor_pad" and i.metadata["module"] == 1]
        self.assertEqual(len(pads), 2)
        self.assertEqual(sorted(round(i.shape.BoundingBox().center.y, 2) for i in pads), [23.35, 243.35])
        self.assertTrue(all(abs(bounds(i.shape)[2] - 2) < 1e-6 for i in pads))


if __name__ == "__main__":
    unittest.main()
