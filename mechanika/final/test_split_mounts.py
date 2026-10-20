"""Physical insertion paths and bearing seats of the new reversible assemblies."""
import unittest

import layout
from geometry import box, bounds, cylinder, print_position
from printability import analyze
from mechanism import guide_opening


class SplitMountTests(unittest.TestCase):
    def test_unplugged_usb_crossings_enter_closed_cassette_from_open_edges(self):
        import math
        import flex_routing
        from geometry import PARAMS, curve_tube
        children,_=layout.service_components()
        cassette=[part.shape for name,part in children.items()
                  if name.startswith('left_service_carrier_')]
        cassette += [item.shape for item in layout.service_joint_hardware()
                     if item.name.startswith('left_service_carrier_')]
        route=flex_routing.usb_bank_tail(
            PARAMS['connector_reservations']['bank_usb']['endpoint'],
            PARAMS['mechanism']['origin_x']-2.4,PARAMS['usb_coil']['origin'][1])
        turn=route['primitives'][3]
        first=2*math.pi-math.acos((67.75-turn['centre'][0])/20)
        def point(angle):
            return [turn['centre'][i]+20*(turn['u'][i]*math.cos(angle)+
                    turn['v'][i]*math.sin(angle)) for i in range(3)]
        fore=curve_tube([{'kind':'arc','start':point(first),
            'mid':point((first+2*math.pi)/2),'end':point(2*math.pi)}],2.55)
        rear=curve_tube([route['primitives'][5]],2.55)
        # Clamps are tightened first. These local R20 crossings are dressed
        # from the open Y edges with both plugs unplugged and cable uncoiled.
        # Samples verify the insertion route, not supplier bend compliance.
        for local,direction in ((fore,-1),(rear,1)):
            for step in range(41):
                placed=local.translate((0,direction*step/2,0))
                for obstacle in cassette:
                    self.assertLess(placed.intersect(obstacle).Volume(),1e-7,
                                    (direction,step/2))
        for name in ('bank_usb','pd_usb'):
            reservation=PARAMS['connector_reservations'][name]
            plug=box(*reservation['size'],reservation['origin'])
            self.assertEqual(sorted(reservation['size']),[12,12,20])
            for obstacle in cassette:
                self.assertLess(plug.intersect(obstacle).Volume(),1e-7,name)

    def test_middle_left_clamp_requires_amplifier_tier_removed_for_straight_tool(self):
        from assembly_access import cassette_clamp_tool_paths
        result=cassette_clamp_tool_paths()
        self.assertEqual(result['continuous_tool_paths'],11)
        self.assertEqual(result['remove_before_access'],{
            'left_service_carrier_tier_2_left':('left_service_carrier_tier_1',)})
        # Bench tightening precedes the boards. Installed service must remove
        # the populated lower tier: its amplifier blocks this same real axis.
        _,joints=layout.service_components()
        joint=next(j for j in joints if j['id']=='left_service_carrier_tier_2_left')
        start=tuple(joint['head_face'][k]-joint['axis'][k]*1.65 for k in range(3))
        tool=cylinder(1.7,25,start,tuple(-value for value in joint['axis']))
        amplifier=next(item.shape for item in layout.hardware() if item.name=='amplifier_left')
        self.assertGreater(tool.intersect(amplifier).Volume(),2)

    @classmethod
    def setUpClass(cls):
        cls.shapes={**layout.camera_parts(),**layout.switch_parts()}
        cls.parts={part.name:part for part in layout.parts()}

    def test_all_six_parts_are_single_solids_and_support_free(self):
        self.assertEqual(len(self.shapes),6)
        for name,shape in self.shapes.items():
            with self.subTest(part=name):
                part=self.parts[name]
                self.assertTrue(shape.isValid())
                self.assertEqual(len(shape.Solids()),1)
                report=analyze(print_position(part.shape,part.rotation),{"max_bridge_mm":12,"min_wall_mm":.4})
                self.assertEqual(report['status'],'PASS',report['issues'])

    def test_twelve_joint_hardware_items_clear_all_printed_parts(self):
        hardware=layout.camera_joint_hardware()+layout.switch_joint_hardware()
        self.assertEqual(len(hardware),12)
        for item in hardware:
            for name,shape in self.shapes.items():
                with self.subTest(hardware=item.name,part=name):
                    self.assertLess(item.shape.intersect(shape).Volume(),1e-7)

    def test_rear_countersunk_heads_clear_switch_faces_and_cover(self):
        obstacles=[item.shape for item in layout.hardware() if item.name in layout.PARAMS['panel']]
        obstacles.append(layout.covers()['control_cover'])
        for item in layout.switch_joint_hardware():
            for obstacle in obstacles:
                self.assertLess(item.shape.intersect(obstacle).Volume(),1e-7)

    def test_every_joint_has_broad_clamped_bearing_contact(self):
        for xx,yy,side in layout.SWITCH_JOINTS:
            region=box(7.5,7.5,.1,(xx-3.75,yy-3.75,101.95))
            contact=self.shapes['switch_shelf'].translate((0,0,-.01)).intersect(
                self.shapes[f'switch_frame_{side}']).intersect(region).Volume()/.01
            self.assertGreater(contact,10,(xx,yy,contact))
        for side,xx in (('left',184),('right',247)):
            region=box(7.5,7.5,.1,(xx-3.75,245.5,80.95))
            contact=self.shapes['camera_angle'].translate((0,0,-.01)).intersect(
                self.shapes[f'camera_post_{side}']).intersect(region).Volume()/.01
            self.assertGreater(contact,10,(side,contact))

    def test_joint_nuts_enter_from_the_inboard_mouth_on_the_bench(self):
        placements=[(f'camera_post_{side}',xx,249.25,75,side)
                    for side,xx in (('left',184),('right',247))]
        placements += [(f'switch_frame_{side}',xx,yy,95,side) for xx,yy,side in layout.SWITCH_JOINTS]
        for name,x,y,z,side in placements:
            nut=layout.hex_nut(x,y,z)
            for offset in range(13):
                shift=offset if side=='left' else -offset
                with self.subTest(part=name,offset=shift):
                    self.assertLess(nut.translate((shift,0,0)).intersect(self.shapes[name]).Volume(),1e-7)

    def test_camera_platform_installs_vertically_before_its_two_screws(self):
        for offset in range(21):
            plate=self.shapes['camera_angle'].translate((0,0,offset))
            for side in ('left','right'):
                self.assertLess(plate.intersect(self.shapes[f'camera_post_{side}']).Volume(),1e-7)

    def test_cassette_side_frames_can_close_onto_all_tiers(self):
        children,_=layout.service_components()
        for carrier in layout.SERVICE_MOUNTS:
            tiers=[part.shape for name,part in children.items() if name.startswith(carrier+'_tier_')]
            for side in ('left','right'):
                frame=children[f'{carrier}_frame_{side}'].shape
                for offset in range(21):
                    shifted=frame.translate((-offset if side=='left' else offset,0,0))
                    for tier in tiers:
                        self.assertLess(shifted.intersect(tier).Volume(),1e-7,(carrier,side,offset))

    def test_all_twenty_two_keys_have_receiving_end_bottom_and_back_walls(self):
        children,joints=layout.service_components()
        self.assertEqual(sum(len(joint['key_centers']) for joint in joints),22)
        for joint in joints:
            tier=children[joint['tier']].shape
            sign=-joint['axis'][0]
            for x,y,z in joint['key_centers']:
                with self.subTest(joint=joint['id'],key=(x,y,z)):
                    # Probe inside the engaged half of the tongue, beyond the
                    # quarter-mm clearance. A key hidden in a boss notch must
                    # fail even though it is in the frame and outside the tier.
                    tip=x+sign*.25
                    end_walls=(box(.25,.4,.6,(tip-.125,y-2.15,z-.3)),
                               box(.25,.4,.6,(tip-.125,y+1.75,z-.3)))
                    back_x=x+.75 if sign>0 else x-.75-3
                    back=box(3,1.6,.6,(back_x,y-.8,z-.3))
                    for wall in (*end_walls,back):
                        self.assertLess(wall.cut(tier).Volume(),1e-7)
                    # The lower lead-in is a ramped bearing seat. Require
                    # actual receiving material below the socket, rather than
                    # assuming a flat floor across the entry ramp.
                    seat=box(1.5,3.5,.25,(x-.75,y-1.75,z-1))
                    self.assertGreater(seat.intersect(tier).Volume(),.25)
                    # These are exact convex sweeps for normal X assembly,
                    # proving every intermediate translation, not just poses.
                    swept_key=box(21,3,1,(x-.5-(20 if sign>0 else 0),y-1.5,z-.5))
                    edge=x-sign*.5
                    if sign>0:
                        profile=[(edge-20,z-2.5),(edge,z-2.5),
                                 (edge+1,z-.5),(edge-20,z-.5)]
                    else:
                        profile=[(edge,z-2.5),(edge+20,z-2.5),
                                 (edge+20,z-.5),(edge-1,z-.5)]
                    swept_ramp=layout._xz_ramp(profile,y-1.5,3)
                    self.assertLess(swept_key.intersect(tier).Volume(),1e-7)
                    self.assertLess(swept_ramp.intersect(tier).Volume(),1e-7)

    def test_boundary_clamps_keep_tip_tolerance_full_nut_grip_and_bearing(self):
        children,joints=layout.service_components()
        hardware={item.name:item.shape for item in layout.service_joint_hardware()}
        for joint in joints:
            if not ((joint['carrier'].startswith('left') and joint['axis'][0]<0) or
                    (joint['carrier'].startswith('right') and joint['axis'][0]>0)):
                continue
            with self.subTest(joint=joint['id']):
                screw=hardware[joint['id'].replace('_tier_','_joint_tier_')+'_screw']
                extent=bounds(screw)
                nut_x,y,z=joint['nut_origin']
                if joint['axis'][0]<0:
                    self.assertGreaterEqual(extent[0],15.9+.35-1e-7)
                    self.assertGreaterEqual(nut_x-extent[0],.8-1e-7)
                    backing_x=nut_x+2.4
                else:
                    self.assertLessEqual(extent[3],415.9-.35+1e-7)
                    self.assertGreaterEqual(extent[3]-(nut_x+2.4),.8-1e-7)
                    backing_x=nut_x-2
                backing=box(2,.4,.4,(backing_x,y-.2,z-2.7))
                self.assertLess(backing.cut(children[joint['frame']].shape).Volume(),1e-7)

    def test_chamfered_guides_preserve_the_complete_column(self):
        for clearance in (.3,.45,.6):
            column=box(8,10,20,(-4,-5,0))
            opening=guide_opening(0,0,clearance,22,-1)
            self.assertLess(column.cut(opening).Volume(),1e-7)
            extent=bounds(opening)
            self.assertAlmostEqual(extent[3]-extent[0],8+2*clearance)
            self.assertAlmostEqual(extent[4]-extent[1],10+2*clearance)


if __name__=='__main__':
    unittest.main()
