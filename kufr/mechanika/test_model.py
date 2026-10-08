"""Regression checks for linkage closure, admissible sizing and printed assemblies."""
import copy
import json
import math
from pathlib import Path
import unittest

from model import build, linkage_state, validate_config, verify_travel, verify_electronics


class LinkageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = json.loads(Path(__file__).with_name("parametry.json").read_text(encoding="utf-8-sig"))

    def test_closed_monotonic_presentation_and_reverse_return(self):
        p = self.config
        states = [linkage_state(p, p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * i / 100)
                  for i in range(101)]
        self.assertAlmostEqual(states[0]["tilt_deg"], 0, places=8)
        self.assertGreater(states[-1]["tilt_deg"], 5)
        self.assertLess(states[-1]["tilt_deg"], 15)
        self.assertTrue(all(b["tilt_deg"] >= a["tilt_deg"] - 1e-8 for a, b in zip(states, states[1:])))
        self.assertTrue(all(s["closure_error_mm"] < 1e-8 and s["toggle_margin_mm"] > 5 for s in states))
        for i, state in enumerate(reversed(states)):
            reverse = linkage_state(p, p["angle_high_deg"] - (p["angle_high_deg"] - p["angle_low_deg"]) * i / 100)
            self.assertAlmostEqual(reverse["attachment_z_mm"], state["attachment_z_mm"], places=8)

    def test_reject_oversize_head_without_silently_shrinking_it(self):
        p = copy.deepcopy(self.config)
        p["wand_head_diameter_mm"] = 44
        with self.assertRaisesRegex(ValueError, "Decorative head"):
            validate_config(p)

    def test_reject_neighbouring_tray_overlap(self):
        p = copy.deepcopy(self.config)
        p["compartment_pitch_mm"] = p["tray_head_width_mm"]
        with self.assertRaisesRegex(ValueError, "Neighbouring"):
            validate_config(p)

    def test_reject_nonclosing_or_inverted_linkage(self):
        p = copy.deepcopy(self.config)
        angle = math.radians(p["angle_high_deg"])
        p["servo_axis_y_mm"] = p["hinge_y_mm"] - p["crank_radius_mm"] * math.cos(angle)
        p["servo_axis_height_mm"] = p["hinge_axis_z_mm"] - p["crank_radius_mm"] * math.sin(angle)
        with self.assertRaises(ValueError):
            linkage_state(p, p["angle_high_deg"])


class GeometryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.p = json.loads(Path(__file__).with_name("parametry.json").read_text(encoding="utf-8-sig"))
        cls.parts, cls.items, cls.purchased, cls.mechanics = build(cls.p)

    def test_all_printed_parts_connected_and_valid(self):
        for name, shape in self.parts.items():
            with self.subTest(part=name):
                self.assertTrue(shape.isValid())
                self.assertEqual(len(shape.Solids()), 1)
                self.assertGreater(shape.Volume(), 0)

    def test_complete_moving_hardware_envelopes_clear_the_frame(self):
        samples = verify_travel(self.p, self.items + self.purchased)
        self.assertEqual(len(samples), 21)

    def test_lift_report_matches_rotated_front_edge(self):
        p, m = self.p, self.mechanics
        expected = (p["hinge_y_mm"] + p["tray_length_mm"] / 2) * math.sin(math.radians(m["tilt_deg"]))
        self.assertAlmostEqual(m["lift_mm"], expected, places=8)
        self.assertGreater(expected, 25)


class ElectronicsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        from electronics import build_electronics
        cls.p = json.loads(Path(__file__).with_name("parametry.json").read_text(encoding="utf-8-sig"))
        cls.parts, cls.items, cls.hardware, cls.metadata = build_electronics(cls.p)

    def test_two_speakers_and_two_independent_amplifier_carriers(self):
        self.assertEqual(sum(i["name"] == "speaker_open_tower" for i in self.items), 2)
        self.assertEqual(sum(i["name"] == "amplifier_universal_carrier" for i in self.items), 2)
        self.assertLess(self.metadata["connector_design_distance_mm"], 30)
        self.assertGreater(min(self.metadata["speaker_to_microphone_distance_mm"]), 300)

    def test_component_and_fastener_head_clearances(self):
        checks = verify_electronics(self.items + self.hardware, self.metadata)
        self.assertEqual(checks["fastener_head_clearances"], "PASS")
        self.assertGreater(checks["fastener_heads_checked"], 50)


if __name__ == "__main__":
    unittest.main()
