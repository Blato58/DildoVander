"""CAD regression acceptance without writing exports or claiming physical fit."""
import unittest
import tempfile
from pathlib import Path

import cadquery as cq

import chassis
import layout
import mechanism
from geometry import PARAMS, bounds, print_position, box
from build import all_parts
from check_fit import collisions, overlaps


class InsertCadTests(unittest.TestCase):
    def test_left_cassette_wires_clear_installed_scene_after_service_moves(self):
        import harness
        from verify_harness_geometry import audit
        wires=[wire for wire in harness.HARNESSES
               if wire['id'] in ('USB-PD','P12+','F_AUDIO+-01','C1+','C1-','C2+','C2-')]
        result=audit(wires=wires)
        self.assertEqual(result['status'],'PASS',result['collisions'])

    def test_measured_clear_rectangle_rim_and_lower_arcs(self):
        shell=chassis.shell()
        self.assertTrue(shell.isValid())
        self.assertEqual(len(shell.Solids()),1)
        actual=bounds(shell)
        for value,expected in zip(actual,(-5,-20,-5,435,289,114.3)):
            self.assertAlmostEqual(value,expected,places=6)
        # The front/back walls continue below the rim ledges. The 430x269
        # rectangle is clear only BETWEEN these ledges at the opening.
        for y,z,expected in ((-5,80,1),(-5,70,0),(274,90,1),(274,80,0),
                             (0,5,1),(30,.25,0),(238,.25,0),(269,10,1)):
            probe=box(1,1,.1,(100,y,z))
            self.assertAlmostEqual(shell.intersect(probe).Volume(),expected*.1,places=6)
        c=PARAMS['case']
        self.assertEqual((c['inside_x'],c['inside_y'],c['wall']),(430,269,5))
        self.assertEqual((c['brace_width'],c['brace_y'],c['brace_height']),(80,94.5,5))

    def test_curved_floor_anchors_and_bonds_follow_wood(self):
        shell=chassis.shell()
        for name in chassis.floor_anchor_specs():
            with self.subTest(anchor=name):
                anchor=chassis.floor_anchor_shape(name)
                film=chassis.floor_bond_film(name)
                self.assertLess(anchor.intersect(shell).Volume(),1e-6)
                self.assertLess(film.intersect(shell).Volume(),1e-6)
                self.assertLess(anchor.intersect(film).Volume(),1e-6)
                self.assertAlmostEqual(film.Volume(),chassis.floor_bond_projected_film(name).Volume(),places=5)
        # At the old front/rear edge the underside is curved, not a floating
        # flat pad with several millimetres of unmodelled adhesive.
        self.assertGreater(chassis.floor_bond_film('floor_anchor_front_left').BoundingBox().zmax,3)
        self.assertGreater(chassis.floor_bond_film('floor_anchor_rear_left').BoundingBox().zmax,2)

    def test_curved_anchor_print_orientation_and_strict_stl_topology(self):
        from verify import stl_check
        with tempfile.TemporaryDirectory() as directory:
            for part in chassis.parts():
                if not part.name.startswith('floor_anchor_'):
                    continue
                with self.subTest(anchor=part.name):
                    self.assertEqual(part.rotation,((1,0,0),180))
                    printed=print_position(part.shape,part.rotation)
                    # The flat top goes on the bed; the curved underside is
                    # uppermost, avoiding support on the glue contact curve.
                    bottom_area=sum(face.Area() for face in printed.Faces()
                                    if face.geomType()=='PLANE' and abs(face.Center().z)<1e-6
                                    and face.normalAt().z<-.9)
                    self.assertGreater(bottom_area,5000)
                    path=Path(directory)/(part.name+'.stl')
                    cq.exporters.export(printed,str(path),tolerance=.03,angularTolerance=.08)
                    report=stl_check(path,printed)
                    self.assertTrue(report['closed'] and report['connected'] and report['consistent_winding'])

    def test_printed_insert_has_no_wooden_shell_exports(self):
        parts=all_parts()
        self.assertFalse(any(p.name.startswith(("body_","lid_")) for p in parts))
        self.assertEqual(sum(p.name.startswith("frame_panel_") for p in chassis.parts()),4)
        self.assertEqual(sum(p.name.startswith("floor_anchor_") for p in chassis.parts()),4)
        for part in parts:
            with self.subTest(part=part.name):
                self.assertTrue(part.shape.isValid())
                self.assertEqual(len(part.shape.Solids()),1)
                printed=print_position(part.shape,part.rotation)
                extent=bounds(printed)
                self.assertLessEqual(extent[3]+2*PARAMS["print"]["brim"],PARAMS["print"]["bed_x"]+.001)
                self.assertLessEqual(extent[4]+2*PARAMS["print"]["brim"],PARAMS["print"]["bed_y"]+.001)

    def test_hard_stops_are_real_base_material(self):
        m=PARAMS["mechanism"]
        low=mechanism.tray().translate((0,0,m["cam_z"]+m["cam_low_radius"]+m["follower_drop"]))
        self.assertLess(mechanism.base().intersect(low).Volume(),.03)
        contact=mechanism.base().intersect(low.translate((0,0,-.01))).Volume()/.01
        self.assertGreater(contact,80)
        self.assertEqual(box(m["base_width"],m["length"],4).intersect(low.translate((0,0,-.01))).Volume(),0)

    def test_standard_m4_heads_clear_installed_carriers(self):
        heads=[i for i in chassis.items() if i.name.startswith("anchor_head_")]
        for name,shape in layout.carrier_shapes().items():
            for head in heads:
                with self.subTest(carrier=name,head=head.name):
                    self.assertLess(shape.intersect(head.shape).Volume(),1e-6)

    def test_captured_guides_clear_fully_raised_tray(self):
        for index in range(PARAMS["mechanism"]["count"]):
            with self.subTest(module=index+1):
                self.assertEqual(collisions(mechanism.module(index,160)),[])

    def test_captured_cam_tray_and_servo_can_be_lifted_together_for_service(self):
        # Disconnect wires, remove guide nuts/washers and cut the two servo
        # straps first. The integral peg stays inside the cam while this
        # subassembly is lifted; forcing the tray alone is not an assembly path.
        module=mechanism.module(0,with_wand=False)
        moving=[i for i in module if i.group in ("moving","moving_hw") or i.name=="servo_1"
                or i.name.startswith("coupler_")]
        fixed=[i for i in module if i.name.startswith(("base_","guide_bolt_","guide_head_","module_"))]
        fixed += mechanism.module(1,with_wand=False)+chassis.items()+layout.items()
        fixed=[i for i in fixed if not i.group.startswith("lid")]
        boxes={i.name:bounds(i.shape) for i in fixed}
        for z in range(0,106,5):
            for item in moving:
                lifted=item.shape.translate((0,0,z)); bb=bounds(lifted)
                for obstacle in fixed:
                    if overlaps(bb,boxes[obstacle.name]):
                        with self.subTest(offset=z,moving=item.name,fixed=obstacle.name):
                            self.assertLess(lifted.intersect(obstacle.shape).Volume(),.03)

    def test_connector_reservations_match_authoritative_contract(self):
        solids={i.name:i.shape for i in layout.reservations()}
        for name,spec in PARAMS["connector_reservations"].items():
            key="esp_usb" if name=="esp32_usb" else name
            expected=list(spec["origin"])+[a+b for a,b in zip(spec["origin"],spec["size"])]
            for actual,target in zip(bounds(solids[key]),expected):
                self.assertAlmostEqual(actual,target,places=6)

    def test_capacitor_lead_bores_export_closed_nondegenerate_meshes(self):
        from verify import stl_check
        holders=layout.carrier_shapes()
        with tempfile.TemporaryDirectory(prefix="f2-cap-stl-") as directory:
            for index in range(3,7):
                name=f"capacitor_holder_{index}"
                with self.subTest(part=name):
                    shape=print_position(holders[name])
                    self.assertTrue(shape.isValid())
                    self.assertEqual(len(shape.Solids()),1)
                    path=Path(directory)/(name+".stl")
                    cq.exporters.export(shape,str(path),tolerance=.03,angularTolerance=.08)
                    result=stl_check(path,shape)
                    self.assertTrue(result["closed"] and result["connected"])

    def test_service_cassettes_export_closed_connected_meshes(self):
        from verify import stl_check
        holders=layout.carrier_shapes()
        with tempfile.TemporaryDirectory(prefix="f2-cassette-stl-") as directory:
            for name in ("left_service_carrier","right_service_carrier"):
                with self.subTest(part=name):
                    shape=print_position(holders[name])
                    self.assertTrue(shape.isValid())
                    self.assertEqual(len(shape.Solids()),1)
                    path=Path(directory)/(name+".stl")
                    cq.exporters.export(shape,str(path),tolerance=.03,angularTolerance=.08)
                    result=stl_check(path,shape)
                    self.assertTrue(result["closed"] and result["connected"] and result["consistent_winding"])


if __name__=="__main__":
    unittest.main()
