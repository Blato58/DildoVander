"""Layout manufacturing regression tests; no final exports are written."""
import unittest
from collections import Counter

import layout
from check_fit import collisions
from geometry import box, bounds, cylinder
from service_geometry import SERVICE_MOUNTS
from geometry import print_position
from printability import analyze


class LayoutManufacturingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.carriers = layout.carrier_shapes()
        cls.parts = {part.name: part for part in layout.parts()}

    def test_revised_cassette_intersections_and_breaker_roof(self):
        children,_=layout.service_components()
        tier1=children['left_service_carrier_tier_1'].shape
        tier2=children['left_service_carrier_tier_2'].shape
        right=children['right_service_carrier_frame_right'].shape
        self.assertLess(tier1.intersect(tier2).Volume(),1e-7)
        self.assertLess(right.intersect(self.carriers['carrier_speaker_right']).Volume(),1e-7)
        self.assertLess(right.intersect(layout.covers()['right_front_cover']).Volume(),1e-7)
        # Every remaining breaker-floor contact sample has at least .4 mm
        # of solid roof below it; original tie/head openings are absent from
        # the sample itself and cannot hide a newly invented thin film.
        contact=tier1.intersect(box(45,28,.05,(21,65,48.95)))
        self.assertGreater(contact.Volume()/.05,800)
        import cadquery as cq
        for face in contact.Faces():
            if face.geomType()=='PLANE' and face.normalAt().z<-.99:
                backing=cq.Solid.extrudeLinear(face.outerWire(),face.innerWires(),
                                              cq.Vector(0,0,-.35))
                self.assertLess(backing.cut(tier1).Volume(),1e-7)

    def test_c1_rear_exit_capsules_and_clamp_hardware_remain_clear(self):
        children,_=layout.service_components()
        tier=children['left_service_carrier_tier_2'].shape
        for x,length,y in ((26,5.2,110.2),(30,7.9,112.9)):
            self.assertLess(cylinder(1.35,length,(x,105,58.5),(0,1,0)).intersect(tier).Volume(),1e-7)
            self.assertLess(cylinder(1.35,46.5-(x-1.7),(x-1.7,y,58.5),(1,0,0)).intersect(tier).Volume(),1e-7)
        for item in layout.service_joint_hardware():
            for name,part in children.items():
                self.assertLess(item.shape.intersect(part.shape).Volume(),1e-7,(item.name,name))

    def test_capacitor_rear_opening_keeps_all_arms_and_positive_disk_support(self):
        tier=layout.service_components()[0]['left_service_carrier_tier_2'].shape
        original=layout._carrier_source_shapes()['left_service_carrier']
        for x,y,z in layout.PARAMS['capacitors'][:2]:
            arms=box(13,3,18.5,(x-6.5,y-1.5,z))
            self.assertLess(original.intersect(arms).cut(tier).Volume(),1e-7)
            disk=cylinder(5,.05,(x,y,z-.05))
            self.assertGreater(tier.intersect(disk).Volume()/.05,20)
            # The new top-open rear passage leaves the original forward
            # bearing region unchanged, including its existing lead openings.
            fore=disk.intersect(box(14,3,.05,(x-7,y-5,z-.05)))
            self.assertLess(original.intersect(fore).cut(tier).Volume(),1e-7)
        self.assertEqual(len(tier.Solids()),1)

    def test_cover_nut_entry_sweeps_are_clear_with_print_tolerance(self):
        import math
        import cadquery as cq
        from shapely.geometry import MultiPoint
        entries=(("bank_holder",51.5,152.5,-1),("bank_holder",51.5,249.5,1),
                 ("carrier_speaker_right",411.75,45,-1),
                 ("carrier_speaker_right",411.75,125,1))
        for name,x,y,direction in entries:
            for z in (102.45,102.6):
                # Convex outer-hex sweep encloses the entire real nut during
                # continuous 12 mm insertion, including its central bore.
                points=[(x+3.2*math.cos(a*math.pi/3),
                         y+3.2*math.sin(a*math.pi/3)+travel*direction)
                        for a in range(6) for travel in (0,12)]
                outline=list(MultiPoint(points).convex_hull.exterior.coords)[:-1]
                sweep=cq.Workplane("XY",origin=(0,0,z)).polyline(outline).close().extrude(2.4).val()
                local=self.carriers[name].intersect(box(20,40,5,(x-10,y-20,101)))
                with self.subTest(part=name,x=x,y=y,z=z):
                    self.assertLess(sweep.intersect(local).Volume(),1e-7)
                    self.assertLess(sweep.cut(box(20,40,5,(x-10,y-20,101))).Volume(),1e-7)

    def test_fixed_capacitor_terminals_remain_open_after_clamp_tab_fusion(self):
        import cadquery as cq
        tier=layout.service_components()[0]['left_service_carrier_tier_2'].shape
        for x,y,z in layout.PARAMS['capacitors'][:2]:
            for lead_x in (x-2,x+2):
                capsule=cq.Solid.makeSphere(1.35,cq.Vector(lead_x,y,z),
                    angleDegrees1=-90,angleDegrees2=90)
                self.assertLess(capsule.intersect(tier).Volume(),1e-7)

    def test_deep_lower_plinth_keeps_the_original_usb_pd_corridor(self):
        tier=layout.service_components()[0]['left_service_carrier_tier_1'].shape
        wire=cylinder(2.55,35.35,(33.25,97,42),(1,0,0))
        self.assertLess(wire.intersect(tier).Volume(),1e-7)
        self.assertTrue(tier.isValid())
        self.assertEqual(len(tier.Solids()),1)

    def test_purchased_component_envelopes_remain_fixed(self):
        expected = {
            "esp32": ([187.4,236,44],[57,13,28]),
            "camera": ([203.9,237,84],[24,12,24]),
            "microphone": ([207.645,14,87],[16.51,5,12.7]),
            "pca9685": ([267,234,20],[70,15,25]),
            "mcp23017": ([119,240,20],[43,5,18]),
            "pixel_shifter": ([374,136,20],[25.5,15,10.2]),
            "speaker_left": ([20,13.35,18],[45,100,21]),
            "speaker_right": ([367,16,18],[45,100,21]),
            "amplifier_left": ([46,94,49],[18,18,8]),
            "amplifier_right": ([388,116,18],[18,18,8]),
            "breaker": ([21,65,49],[45,28,13]),
            "buck": ([20,65,77],[46,31,19]),
            "pd_trigger": ([48,118.35,64],[14,28,11]),
            "powerbank": ([20,114.35,18],[26.5,139,80]),
            "perfboard_1": ([363,163,20],[50,70,1.6]),
            "perfboard_2": ([363,163,48],[50,70,1.6]),
            "degson_1": ([372,156,76],[35,22,16]),
            "degson_2": ([372,186,76],[35,22,16]),
            "degson_3": ([372,216,76],[35,22,16]),
        }
        self.assertEqual(set(layout.COMPONENTS),set(expected))
        for name,(origin,size) in expected.items():
            with self.subTest(component=name):
                self.assertEqual(layout.COMPONENTS[name]["origin"],origin)
                self.assertEqual(layout.COMPONENTS[name]["size"],size)

    def test_every_manufactured_part_is_one_valid_solid(self):
        for name,part in self.parts.items():
            with self.subTest(part=name):
                self.assertTrue(part.shape.isValid())
                self.assertEqual(len(part.shape.Solids()),1)

    def test_layout_items_do_not_intersect(self):
        self.assertEqual(collisions(layout.items()),[])

    def test_seven_service_screw_paths_stay_open(self):
        self.assertEqual(sum(map(len,SERVICE_MOUNTS.values())),7)
        for name,axes in SERVICE_MOUNTS.items():
            for x,y in axes:
                with self.subTest(part=name,axis=(x,y)):
                    shaft = cylinder(1.5,5,(x,y,11))
                    head = cylinder(2.85,100,(x,y,15))
                    self.assertLess(self.carriers[name].intersect(shaft).Volume(),1e-7)
                    self.assertLess(self.carriers[name].intersect(head).Volume(),1e-7)

    def test_split_cassettes_have_reversible_keyed_clamp_hardware(self):
        children,joints = layout.service_components()
        self.assertEqual(len(children),10)
        self.assertEqual(len(joints),11)
        self.assertEqual(Counter(j["bolt"] for j in joints),{
            "M3x12 ISO7380":10,"M3x10 ISO7380":1})
        self.assertEqual(len(layout.service_joint_hardware()),22)
        for joint in joints:
            frame = children[joint["frame"]].shape
            tier = children[joint["tier"]].shape
            with self.subTest(joint=joint["id"]):
                self.assertEqual(joint["clearance_mm"],.25)
                self.assertGreaterEqual(joint["minimum_nut_bearing_mm"],1.2)
                self.assertEqual(len(joint["key_centers"]),2)
                nut_x,y,z = joint["nut_origin"]
                backing_x = nut_x+2.4 if joint["axis"][0]<0 else nut_x-1.2
                backing = box(1.2,.2,.2,(backing_x,y-.1,z-2.7))
                self.assertLess(backing.cut(frame).Volume(),1e-7)
                for x,y,z in joint["key_centers"]:
                    key = box(1,3,1,(x-.5,y-1.5,z-.5))
                    self.assertLess(key.cut(frame).Volume(),1e-7)
                    self.assertLess(key.intersect(tier).Volume(),1e-7)

    def test_right_speaker_and_microphone_wraps_fit_the_safe_front_plane(self):
        self.assertGreaterEqual(bounds(self.carriers['carrier_speaker_right'])[1],13.35-1e-6)
        microphone=next(item.shape for item in layout.hardware() if item.name=='microphone')
        wraps=layout.ties('microphone')
        self.assertEqual(len(wraps),2)
        for wrap in wraps:
            extent=bounds(wrap.shape)
            self.assertGreaterEqual(extent[1],13.35-1e-6)
            self.assertLess(wrap.shape.intersect(microphone).Volume(),1e-7)
            self.assertLess(wrap.shape.intersect(self.carriers['carrier_microphone']).Volume(),1e-7)
            # The boundary correction translates the whole strap; it must
            # not turn its original half-mm front leg into a thin sliver.
            front_leg=box(2.5,.5,10,(extent[0],extent[1],88))
            self.assertLess(front_leg.cut(wrap.shape).Volume(),1e-7)

    def test_moving_reservations_match_complete_shared_return_bend_sweeps(self):
        import flex_routing
        mechanism=layout.PARAMS['mechanism']
        loops=[item for item in layout.reservations() if item.group=='cable']
        self.assertEqual(len(loops),6)
        for index,item in enumerate(loops,1):
            reserve=flex_routing.loop_reservation(index,mechanism['origin_x'],mechanism['pitch'])
            expected=box(*reserve['size'],reserve['origin'])
            self.assertLess(item.shape.cut(expected).Volume(),1e-7)
            self.assertLess(expected.cut(item.shape).Volume(),1e-7)
            # Both extreme lifts bound every intermediate translating return
            # bend, including cable radius, service gap and chord error.
            extent=bounds(item.shape)
            radius=2.1/2+flex_routing.CLEARANCE_MM+flex_routing.CHORD_ERROR_MM
            for lift in (0,8,16):
                for y in (flex_routing.MOVING_Y,flex_routing.FIXED_Y):
                    self.assertGreaterEqual(y-radius,extent[1]-1e-7)
                    self.assertLessEqual(y+radius,extent[4]+1e-7)
                bottom=flex_routing.CENTRE_Z-flex_routing.RADIUS_MM+lift/2-radius
                top=flex_routing.CENTRE_Z+lift/2+radius
                self.assertGreaterEqual(bottom,extent[2]-1e-7)
                self.assertLessEqual(top,extent[5]+1e-7)

    def test_split_cassette_fasteners_clear_printed_parts(self):
        children,_ = layout.service_components()
        for fastener in layout.service_joint_hardware():
            for name,part in children.items():
                with self.subTest(fastener=fastener.name,part=name):
                    self.assertLess(fastener.shape.intersect(part.shape).Volume(),1e-7)

    def test_parking_clip_retains_two_mm_nut_backing_and_flush_head(self):
        frame = layout.service_components()[0]["left_service_carrier_frame_left"].shape
        backing = box(2,.5,.8,(18.3,105.2,42.6))
        self.assertLess(backing.cut(frame).Volume(),1e-7)
        clip = layout.usb_clip()
        hardware = {item.name:item.shape for item in layout.items()}
        self.assertLess(clip.intersect(hardware["usb_clip_shaft"]).Volume(),1e-7)
        self.assertLess(clip.intersect(frame).Volume(),1e-7)

    def test_split_cassettes_pass_layer_analysis_in_declared_orientation(self):
        for name,part in layout.service_components()[0].items():
            with self.subTest(part=name):
                report = analyze(print_position(part.shape,part.rotation),{
                    "max_bridge_mm":12,"min_wall_mm":.4})
                self.assertEqual(report["status"],"PASS",report["issues"])

    def test_bank_and_speakers_pass_without_closing_service_foot_mouths(self):
        for name in ("bank_holder","carrier_speaker_left","carrier_speaker_right"):
            part = self.parts[name]
            with self.subTest(part=name):
                report = analyze(print_position(part.shape,part.rotation),{
                    "max_bridge_mm":12,"min_wall_mm":.4})
                self.assertEqual(report["status"],"PASS",report["issues"])
        rear_foot_mouth = box(23.6,9.75,15,(0,106,11))
        self.assertLess(self.parts["carrier_speaker_left"].shape.intersect(
            rear_foot_mouth).Volume(),1e-7)

    def test_control_cover_face_opening_passes_and_clears_button(self):
        part = self.parts["control_cover"]
        report = analyze(print_position(part.shape,part.rotation),{
            "max_bridge_mm":12,"min_wall_mm":.4})
        self.assertEqual(report["status"],"PASS",report["issues"])
        button = next(item.shape for item in layout.hardware() if item.name=="white_ptt")
        self.assertLess(part.shape.intersect(button).Volume(),1e-7)

    def test_capacitor_clips_clear_purchased_bodies_and_each_other(self):
        for number,(x,y,z) in enumerate(layout.PARAMS["capacitors"],1):
            name = "left_service_carrier" if number < 3 else f"capacitor_holder_{number}"
            body = cylinder(5,17,(x,y,z))
            with self.subTest(capacitor=number):
                self.assertLess(self.carriers[name].intersect(body).Volume(),1e-7)
        self.assertLess(self.carriers["capacitor_holder_5"].intersect(
            self.carriers["capacitor_holder_6"]).Volume(),1e-7)

    def test_screw_membranes_only_add_material_to_empty_shaft_regions(self):
        name = "usb_coil_cradle"
        installed = self.carriers[name]
        manufactured = self.parts[name].shape
        added = manufactured.cut(installed)
        self.assertGreater(added.Volume(),1)
        membrane_region = box(18,80,.8,(49,165,14.4))
        self.assertLess(added.cut(membrane_region).Volume(),1e-7)
        self.assertAlmostEqual(bounds(added)[2],14.4,places=6)
        self.assertAlmostEqual(bounds(added)[5],15.2,places=6)

    def test_staged_nut_roofs_preserve_flat_metal_bearing_wings(self):
        for name in ("capacitor_holder_4","bank_holder","carrier_speaker_left","carrier_speaker_right","carrier_mcp23017"):
            if name.startswith("capacitor_holder_"):
                axes = [layout.PARAMS["capacitors"][3][:2]]
            else:
                component = "powerbank" if name=="bank_holder" else name.removeprefix("carrier_")
                axes = layout.mount_positions(component)
            for x,y in axes:
                with self.subTest(part=name,axis=(x,y)):
                    # A thin prism representing the nut's top metal annulus
                    # quantifies contact at the unchanged flat bearing plane.
                    contact = layout.hex_nut(x,y,14.4).intersect(box(8,8,.01,(x-4,y-4,14.4)))
                    bearing_area = contact.intersect(self.carriers[name]).Volume()/.01
                    self.assertGreater(bearing_area,8.0)
                    location=next(record for record in layout.membrane_locations(name)
                                  if record["coordinate_mm"][:2]==[x,y] and record["kind"]=="base")
                    pilot_x,pilot_y = location["pilot_relief_size_mm"]
                    pilot = box(pilot_x,pilot_y,.4,(x-pilot_x/2,y-pilot_y/2,14.4))
                    self.assertLess(pilot.intersect(self.parts[name].shape).Volume(),1e-7)

    def test_left_speaker_rear_nut_keeps_bearing_with_narrow_pilot(self):
        name="carrier_speaker_left"
        x,y=25,109.35
        location=next(record for record in layout.membrane_locations(name)
                      if record["coordinate_mm"][:2]==[x,y])
        self.assertEqual(location["pilot_relief_size_mm"],[1.2,6.7])
        self.assertEqual(location["open_diameter_mm"],3.4)
        contact=layout.hex_nut(x,y,14.4).intersect(box(8,8,.01,(x-4,y-4,14.4)))
        bearing_area=contact.intersect(self.carriers[name]).Volume()/.01
        self.assertGreater(bearing_area,9.5)
        foot_mouth=box(23.6,9.75,15,(0,106,11))
        self.assertLess(self.carriers[name].intersect(foot_mouth).Volume(),1e-7)

    def test_mcp_and_standalone_capacitor_clips_pass_layer_analysis(self):
        for name in ("carrier_mcp23017","usb_coil_cradle",*(f"capacitor_holder_{number}" for number in range(3,7))):
            part = self.parts[name]
            with self.subTest(part=name):
                report = analyze(print_position(part.shape,part.rotation),{
                    "max_bridge_mm":12,"min_wall_mm":.4})
                self.assertEqual(report["status"],"PASS",report["issues"])

    def test_finishing_records_match_exact_staged_roofs(self):
        records = [record for name in self.parts for record in layout.membrane_locations(name)]
        self.assertEqual(len(records),40)
        self.assertEqual(Counter(record["kind"] for record in records),{"base":34,"cover":6})
        for record in records:
            self.assertEqual(record["final_puncture_thickness_mm"],.4)
            self.assertEqual(record["pilot_height_mm"],.4)
            self.assertEqual(record["filled_stage_height_mm"],.8)
            self.assertEqual(record["open_diameter_mm"],3.4)
            self.assertAlmostEqual(record["coordinate_mm"][2],record["pilot_bottom_z_mm"]+.4)
        self.assertEqual(layout.print_finishing("carrier_microphone"),"")
        self.assertNotIn("screw pilots",self.parts["carrier_microphone"].reason)

    def test_punctured_manufactured_carriers_match_installed_scene(self):
        for name,part in self.parts.items():
            locations=layout.membrane_locations(name)
            if not locations:
                continue
            tools=[cylinder(1.7,.8,(record["coordinate_mm"][0],record["coordinate_mm"][1],
                                     record["filled_stage_bottom_z_mm"])) for record in locations]
            opened=part.shape.cut(*tools)
            with self.subTest(part=name):
                self.assertLess(opened.cut(self.carriers[name]).Volume(),1e-7)
                self.assertLess(self.carriers[name].cut(opened).Volume(),1e-7)


if __name__ == "__main__":
    unittest.main()
