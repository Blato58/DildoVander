"""Nominal fit and print orientation, without asserting physical spline grip."""
import tempfile
import unittest
from pathlib import Path

from shapely.geometry import LineString, Point
from shapely.ops import unary_union

from geometry import bounds, cylinder, print_position
import manufacturing
import mechanism
import servo_horn as horn


class ServoHornTests(unittest.TestCase):
    def test_socket_preserves_step_grooves_and_clearances(self):
        section = horn.shaft_section()
        self.assertEqual(len(section.Edges()), 40)
        self.assertEqual(sum(edge.geomType() == "CIRCLE" for edge in section.Edges()), 20)
        # Use the actual connected edge order, including the closing segment.
        segments = [LineString([(p.y,p.z) for p in edge.sample(2)[0]])
                    for edge in horn.printable_section().Edges()]
        outline = unary_union(segments)
        for edge in section.Edges():
            self.assertLess(max(Point(p.y,p.z).distance(outline)
                                for p in edge.sample(65)[0]),.003)
        for allowance in horn.FIT_ALLOWANCES:
            shape = horn.horn(allowance)
            self.assertTrue(shape.isValid())
            self.assertEqual(len(shape.Solids()), 1)
            b = bounds(shape)
            self.assertAlmostEqual(b[0], -1.55, places=5)
            self.assertAlmostEqual(b[3], 2.2, places=5)
            self.assertAlmostEqual(b[4] - b[1], 35)
            placed = shape.translate((29, 112, 37))
            # Zero-clearance coupon can overlap at chord error (<0.003 mm).
            self.assertLess(placed.intersect(mechanism.servo()).Volume(),
                            .005 if allowance == 0 else 1e-6)
            # Full center-hole clearance and actual cap bearing material.
            self.assertLess(shape.intersect(cylinder(1.09, 5, (-2,0,0),(1,0,0))).Volume(), 1e-6)
            cap = cylinder(1.7, .8, (1.4,0,0),(1,0,0)).cut(
                cylinder(1.11, 1, (1.3,0,0),(1,0,0)))
            self.assertAlmostEqual(shape.intersect(cap).Volume(), cap.Volume(), places=5)
            for y in (-9,9):
                self.assertLess(shape.intersect(cylinder(1.19,3,(0,y,0),(1,0,0))).Volume(),1e-6)

    def test_coupon_has_production_identical_sockets(self):
        coupon = horn.fit_coupon()
        for index, allowance in enumerate(horn.FIT_ALLOWANCES):
            y = (index - 1) * 12
            # Compare inside hub, excluding only its outer wall and edge labels.
            region = cylinder(4,3.75,(-1.55,y,0),(1,0,0))
            expected = horn.hub(allowance).translate((0,y,0)).intersect(region)
            actual = coupon.intersect(region)
            self.assertLess(actual.cut(expected).Volume()+expected.cut(actual).Volume(),1e-6)

    def test_print_front_face_flat_and_socket_open_upward(self):
        for part in horn.parts():
            shape = print_position(part.shape, part.rotation)
            b = bounds(shape)
            self.assertAlmostEqual(b[2],0)
            self.assertAlmostEqual(b[5],3.75,places=5)
            result = manufacturing.analyze(shape,manufacturing.ANALYTICAL_PROFILE)
            self.assertEqual(result["status"],"PASS",result)
            self.assertEqual(result["floating_start_count"],0)
            self.assertEqual(result["unsupported_count"],0)
            with tempfile.TemporaryDirectory() as directory:
                record = manufacturing.record(part,Path(directory))
                self.assertEqual(record["infill"],"100%")
                self.assertEqual(record["perimeters"],6)
                self.assertEqual(record["nozzle_mm"],.4)

    def test_sweep_clears_cam_fixed_servo_body_and_base(self):
        # The output shaft rotates with the horn. Keep only the stationary body
        # here; nominal spline mating at zero angle is checked separately above.
        fixed_servo = mechanism.servo().cut(cylinder(2.6,3.1,(27.2,112,37),(1,0,0)))
        for angle in range(0,161,5):
            placed = horn.horn().rotate((0,0,0),(1,0,0),angle).translate((29,112,37))
            with self.subTest(angle=angle):
                self.assertLess(placed.intersect(fixed_servo).Volume(),1e-6)
                self.assertLess(placed.intersect(mechanism.base()).Volume(),1e-6)
                cam = mechanism.cam().rotate((0,0,0),(1,0,0),angle).translate((29,112,37))
                self.assertLess(placed.intersect(cam).Volume(),1e-6)

    def test_canonical_module_rotates_only_output_stub(self):
        from servo_reference import servo_at_angle
        from check_fit import collisions
        original = mechanism.servo()
        body_region = cylinder(6, 25, (1,112,37),(1,0,0))
        for angle in (0, 80, 160):
            pose = servo_at_angle(angle)
            self.assertTrue(pose.isValid())
            self.assertEqual(len(pose.Solids()),1)
            self.assertAlmostEqual(pose.Volume(),original.Volume(),places=4)
            self.assertLess(pose.intersect(body_region).cut(original).Volume(),1e-6)
            self.assertEqual(collisions(mechanism.module(0,angle)),[])


if __name__ == "__main__":
    unittest.main()
