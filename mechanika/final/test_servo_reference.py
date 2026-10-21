"""Reference placement, retention and drive clearances; no physical fit claim."""
import hashlib
import unittest

from OCP.BRepAdaptor import BRepAdaptor_Surface

import harness
import mechanism
from geometry import ROOT, bounds, cylinder


class ServoReferenceTests(unittest.TestCase):
    def test_reference_bytes_and_six_installed_shaft_axes(self):
        reference = ROOT / "reference" / "SG90-Servo.stp"
        self.assertEqual(hashlib.sha256(reference.read_bytes()).hexdigest(),
                         "e2bb320f91ece91deaff236bc0b12565e93ab54a3e619d6c5f2fba884c62aeea")
        m = mechanism.M
        self.assertEqual(m["cam_z"], 37)
        for index in range(m["count"]):
            placed = next(i.shape for i in mechanism.module(index) if i.name == f"servo_{index+1}")
            self.assertTrue(placed.isValid())
            self.assertEqual(len(placed.Solids()), 1)
            splines = []
            for face in placed.Faces():
                if face.geomType() == "CYLINDER":
                    surface = BRepAdaptor_Surface(face.wrapped).Cylinder()
                    if abs(surface.Radius()-2.475) < 1e-6:
                        splines.append(surface)
            self.assertTrue(splines)
            for spline in splines:
                self.assertAlmostEqual(abs(spline.Axis().Direction().X()), 1)
                self.assertAlmostEqual(spline.Location().Y(), m["origin_y"]+m["cam_y"])
                self.assertAlmostEqual(spline.Location().Z(), m["origin_z"]+37)

    def test_real_case_ears_and_cable_stubs_clear_cradle_and_ties(self):
        servo, base = mechanism.servo(), mechanism.base()
        self.assertTrue(base.isValid())
        self.assertEqual(len(base.Solids()), 1)
        self.assertLess(servo.intersect(base).Volume(), 1e-6)
        self.assertAlmostEqual(bounds(servo)[2]-mechanism.SERVO_SUPPORT_Z, .25)
        for tie in mechanism.retaining_ties("servo"):
            self.assertLess(tie.intersect(servo).Volume(), 1e-6)
            self.assertLess(tie.intersect(base).Volume(), 1e-6)

    def test_shaft_socket_and_rotating_cam_clear_real_servo(self):
        servo = mechanism.servo()
        m = mechanism.M
        for angle in range(0, 161, 5):
            with self.subTest(angle=angle):
                for shape in (mechanism.cam(), mechanism.stock_horn()):
                    placed = shape.rotate((0,0,0),(1,0,0),angle).translate((m["cam_x"],m["cam_y"],m["cam_z"]))
                    self.assertLess(servo.intersect(placed).Volume(), .001)

    def test_raised_tray_retains_running_clearance_and_thread_engagement(self):
        module = mechanism.module(0, 160)
        m = mechanism.M
        for x, y in mechanism.GUIDES:
            tray = next(i for i in module if i.name == ("tray_left_1" if x < 16 else "tray_1"))
            washer = next(i for i in module if i.name == f"guide_washer_1_{int(y)}")
            nut = next(i for i in module if i.name == f"guide_nut_1_{int(y)}")
            bolt = next(i for i in module if i.name == f"guide_bolt_1_{int(y)}")
            # Check the deck below each washer, excluding the raised side rails.
            deck = tray.shape.intersect(cylinder(4.5,150,(m["origin_x"]+x,m["origin_y"]+y,0)))
            self.assertAlmostEqual(bounds(washer.shape)[2]-bounds(deck)[5], 1)
            self.assertGreater(bounds(bolt.shape)[5]-bounds(nut.shape)[5], 4)

    def test_wiring_escapes_front_slot_before_turning_around_case(self):
        for index in range(1, 7):
            for pin in ("+", "-", "S"):
                endpoint = harness.endpoint_anchor(f"servo_{index}:{pin}")
                wire = next(w for w in harness.HARNESSES if w["to"] == f"servo_{index}:{pin}")
                # Stored routes run from controller to servo; read backwards.
                route = list(reversed(wire["points"]))
                self.assertEqual(tuple(route[0]), endpoint)
                self.assertLess(route[1][1], endpoint[1])
                self.assertEqual(route[1][0], endpoint[0])


if __name__ == "__main__":
    unittest.main()
