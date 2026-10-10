"""Source-CAD checks for printed floor anchoring, with no physical-fit claim."""
import math
import unittest

import chassis
import mechanism
import strength
from geometry import PARAMS, box, bounds, cylinder, hole_z, print_position


class FloorAnchorTests(unittest.TestCase):
    def test_all_eight_printed_parts_connected_and_fit_bed(self):
        parts=chassis.parts()
        self.assertEqual(len(parts),8)
        self.assertEqual(sum(p.name.startswith("floor_anchor_") for p in parts),4)
        for part in parts:
            with self.subTest(part=part.name):
                self.assertTrue(part.shape.isValid())
                self.assertEqual(len(part.shape.Solids()),1)
                extent=bounds(print_position(part.shape))
                self.assertLessEqual(extent[3]+6,PARAMS["print"]["bed_x"]+.001)
                self.assertLessEqual(extent[4]+6,PARAMS["print"]["bed_y"]+.001)
        # Uprights must be actual printable panel material, not hardware.
        items=chassis.items()
        self.assertFalse(any(i.name.startswith(("bonded_angle_","front_crossrail","rear_crossrail")) for i in items))
        panels=chassis.tiles()
        for rail in PARAMS["frame"]["stiffeners"]:
            x0,x1=rail["upright_x"]
            for x in (x0+4,x1-4):
                sample=box(1,1,1,(x,rail["upright_y"]+.5,20))
                self.assertAlmostEqual(sum(p.intersect(sample).Volume() for p in panels),1,places=6)

    def test_safe_zone_brace_and_dry_interfaces(self):
        c=PARAMS["case"]
        low=c["safe_origin"]; high=[a+b for a,b in zip(low,c["safe_zone"])]
        brace=box(c["inside_x"],c["brace_width"],c["brace_height"],(0,c["brace_y"],0))
        for name in chassis.floor_anchor_specs():
            with self.subTest(anchor=name):
                anchor=chassis.floor_anchor_shape(name)
                b=bounds(anchor)
                self.assertTrue(all(b[k]>=low[k]-1e-6 and b[k+3]<=high[k]+1e-6 for k in range(3)))
                self.assertLess(anchor.intersect(brace).Volume(),1e-6)
                bond=chassis.floor_bond_shape(name)
                self.assertEqual(len(bond.Solids()),1)
                self.assertLess(anchor.intersect(bond).Volume(),1e-6)
                for panel in chassis.tiles():
                    self.assertLess(anchor.intersect(panel).Volume(),1e-6)

    def test_closed_nut_roofs_side_loading_and_full_engagement(self):
        count={"module":0,"frame":0}
        for name,spec in chassis.floor_anchor_specs().items():
            anchor=chassis.floor_anchor_shape(name)
            for nut in chassis.anchor_nuts(name):
                with self.subTest(anchor=name,kind=nut["kind"],x=nut["x"]):
                    count[nut["kind"]]+=1
                    x,y,z=nut["x"],nut["y"],nut["z"]
                    height=5-z
                    solid=chassis.hex_prism(x,y,z,nut["diameter"],height).cut(hole_z(x,y,3 if nut["kind"]=="module" else 4,z=z-.1,height=height+.2))
                    self.assertLess(anchor.intersect(solid).Volume(),1e-6)
                    # A complete 3 mm roof annulus above the hex footprint:
                    # this would fail for a top-open pocket or a relief cut.
                    roof=chassis.hex_prism(x,y,5,nut["pocket"],3).cut(hole_z(x,y,nut["bore"],z=4.9,height=3.2))
                    self.assertAlmostEqual(anchor.intersect(roof).Volume(),roof.Volume(),places=5)
                    self.assertGreater(anchor.intersect(solid.translate((0,0,.1))).Volume(),1)
                    edge=spec["y_range"][0] if spec["end"]=="front" else spec["y_range"][1]
                    for ratio in (.25,.5,1):
                        self.assertLess(anchor.intersect(solid.translate((0,(edge-y)*ratio,0))).Volume(),1e-6)
                    length=14 if nut["kind"]=="module" else 10
                    head=16 if nut["kind"]=="module" else 11.5
                    tip=head-length
                    self.assertLessEqual(tip,z-.2)
                    self.assertGreater(head,5)
                    barrier=cylinder(nut["bore"]/2,.49,(x,y,.51))
                    self.assertAlmostEqual(anchor.intersect(barrier).Volume(),barrier.Volume(),places=5)
        self.assertEqual(count,{"module":24,"frame":4})

    def test_epoxy_keys_keep_two_mm_web_bottom_access_and_top_vents(self):
        total=0
        for name in chassis.floor_anchor_specs():
            blank=chassis.floor_anchor_blank(name)
            keys=chassis.floor_anchor_keys(name)
            self.assertGreaterEqual(len(keys),4)
            total+=len(keys)
            for index,key in enumerate(keys):
                with self.subTest(anchor=name,key=index):
                    expanded=cylinder(6.5,7.5,(key["x"],key["y"],.5))
                    self.assertAlmostEqual(expanded.intersect(blank).Volume(),expanded.Volume(),places=5)
                    for tool in chassis.floor_anchor_clearances(name):
                        self.assertLess(expanded.intersect(tool).Volume(),1e-6)
                    for other in keys[index+1:]:
                        self.assertGreaterEqual(math.hypot(key["x"]-other["x"],key["y"]-other["y"]),11)
                    for dx in (-2,2):
                        mouth=cylinder(1.25,.01,(key["x"]+dx,key["y"],7.99))
                        self.assertAlmostEqual(key["shape"].intersect(mouth).Volume(),mouth.Volume(),places=5)
                    neck=cylinder(3,.01,(key["x"],key["y"],.5))
                    self.assertAlmostEqual(key["shape"].intersect(neck).Volume(),neck.Volume(),places=5)
                    # Inverted carry physically loads the bottom lip onto the
                    # cast mushroom head; a straight unkeyed bore would fail.
                    uplift=chassis.floor_anchor_shape(name).translate((0,0,.1))
                    self.assertGreater(uplift.intersect(key["shape"]).Volume(),3)
        self.assertGreaterEqual(total,16)

    def test_real_fasteners_and_underside_heads_clear_cradles(self):
        import layout
        anchors=[chassis.floor_anchor_shape(n) for n in chassis.floor_anchor_specs()]
        installed=chassis.items()+layout.items()
        for index in range(PARAMS["mechanism"]["count"]):
            installed+=mechanism.module(index)
        for item in installed:
            if item.name.startswith(("carrier_head_","guide_head_","frame_seam_nut_","anchor_","module_nut_","module_m3_")):
                with self.subTest(item=item.name):
                    self.assertLess(sum(a.intersect(item.shape).Volume() for a in anchors),.00001)

    def test_strength_reports_assumptions_and_actual_net_areas(self):
        report=strength.calculate()
        self.assertEqual(report["qualification"],"NOT PHYSICALLY ACCEPTED")
        self.assertAlmostEqual(report["adverse_two_support_reaction_N"],73.575)
        self.assertNotIn("nominal_yield_ratio",report)
        for name,data in report["anchors"].items():
            with self.subTest(anchor=name):
                self.assertLess(data["petg_film_contact_mm2"],data["wood_bond_area_mm2"])
                expected=chassis.floor_bond_shape(name).Volume()
                self.assertAlmostEqual(data["epoxy_volume_mm3"],expected,places=5)
                self.assertGreater(data["bond_section_modulus_min_mm3"],0)
                self.assertTrue(all(area>0 for area in data["nut_row_roof_net_sections_mm2"].values()))
                self.assertGreater(data["assumed_sideways_key_neck_ratio"],1)
                self.assertTrue(all(r["bearing_area_mm2"]>0 for r in data["nut_roofs"]))


if __name__=="__main__":
    unittest.main()
