"""Analytic load-model and two-piece clearance-coupon regression checks."""
import math
import unittest

import follower_load
import layout
from geometry import PARAMS, bounds, print_position


class FollowerLoadTests(unittest.TestCase):
    def test_frictionless_torque_matches_radial_cam_work(self):
        load = follower_load.contact_load(20, 12, 5, 0)
        self.assertAlmostEqual(load["torque_Nm"], 5*12/1000)
        self.assertAlmostEqual(load["normal_force_N"], 5*math.sqrt(1+.6**2))

    def test_circular_dwell_friction_and_singularity(self):
        load = follower_load.contact_load(26, 0, 5, .4)
        self.assertAlmostEqual(load["torque_Nm"], .4*5*26/1000)
        self.assertAlmostEqual(load["peg_resultant_N"], 5*math.sqrt(1+.4**2))
        singular = follower_load.contact_load(10, 20, 5, .5)
        self.assertTrue(singular["singularity"])
        self.assertIsNone(singular["torque_Nm"])

    def test_actual_cam_sensitivity_and_nominal_root_stress(self):
        report = follower_load.calculate()
        self.assertEqual(report["sample_count"], 1601)
        self.assertAlmostEqual(report["max_pressure_angle_degrees"], 41.22, delta=.02)
        self.assertFalse(report["physical_verified"])
        self.assertIsNone(report["material_allowable_MPa"])
        self.assertAlmostEqual(report["guide_friction_allowance_N"], .5*report["gravity_N"])
        scenarios = report["scenarios"]
        self.assertEqual([scenario["friction_coefficient"] for scenario in scenarios], [0, .2, .4])
        for previous, following in zip(scenarios, scenarios[1:]):
            self.assertLess(previous["max_torque_Nm"], following["max_torque_Nm"])
            self.assertLess(previous["max_peg_resultant_N"], following["max_peg_resultant_N"])
        selected = report["selected_scenario"]
        self.assertFalse(selected["singularity"])
        force = selected["max_peg_resultant_N"]
        self.assertAlmostEqual(selected["nominal_root_bending_MPa"], 32*force*4.5/(math.pi*6**3))
        self.assertAlmostEqual(selected["nominal_root_max_shear_MPa"], 4*force/(3*math.pi*6**2/4))

    def test_two_coupon_solids_fit_bed_with_final_contact_orientation(self):
        coupons = {"fit_coupon": (layout.fit_coupon(), None),
                   "follower_groove_coupon": (layout.follower_groove_coupon(), ((0,1,0),-90))}
        for name, (shape, rotation) in coupons.items():
            with self.subTest(coupon=name):
                self.assertTrue(shape.isValid())
                self.assertEqual(len(shape.Solids()), 1)
                extent = bounds(print_position(shape, rotation))
                self.assertAlmostEqual(extent[2], 0)
                self.assertLessEqual(extent[3]+2*PARAMS["print"]["brim"], PARAMS["print"]["bed_x"])
                self.assertLessEqual(extent[4]+2*PARAMS["print"]["brim"], PARAMS["print"]["bed_y"])
        peg = layout.follower_coupon_peg()
        top = max((face for face in peg.Faces() if face.geomType() == "PLANE"),
                  key=lambda face: face.Center().z)
        self.assertAlmostEqual(top.normalAt().z, 1)
        self.assertAlmostEqual(bounds(peg)[5]-bounds(peg)[2], 4.5)
        self.assertAlmostEqual(layout.fit_coupon().intersect(peg).Volume(), peg.Volume(), places=5)
        # The real x33 floor faces upward after the same rotation as the cam.
        groove = layout.follower_groove_coupon()
        floor = next(face for face in groove.Faces()
                     if face.geomType() == "PLANE" and abs(face.Center().x-33) < 1e-6)
        rotated_floor = floor.rotate((0,0,0), (0,1,0), -90)
        self.assertAlmostEqual(rotated_floor.normalAt().z, 1)
        self.assertAlmostEqual(floor.Area(), 6.8*20)
        self.assertAlmostEqual(bounds(groove)[3]-floor.Center().x, 6)
        # Invert the groove coupon for the hand fit. Its front rests on the
        # one-mm coupon seat, preserving the real 2.5-mm peg-tip/floor gap.
        groove_up = print_position(groove, ((0,1,0),-90))
        groove_down = print_position(groove_up, ((1,0,0),180)).translate((68,41,5))
        self.assertLess(layout.fit_coupon().intersect(groove_down).Volume(), .001)
        self.assertGreater(layout.fit_coupon().intersect(groove_down.translate((0,0,-.01))).Volume(), .1)
        self.assertAlmostEqual(11-bounds(peg)[5], 2.5)


if __name__ == "__main__":
    unittest.main()
