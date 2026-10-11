"""Bounded F2 sliding-peg BREP checks; no exports or physical-fit claims."""
import math
import unittest

import mechanism
from check_fit import collisions
from geometry import PARAMS, bounds, print_position


class SlidingFollowerTests(unittest.TestCase):
    def test_peg_is_integral_and_original_stack_is_absent(self):
        tray = mechanism.tray()
        peg = mechanism.sliding_follower()
        self.assertTrue(tray.isValid())
        self.assertEqual(len(tray.Solids()), 1)
        self.assertAlmostEqual(tray.intersect(peg).Volume(), peg.Volume(), places=5)
        removed = ("roller_", "follower_head_", "follower_sleeve_", "follower_spacer_",
                   "follower_washer_", "follower_nut_", "follower_pin_")
        self.assertFalse(any(item.name.startswith(removed) for item in mechanism.module()))
        m = PARAMS["mechanism"]
        expected = [m["sliding_follower_tip_x"], 109, -m["follower_drop"]-3,
                    m["sliding_follower_root_x"], 115, -m["follower_drop"]+3]
        for actual, target in zip(bounds(peg), expected):
            self.assertAlmostEqual(actual, target, places=6)

    def test_peg_clears_actual_cam_at_every_degree_and_remains_captured(self):
        m = PARAMS["mechanism"]
        self.assertAlmostEqual(m["groove_width"]-m["sliding_follower_diameter"], .8)
        self.assertAlmostEqual(m["sliding_follower_tip_x"]-(m["cam_x"]+m["cam_thickness"]-m["groove_depth"]), 2.5)
        self.assertAlmostEqual(m["sliding_follower_root_x"]-(m["cam_x"]+m["cam_thickness"]), 1)
        for angle in range(161):
            with self.subTest(angle=angle):
                radius = mechanism.cam_law(angle)[0]
                tray_z = m["cam_z"]+radius+m["follower_drop"]
                peg = mechanism.sliding_follower().translate((0, 0, tray_z))
                cam = mechanism.cam().rotate((0, 0, 0), (1, 0, 0), angle).translate(
                    (m["cam_x"], m["cam_y"], m["cam_z"]))
                self.assertLess(peg.intersect(cam).Volume(), .001)
                if angle in (0, 160):
                    # Both walls stop radial escape; the two circular dwells
                    # retain positive return rather than relying on friction.
                    for displacement in (-.6, .6):
                        self.assertGreater(peg.translate((0, 0, displacement)).intersect(cam).Volume(), .1)
        self.assertEqual(mechanism.cam_law(160)[0]-mechanism.cam_law(0)[0], 16)
        for angle in range(-19, 20):
            self.assertEqual(mechanism.cam_law(angle)[1], 0)
            self.assertEqual(mechanism.cam_law(angle+160)[1], 0)

    def test_complete_single_module_and_lower_stops(self):
        for angle in (0, 80, 160):
            with self.subTest(angle=angle):
                self.assertEqual(collisions(mechanism.module(0, angle)), [])
        m = PARAMS["mechanism"]
        tray = mechanism.tray().translate((0, 0, m["cam_z"]+m["cam_low_radius"]+m["follower_drop"]))
        self.assertLess(mechanism.base().intersect(tray).Volume(), .03)
        contact_area = mechanism.base().intersect(tray.translate((0, 0, -.01))).Volume()/.01
        self.assertGreater(contact_area, 80)

    def test_side_print_puts_peg_axis_up_and_fits_bed(self):
        part = next(part for part in mechanism.parts() if part.name == "wand_bed")
        self.assertEqual(part.rotation, ((0, 1, 0), 90))
        rotated_peg = mechanism.sliding_follower().rotate((0, 0, 0), *part.rotation)
        # The rounded entry cap faces upward. This is evidence for the peg's
        # actual axis, not merely a bounding-box fit of the complete tray.
        top_cap = max((face for face in rotated_peg.Faces() if face.geomType() == "PLANE"),
                      key=lambda face: face.Center().z)
        self.assertAlmostEqual(top_cap.normalAt().z, 1, places=6)
        self.assertAlmostEqual(top_cap.Center().z, -PARAMS["mechanism"]["sliding_follower_tip_x"], places=6)
        printed = print_position(part.shape, part.rotation)
        box = bounds(printed)
        brim = 2*PARAMS["print"]["brim"]
        self.assertLessEqual(box[3]+brim, PARAMS["print"]["bed_x"])
        self.assertLessEqual(box[4]+brim, PARAMS["print"]["bed_y"])
        self.assertAlmostEqual(box[2], 0, places=6)

    def test_four_m3x14_mounts_reach_each_captive_hex_nut(self):
        items = {item.name: item for item in mechanism.module()}
        for number in range(1, 5):
            with self.subTest(screw=number):
                shaft = items[f"module_m3_1_{number}"].shape
                head = items[f"module_head_1_{number}"].shape
                nut = items[f"module_nut_1_{number}"].shape
                shaft_box, head_box, nut_box = bounds(shaft), bounds(head), bounds(nut)
                self.assertAlmostEqual(shaft_box[2], 2)
                self.assertAlmostEqual(shaft_box[5], 16)
                self.assertAlmostEqual(head_box[2], 16)
                self.assertAlmostEqual(nut_box[2], PARAMS["frame"]["floor_anchors"]["module_nut_z"])
                self.assertAlmostEqual(nut_box[5], 5)
                self.assertAlmostEqual(nut_box[4]-nut_box[1], 5.5)
                self.assertAlmostEqual(nut_box[3]-nut_box[0], 5.5/math.cos(math.pi/6))
                self.assertLess(shaft.intersect(nut).Volume(), .001)
                self.assertLessEqual(shaft_box[2], nut_box[2])
                self.assertGreaterEqual(shaft_box[5], nut_box[5])


if __name__ == "__main__":
    unittest.main()
