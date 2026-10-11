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
                    head_radius=PARAMS["frame"]["floor_anchors"]["key_head_diameter"]/2
                    expanded=cylinder(head_radius+2,7.5,(key["x"],key["y"],.5))
                    self.assertAlmostEqual(expanded.intersect(blank).Volume(),expanded.Volume(),places=5)
                    for tool in chassis.floor_anchor_clearances(name):
                        self.assertLess(expanded.intersect(tool).Volume(),1e-6)
                    for other in keys[index+1:]:
                        self.assertGreaterEqual(math.hypot(key["x"]-other["x"],key["y"]-other["y"]),2*head_radius+2-1e-8)
                    for dx in (-2,2):
                        mouth=cylinder(1.25,.01,(key["x"]+dx,key["y"],7.99))
                        self.assertAlmostEqual(key["shape"].intersect(mouth).Volume(),mouth.Volume(),places=5)
                    neck_radius=PARAMS["frame"]["floor_anchors"]["key_neck_diameter"]/2
                    self.assertGreaterEqual(head_radius-neck_radius,1.5)
                    neck=cylinder(neck_radius,.01,(key["x"],key["y"],.5))
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


class AnchorCouponTests(unittest.TestCase):
    def test_open_head_recesses_copy_production_tools_and_accept_actual_heads(self):
        import layout
        coupon=layout.fit_coupon()
        m3=chassis.carrier_head_clearance(10,12)
        m4=chassis.frame_head_clearance(25,12)
        # Align actual top faces: the 1.75 mm M3 cutter has 0.1 mm overrun,
        # leaving the same physical 1.65 mm recess as the production cradle.
        samples=((10,3.4,6.2,1.65,m3.translate((0,0,4-8)),
                  cylinder(2.85,1.65,(10,12,2.35))),
                 (25,4.5,7.5,.5,m4.translate((0,0,4-12)),
                  cylinder(3.5,4,(25,12,3.5))))
        for x,bore,recess,depth,tool,head in samples:
            with self.subTest(head_bore=bore):
                void=cylinder(recess/2,depth,(x,12,4-depth))
                self.assertLess(coupon.intersect(void).Volume(),1e-6)
                self.assertAlmostEqual(void.intersect(tool).Volume(),void.Volume(),places=5)
                self.assertLess(coupon.intersect(head).Volume(),1e-6)
                bottom=cylinder(recess/2,.01,(x,12,4-depth-.01)).cut(
                    cylinder(bore/2,.01,(x,12,4-depth-.01)))
                self.assertAlmostEqual(coupon.intersect(bottom).Volume(),bottom.Volume(),places=5)
                self.assertLess(coupon.intersect(hole_z(x,12,bore,z=0,height=4-depth)).Volume(),1e-6)

    def test_coupon_epoxy_key_matches_production_and_preserves_lip_roof_and_ports(self):
        import layout
        coupon=layout.fit_coupon()
        key=chassis.floor_anchor_key_shape(100,40).translate((0,0,-.5))
        window=box(13,13,7.5,(93.5,33.5,0))
        expected=window.cut(key)
        actual=coupon.intersect(window)
        self.assertLess(actual.cut(expected).Volume()+expected.cut(actual).Volume(),1e-6)
        production_counts={"floor_anchor_front_left":19,"floor_anchor_front_right":21,
                           "floor_anchor_rear_left":22,"floor_anchor_rear_right":22}
        for name,count in production_counts.items():
            keys=chassis.floor_anchor_keys(name)
            self.assertEqual(len(keys),count)
            source=keys[0]
            translated=source["shape"].translate((100-source["x"],40-source["y"],-.5))
            self.assertLess(key.cut(translated).Volume()+translated.cut(key).Volume(),1e-6)
        a=PARAMS["frame"]["floor_anchors"]
        neck_radius=a["key_neck_diameter"]/2
        head_radius=a["key_head_diameter"]/2
        self.assertAlmostEqual(a["key_head_bottom"]-a["z"],2)
        self.assertAlmostEqual(a["z"]+a["height"]-a["key_head_top"],3.5)
        self.assertGreaterEqual(head_radius-neck_radius,1.5)
        neck=cylinder(neck_radius,2,(100,40,0))
        chamber=cylinder(head_radius,2,(100,40,2))
        self.assertLess(coupon.intersect(neck).Volume(),1e-6)
        self.assertLess(coupon.intersect(chamber).Volume(),1e-6)
        lip=chamber.translate((0,0,-2)).cut(neck)
        self.assertAlmostEqual(coupon.intersect(lip).Volume(),lip.Volume(),places=5)
        vents=[cylinder(1.25,3.5,(x,40,4)) for x in (98,102)]
        for vent in vents:
            self.assertLess(coupon.intersect(vent).Volume(),1e-6)
        roof=cylinder(head_radius,3.5,(100,40,4)).cut(*vents)
        self.assertAlmostEqual(coupon.intersect(roof).Volume(),roof.Volume(),places=5)
        # An upward pull loads the bottom lip onto the mushroom shoulder.
        self.assertGreater(coupon.translate((0,0,.1)).intersect(key).Volume(),3)

    def test_coupon_matches_actual_profiles_and_preserves_production_anchors(self):
        import layout
        coupon=layout.fit_coupon()
        anchor="floor_anchor_front_left"
        edge=chassis.floor_anchor_specs()[anchor]["y_range"][0]
        production_tools=chassis.floor_anchor_clearances(anchor)
        for kind,x0,target_x in (("module",86,93),("frame",100,107)):
            nut=next(n for n in chassis.anchor_nuts(anchor) if n["kind"]==kind)
            shift=(target_x-nut["x"],-edge,-.5)
            tools=chassis.anchor_nut_clearances(nut,edge)
            window=box(14,25,7.5,(x0,0,0))
            expected=window.cut(*(tool.translate(shift) for tool in tools))
            actual=coupon.intersect(window)
            with self.subTest(profile=kind):
                self.assertLess(actual.cut(expected).Volume()+expected.cut(actual).Volume(),1e-6)
                index=next(i for i,n in enumerate(chassis.anchor_nuts(anchor)) if n["kind"]==kind)
                for current,original in zip(tools,production_tools[index*3:index*3+3]):
                    self.assertLess(current.cut(original).Volume()+original.cut(current).Volume(),1e-6)
        # Approved enlarged-key source volumes. These also catch production
        # changes beyond matching just the two coupon nut cavities.
        original_volumes={
            "floor_anchor_front_left":55863.91132015793,
            "floor_anchor_front_right":55553.81498064702,
            "floor_anchor_rear_left":62597.807063061126,
            "floor_anchor_rear_right":62697.43639088506}
        for name,volume in original_volumes.items():
            self.assertAlmostEqual(chassis.floor_anchor_shape(name).Volume(),volume,places=5)

    def test_coupon_side_insertion_complete_roofs_and_blind_tip_barriers(self):
        import layout
        coupon=layout.fit_coupon()
        a=PARAMS["frame"]["floor_anchors"]
        nuts=chassis.anchor_nuts("floor_anchor_front_left")
        for kind,target_x in (("module",93),("frame",107)):
            source=next(n for n in nuts if n["kind"]==kind)
            x,y,z=target_x,16,source["z"]-a["z"]
            roof_z=a["nut_roof_z"]-a["z"]
            height=a["nut_roof_z"]-source["z"]
            nut=chassis.hex_prism(x,y,z,source["diameter"],height).cut(
                hole_z(x,y,3 if kind=="module" else 4,z=z-.1,height=height+.2))
            with self.subTest(profile=kind):
                for ratio in (0,.25,.5,.75,1):
                    self.assertLess(coupon.intersect(nut.translate((0,-y*ratio,0))).Volume(),1e-6)
                roof=chassis.hex_prism(x,y,roof_z,source["pocket"],3).cut(
                    hole_z(x,y,source["bore"],z=roof_z-.1,height=3.2))
                self.assertAlmostEqual(coupon.intersect(roof).Volume(),roof.Volume(),places=5)
                self.assertGreater(coupon.intersect(nut.translate((0,0,.1))).Volume(),1)
                # Production blind bore starts at global Z1, hence the coupon
                # retains a true 0.5 mm closed printed bottom, not a through bore.
                barrier=cylinder(source["bore"]/2,.5,(x,y,0))
                self.assertAlmostEqual(coupon.intersect(barrier).Volume(),barrier.Volume(),places=5)
                free_tip=hole_z(x,y,source["bore"],z=.5,height=.01)
                self.assertLess(coupon.intersect(free_tip).Volume(),1e-6)

    def test_coupon_remains_one_existing_print_and_retains_original_features(self):
        import layout
        from build import all_parts
        coupon=layout.fit_coupon()
        self.assertTrue(coupon.isValid())
        self.assertEqual(len(coupon.Solids()),1)
        self.assertEqual(bounds(print_position(coupon))[:3],[0,0,0])
        self.assertAlmostEqual(bounds(coupon)[3],115)
        for x,diameter in ((10,3.4),(25,4.5),(41,12.4),(61,19.4)):
            with self.subTest(original_bore=diameter):
                self.assertLess(coupon.intersect(hole_z(x,12,diameter,z=0,height=4)).Volume(),1e-6)
        guide=box(8,10,22,(5,34,4))
        self.assertAlmostEqual(coupon.intersect(guide).Volume(),guide.Volume(),places=5)
        peg=layout.follower_coupon_peg()
        self.assertAlmostEqual(coupon.intersect(peg).Volume(),peg.Volume(),places=5)
        parts=all_parts()
        self.assertEqual(len(parts),37)
        self.assertEqual(sum(p.quantity for p in parts),52)
        self.assertEqual(sum(p.name=="fit_coupon" for p in parts),1)


if __name__=="__main__":
    unittest.main()
