"""Manufacturing joints and continuous mechanical interfaces; no physical claim."""
import unittest

import chassis
import mechanism
from geometry import box, bounds, cylinder, print_position
from printability import analyze


PROFILE = {"max_bridge_mm": 12, "min_wall_mm": .4}


class SupportFreeMechanismTests(unittest.TestCase):
    def test_each_mechanism_print_has_no_analytical_support_requirement(self):
        for part in mechanism.parts():
            with self.subTest(part=part.name):
                self.assertTrue(part.shape.isValid())
                self.assertEqual(len(part.shape.Solids()), 1)
                report = analyze(print_position(part.shape, part.rotation), PROFILE)
                self.assertEqual(report["status"], "PASS", report["issues"])

    def test_bed_halves_insert_and_separate_directly(self):
        left, right = mechanism.tray_halves()
        for distance in (0, .25, 1, 2, 4, 10, 20):
            with self.subTest(distance=distance):
                self.assertLess(left.translate((-distance,0,0)).intersect(right).Volume(), 1e-6)
        # Each socket has material both above and below its locating tongue.
        for y in mechanism.TRAY_JOINTS:
            for z in (-19.8,-16.8):
                sample=box(.5,1,.2,(17,y-.5,z))
                self.assertAlmostEqual(right.intersect(sample).Volume(), sample.Volume(), places=6)

    def test_joint_fasteners_never_enter_neighbours_at_any_lift(self):
        for index in range(6):
            module=mechanism.module(index)
            joints=[i for i in module if i.name.startswith("tray_joint_")]
            for neighbour in (index-1,index+1):
                if not 0<=neighbour<6:
                    continue
                moving=[i for i in mechanism.module(neighbour)
                        if i.group in ("moving","moving_hw","wand")]
                for a in joints:
                    ab=bounds(a.shape)
                    for b in moving:
                        bb=bounds(b.shape)
                        # Disjoint XY projections prove clearance for all
                        # independent Z positions, including intermediate lift.
                        self.assertTrue(any(min(ab[k+3],bb[k+3])-max(ab[k],bb[k])<=1e-5
                                            for k in (0,1)), (a.name,b.name))

    def test_full_bed_clears_cam_at_every_degree(self):
        m=mechanism.M
        for angle in range(161):
            with self.subTest(angle=angle):
                z=m["cam_z"]+mechanism.cam_law(angle)[0]+m["follower_drop"]
                cam=mechanism.cam().rotate((0,0,0),(1,0,0),angle).translate(
                    (m["cam_x"],m["cam_y"],m["cam_z"]))
                self.assertLess(mechanism.tray().translate((0,0,z)).intersect(cam).Volume(), .001)

    def test_frame_seam_fasteners_clamp_both_panels(self):
        panels=chassis.tiles()
        for x,y in chassis.seam_floor_holes():
            # Nut bearing and conical-head bearing each cross the butt seam.
            for z,outer,inner in ((8.05,3.0,1.8),(11.45,3.1,2.9)):
                ring=cylinder(outer,.05,(x,y,z)).cut(cylinder(inner,.1,(x,y,z-.01)))
                touched=[p.intersect(ring).Volume() for p in panels]
                self.assertEqual(sum(v>1e-4 for v in touched),2,(x,y,z,touched))


if __name__ == "__main__":
    unittest.main()
