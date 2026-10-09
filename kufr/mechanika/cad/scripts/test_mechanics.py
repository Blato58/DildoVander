"""Geometry regression checks for the measured-case six-module mechanism."""
import sys
from pathlib import Path
import unittest
import copy

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import bounds, box, load_parameters, overlap
from printable.mechanics import build, mechanics_parameters, pose, print_shape, verification, transport
from scripts.validate import pairs


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
        pads = [i for i in self.items if i.part == "module_foot" and i.metadata["module"] == 1]
        self.assertEqual(len(pads), 2)
        self.assertEqual(sorted(round(i.shape.BoundingBox().center.y, 2) for i in pads), [23.35, 243.35])
        self.assertTrue(all(abs(bounds(i.shape)[2] - 2) < 1e-6 for i in pads))

    def test_long_parts_have_a_real_bed_contact_floor(self):
        for name in ("module_chassis", "wand_tray"):
            printed = print_shape(name, self.parts[name], self.p)
            slab = box(250, 250, .2)
            # Reject a normalization that puts a lone lug on the bed and leaves
            # the actual long deck suspended above it.
            self.assertGreater(printed.intersect(slab).Volume() / .2, 8000, name)

    def test_empty_transport_wraps_clear_leds_and_fit_neighbours(self):
        travel = transport(self.p, self.items)
        self.assertFalse(any(i.kind == "wand" for i in travel))
        self.assertEqual(len([i for i in travel if i.group == "transport"]), 6)
        self.assertEqual(pairs(travel)["collisions"], [])

    def test_wand_parameter_changes_the_actual_envelope(self):
        p = copy.deepcopy(self.p)
        p["mechanism"]["wand_length"] = 190
        _, items, _ = build(p)
        wand = [bounds(i.shape) for i in items if i.kind == "wand" and i.metadata["module"] == 1]
        self.assertAlmostEqual(max(b[4] for b in wand) - min(b[1] for b in wand), 190)
        p["mechanism"]["wand_length"] = 300
        with self.assertRaises(ValueError):
            build(p)

    def test_changed_base_height_retains_wood_contact(self):
        p = copy.deepcopy(self.p)
        p["mechanism"]["base_z"] = 12
        _, items, _ = build(p)
        pads = [i for i in items if "case_hook_loop" in i.name]
        self.assertEqual(len(pads), 12)
        self.assertTrue(all(abs(bounds(i.shape)[2]) < 1e-6 for i in pads))
        feet = [i for i in items if i.part == "module_foot"]
        self.assertTrue(all(abs(bounds(i.shape)[2] - 2) < 1e-6 for i in feet))


if __name__ == "__main__":
    unittest.main()
