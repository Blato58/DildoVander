"""Topology regression checks without CAD or physical-hardware claims."""
import csv
import json
import math
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path

import harness


def segment_intersects_box(start, end, bounds):
    """Open-box intersection for orthogonal route centreline regression cases."""
    return all(max(min(start[axis], end[axis]), bounds[axis]) <
               min(max(start[axis], end[axis]), bounds[axis + 3])
               if start[axis] != end[axis] else bounds[axis] < start[axis] < bounds[axis + 3]
               for axis in range(3))


class HarnessTests(unittest.TestCase):
    def wires(self):
        return deepcopy(list(harness.HARNESSES))

    def test_base_plan_validates_and_keeps_physical_acceptance_open(self):
        result = harness.validate()
        self.assertEqual("PASS", result["status"])
        self.assertEqual("NOT PERFORMED", result["physical_verification"])
        self.assertEqual({"MAIN5", "DRIVE5", "GND"}, set(harness.NETS.values()))

    def test_degs_on_capacity_rejects_sixth_port_and_parallel_port_wires(self):
        terminals = deepcopy(harness.TERMINALS)
        terminals["degson_1"][6] = "amplifier_left:VIN"
        with self.assertRaisesRegex(ValueError, "capacity"):
            harness.validate(terminals=terminals)
        wires = self.wires()
        extra = deepcopy(next(wire for wire in wires if wire["id"] == "D1-3"))
        extra["id"] = "D1-3-extra"
        wires.append(extra)
        with self.assertRaisesRegex(ValueError, "exactly one wire"):
            harness.validate(wires=wires)

    def test_degs_on_rejects_crossed_nets_and_thin_dupont(self):
        for key, value in (("net", "DRIVE5"), ("area_mm2", 0.1)):
            wires = self.wires()
            next(wire for wire in wires if wire["id"] == "D1-3")[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "wrong net or thin"):
                harness.validate(wires=wires)

    def test_fanout_cannot_mix_main_and_drive(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "F_LED+-01")["net"] = "DRIVE5"
        with self.assertRaisesRegex(ValueError, "wrong fanout net"):
            harness.validate(wires=wires)

    def test_speaker_negative_and_u059_nc_remain_isolated(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "SPK-left--")["net"] = "GND"
        with self.assertRaisesRegex(ValueError, "speaker output grounded"):
            harness.validate(wires=wires)
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "U1-DIN")["to"] = "u059_1:white"
        with self.assertRaisesRegex(ValueError, "NC must stay isolated"):
            harness.validate(wires=wires)

    def test_six_servo_and_vibration_channels_cannot_be_swapped(self):
        for identifier in ("S6-S", "U6-DIN"):
            wires = self.wires()
            next(wire for wire in wires if wire["id"] == identifier)["channel"] = 0
            with self.subTest(identifier=identifier), self.assertRaisesRegex(ValueError, "wrong PCA channel"):
                harness.validate(wires=wires)

    def test_every_moving_load_has_free_loop_and_both_anchor_labels(self):
        wires = self.wires()
        loop = next(wire for wire in wires if wire["id"] == "RGB-3")
        self.assertEqual(16, loop["lift_mm"])
        self.assertGreaterEqual(loop["motion_mm"], 64)
        loop["motion_mm"] = 16
        with self.assertRaisesRegex(ValueError, "moving harness"):
            harness.validate(wires=wires)
        loop["motion_mm"] = 64
        loop["moving_anchor"] = None
        with self.assertRaisesRegex(ValueError, "moving harness"):
            harness.validate(wires=wires)

    def test_power_does_not_use_perfboard_and_lengths_cannot_be_negative(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "L3-FEED")["to"] = "perfboard_1:VCC"
        with self.assertRaisesRegex(ValueError, "power must not pass"):
            harness.validate(wires=wires)
        wires = self.wires()
        wires[0]["estimated_cut_mm"] = -1
        with self.assertRaisesRegex(ValueError, "invalid estimated length"):
            harness.validate(wires=wires)
        wires = self.wires()
        wires[0]["estimated_total_mm"] = 1
        with self.assertRaisesRegex(ValueError, "insufficient route"):
            harness.validate(wires=wires)

    def test_power_routes_to_moving_modules_use_outer_edge_before_front_loop(self):
        route = next(wire for wire in harness.HARNESSES if wire["id"] == "F_U123+-01")
        self.assertIn([121, 212, 45], route["points"])
        self.assertIn([121, 99, 45], route["points"])
        self.assertIn([106, 99, 45], route["points"])
        for wire in harness.HARNESSES:
            for start, end in zip(wire["points"], wire["points"][1:]):
                self.assertLessEqual(sum(a != b for a, b in zip(start, end)), 1)

    def test_left_cluster_power_avoids_vertical_bank_and_caps_keep_assigned_nets(self):
        for identifier in ("D1-1", "D1-2", "C1+", "C2-"):
            route = next(wire for wire in harness.HARNESSES if wire["id"] == identifier)
            self.assertIn([60, 212, 45], route["points"])
        for index in range(1, 7):
            edges = [edge for edge in harness.CONNECTIONS if edge["to"] == f"capacitor_{index}"]
            self.assertEqual(2, len(edges))
            self.assertEqual("degson_3", next(edge for edge in edges if edge["kind"] == "ground")["from"])
            positive = next(edge for edge in edges if edge["kind"] == "power")
            self.assertEqual("pca9685" if index in (3, 4) else "degson_1", positive["from"])

    def test_front_board_power_does_not_cross_actuators_from_rear(self):
        for identifier in ("D2-2", "D3-2", "F_LED+-01", "F_LED--01"):
            route = next(wire for wire in harness.HARNESSES if wire["id"] == identifier)
            self.assertIn([365, 212, 45], route["points"])
            self.assertIn([365, 27, 38], route["points"])

    def test_every_moving_endpoint_visits_its_reserved_loop_box(self):
        for wire in harness.HARNESSES:
            for endpoint in (wire["from"], wire["to"]):
                part = endpoint.split(":", 1)[0]
                if not part.startswith(("led_", "u059_")):
                    continue
                origin_x = 77 + 48 * (int(part.split("_")[-1]) - 1)
                inside = [point for point in wire["points"]
                          if origin_x + 17 <= point[0] <= origin_x + 41
                          and 86 <= point[1] <= 112 and 36 <= point[2] <= 60]
                with self.subTest(wire=wire["id"], endpoint=endpoint):
                    self.assertIn([origin_x + 29, 99, 45], inside)
                    self.assertIn([origin_x + 29, 99, 59], inside)

    def test_esp_signal_approach_stays_outside_right_speaker(self):
        for wire in harness.HARNESSES:
            if wire["kind"] != "signal" or not any(endpoint.startswith("esp32:") for endpoint in (wire["from"], wire["to"])):
                continue
            with self.subTest(wire=wire["id"]):
                self.assertIn([376, 50, 86], wire["points"])
                self.assertIn([376, 50, 38], wire["points"])
                self.assertIn([380, 50, 38], wire["points"])
                for start, end in zip(wire["points"], wire["points"][1:]):
                    if start[0] >= 403 and end[0] >= 403:
                        self.assertGreater(min(start[2], end[2]), 63)

    def test_service_shelf_tails_pass_through_provided_openings(self):
        left_upper = {"amplifier_left", "buck", "red_main", "green_drives", "white_ptt"}
        right_upper = {"esp32", "amplifier_right", "esp_usb_supply"}
        for wire in harness.HARNESSES:
            for endpoint in (wire["from"], wire["to"]):
                part = endpoint.split(":", 1)[0]
                if part not in left_upper | right_upper:
                    continue
                # Intact PD cable does not cross the service shelf.
                if wire["id"] == "USB-PD":
                    continue
                x, y = (65, 93) if part in left_upper else (376, 50)
                with self.subTest(wire=wire["id"], endpoint=endpoint):
                    self.assertTrue(any(point[:2] == [x, y] for point in wire["points"]))

    def test_right_power_exits_leg_rows_through_front_before_rear_trunk(self):
        for wire in harness.HARNESSES:
            if wire["kind"] not in ("power", "ground") or wire["id"] == "USB-ESP":
                continue
            if not any(endpoint.split(":", 1)[0] in ("amplifier_right", "esp_usb_supply", "esp32")
                       for endpoint in (wire["from"], wire["to"])):
                continue
            with self.subTest(wire=wire["id"]):
                self.assertIn([380, 27, 38], wire["points"])
                self.assertIn([365, 27, 38], wire["points"])
                if wire["net"] != "LOGIC3":
                    self.assertIn([365, 212, 45], wire["points"])

    def test_three_contact_rockers_have_no_external_fourth_led_positive_pin(self):
        endpoints = {endpoint for wire in harness.HARNESSES for endpoint in (wire["from"], wire["to"])}
        for rocker in ("red_main", "green_drives"):
            self.assertNotIn(f"{rocker}:LED+", endpoints)
            self.assertIn(f"{rocker}:LED-GND", endpoints)
            self.assertIn(f"{rocker}:IN", endpoints)
            self.assertIn(f"{rocker}:OUT", endpoints)
        self.assertEqual(["white_ptt:LED+"], harness.FANOUTS["F_PANEL+"]["loads"])

    def test_control_tails_exit_above_speaker_before_descending(self):
        for wire in harness.HARNESSES:
            for endpoint in (wire["from"], wire["to"]):
                part = endpoint.split(":", 1)[0]
                if part not in ("red_main", "green_drives", "white_ptt"):
                    continue
                a = harness.ANCHORS[part]
                with self.subTest(wire=wire["id"], endpoint=endpoint):
                    self.assertIn([a[0], 62, a[2]], wire["points"])
                    self.assertIn([a[0], 62, 84], wire["points"])
                    self.assertIn([65, 62, 84], wire["points"])
                    self.assertIn([65, 93, 84], wire["points"])
                    for start, end in zip(wire["points"], wire["points"][1:]):
                        self.assertFalse(segment_intersects_box(start, end, (17, 65, 81, 63, 96, 100)))

    def test_bank_usb_approaches_left_passage_between_amplifier_and_buck(self):
        wire = next(wire for wire in harness.HARNESSES if wire["id"] == "USB-PD")
        self.assertIn([32, 99, 84], wire["points"])
        self.assertIn([65, 99, 84], wire["points"])
        self.assertIn([65, 93, 84], wire["points"])
        for start, end in zip(wire["points"], wire["points"][1:]):
            self.assertFalse(segment_intersects_box(start, end, (42, 102, 81, 60, 120, 89)))
            self.assertFalse(segment_intersects_box(start, end, (17, 65, 81, 63, 96, 100)))

    def test_buck_negative_labels_alias_one_common_physical_terminal(self):
        self.assertEqual("buck:GND", harness.TERMINAL_ALIASES["buck:IN-"])
        self.assertEqual("buck:GND", harness.TERMINAL_ALIASES["buck:OUT-"])
        self.assertEqual("GND", harness.NET_ALIASES["PD_RETURN"])
        self.assertIn("One 1.5mm2 lead", harness.BUCK_GROUND_JOINT["construction"])

    def test_moving_endpoint_drops_before_horizontal_route_at_module_height(self):
        for wire in harness.HARNESSES:
            for key, reverse in (("from", False), ("to", True)):
                part = wire[key].split(":", 1)[0]
                if not part.startswith(("u059_", "led_")):
                    continue
                points = list(reversed(wire["points"])) if reverse else wire["points"]
                a = harness.ANCHORS[part]
                with self.subTest(wire=wire["id"], endpoint=wire[key]):
                    self.assertEqual(list(a), points[0])
                    self.assertEqual([a[0], a[1], 59], points[1])
                    if part.startswith("led_"):
                        origin_x = 77 + 48 * (int(part.split("_")[-1]) - 1)
                        self.assertGreaterEqual(a[0] - (origin_x + 45), 2)
                        self.assertLessEqual(a[0], origin_x + 48)

    def test_servo_tails_approach_above_cradle_and_detour_front_guide(self):
        for wire in harness.HARNESSES:
            part = wire["to"].split(":", 1)[0]
            if not part.startswith("servo_"):
                continue
            origin_x = 77 + 48 * (int(part.split("_")[-1]) - 1)
            with self.subTest(wire=wire["id"]):
                self.assertIn([origin_x + 26, 30, 55], wire["points"])
                self.assertIn([origin_x + 26, 30, 38], wire["points"])
                self.assertIn([origin_x + 26, 145, 55], wire["points"])
                self.assertIn([origin_x + 14, 145, 55], wire["points"])

    def test_right_upper_routes_clear_front_table_inner_leg(self):
        for wire in harness.HARNESSES:
            if not any(endpoint.split(":", 1)[0] in ("amplifier_right", "esp32", "esp_usb_supply")
                       for endpoint in (wire["from"], wire["to"])):
                continue
            for start, end in zip(wire["points"], wire["points"][1:]):
                with self.subTest(wire=wire["id"], start=start, end=end):
                    self.assertFalse(segment_intersects_box(start, end, (371, 29, 12, 377, 35, 68)))

    def test_pca_capacitor_tails_detour_the_other_capacitor(self):
        for index in (3, 4):
            other_x = 187 if index == 3 else 174
            for suffix in ("+", "-"):
                wire = next(wire for wire in harness.HARNESSES if wire["id"] == f"C{index}{suffix}")
                target_x = harness.ANCHORS[f"capacitor_{index}"][0]
                self.assertIn([target_x, 35, 23], wire["points"])
                self.assertIn([target_x, 55, 23], wire["points"])
                for start, end in zip(wire["points"], wire["points"][1:]):
                    self.assertFalse(segment_intersects_box(start, end,
                                                           (other_x - 5, 50, 17, other_x + 5, 60, 34)))

    def test_pca_capacitors_clear_mounting_nut_with_radius_allowance(self):
        nut_center = (181, 43)
        capacitor_radius, nut_radius = 5, 3.4
        for index in (3, 4):
            x, y, z = harness.ANCHORS[f"capacitor_{index}"]
            with self.subTest(capacitor=index):
                self.assertEqual((55, 17), (y, z))
                self.assertGreater(math.hypot(x - nut_center[0], y - nut_center[1]),
                                   capacitor_radius + nut_radius)

    def test_servo_rise_is_behind_front_cover_fascia(self):
        fascia = (75, 25, 45, 368, 27.1, 108)
        for wire in harness.HARNESSES:
            if not wire["to"].startswith("servo_"):
                continue
            for start, end in zip(wire["points"], wire["points"][1:]):
                with self.subTest(wire=wire["id"], start=start, end=end):
                    self.assertFalse(segment_intersects_box(start, end, fascia))

    def test_pca_capacitor_horizontal_tails_stay_above_base_edge(self):
        base_edge = (173, 34, 13, 176, 246, 17.1)
        for identifier in ("C3+", "C3-", "C4+", "C4-"):
            wire = next(wire for wire in harness.HARNESSES if wire["id"] == identifier)
            for start, end in zip(wire["points"], wire["points"][1:]):
                if start[2] != end[2]:
                    continue  # local capacitor endpoint descent is checked by CAD
                with self.subTest(wire=identifier, start=start, end=end):
                    self.assertFalse(segment_intersects_box(start, end, base_edge))

    def test_right_service_approach_clears_full_height_rear_support(self):
        right_endpoints = {"amplifier_right", "esp32", "esp_usb_supply", "speaker_right"}
        support = (371, 120, 12, 377, 126, 105)
        conservative_support = (support[0], support[1] - 0.1, *support[2:])
        for wire in harness.HARNESSES:
            if not any(endpoint.split(":", 1)[0] in right_endpoints
                       for endpoint in (wire["from"], wire["to"])):
                continue
            for start, end in zip(wire["points"], wire["points"][1:]):
                with self.subTest(wire=wire["id"], start=start, end=end):
                    # The Y120 face is included: a finite-thickness cable there
                    # already overlaps the support even if its line touches it.
                    self.assertFalse(segment_intersects_box(start, end, conservative_support))
            for endpoint in (wire["from"], wire["to"]):
                part = endpoint.split(":", 1)[0]
                if part in right_endpoints:
                    a = harness.ANCHORS[part]
                    self.assertIn([380, a[1], a[2]], wire["points"])
                    self.assertIn([380, 50, a[2]], wire["points"])

    def test_export_is_complete_and_routes_are_copies(self):
        with tempfile.TemporaryDirectory() as directory:
            harness.export(directory)
            payload = json.loads((Path(directory) / "cable-plan.json").read_text(encoding="utf-8"))
            with (Path(directory) / "cable-cut-list.csv").open(encoding="utf-8-sig", newline="") as stream:
                cuts = list(csv.DictReader(stream))
            self.assertEqual(len(harness.HARNESSES), len(cuts))
            self.assertEqual(len(cuts), len(payload["harnesses"]))
            self.assertTrue(any("adapter" in gap for gap in payload["missing_acceptance"]))
            self.assertTrue(all(row["label_from"] and row["label_to"] for row in cuts))
        routes = harness.routes()
        routes[0]["points"][0][0] = -100
        self.assertNotEqual(-100, harness.HARNESSES[0]["points"][0][0])


if __name__ == "__main__":
    unittest.main()
