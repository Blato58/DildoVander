"""Topology regression checks without CAD or physical-hardware claims."""
import csv
import hashlib
import json
import math
import subprocess
import sys
import tempfile
import unittest
from copy import deepcopy
from pathlib import Path

import harness


def segment_intersects_box(start, end, bounds):
    """Open-box intersection for orthogonal route centreline regression cases."""
    return all(max(min(start[axis], end[axis]), bounds[axis]) <
               min(max(start[axis], end[axis]), bounds[axis + 3])
               if start[axis] != end[axis] else bounds[axis] < start[axis] < bounds[axis + 3]
               for axis in range(3))


class HarnessTests(unittest.TestCase):
    def wires(self):
        return deepcopy(list(harness.HARNESSES))

    def test_base_plan_validates_and_keeps_physical_acceptance_open(self):
        result = harness.validate()
        self.assertEqual("PASS", result["status"])
        self.assertEqual("NOT PERFORMED", result["physical_verification"])
        self.assertEqual({"MAIN5", "DRIVE5", "GND"}, set(harness.NETS.values()))

    def test_degs_on_capacity_rejects_sixth_port_and_parallel_port_wires(self):
        terminals = deepcopy(harness.TERMINALS)
        terminals["degson_1"][6] = "amplifier_left:VIN"
        with self.assertRaisesRegex(ValueError, "capacity"):
            harness.validate(terminals=terminals)
        wires = self.wires()
        extra = deepcopy(next(wire for wire in wires if wire["id"] == "D1-3"))
        extra["id"] = "D1-3-extra"
        wires.append(extra)
        with self.assertRaisesRegex(ValueError, "exactly one wire"):
            harness.validate(wires=wires)

    def test_degs_on_rejects_crossed_nets_and_thin_dupont(self):
        for key, value in (("net", "DRIVE5"), ("area_mm2", 0.1)):
            wires = self.wires()
            next(wire for wire in wires if wire["id"] == "D1-3")[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "wrong net or thin"):
                harness.validate(wires=wires)

    def test_fanout_cannot_mix_main_and_drive(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "F_LED+-01")["net"] = "DRIVE5"
        with self.assertRaisesRegex(ValueError, "wrong fanout net"):
            harness.validate(wires=wires)

    def test_speaker_negative_and_u059_nc_remain_isolated(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "SPK-left--")["net"] = "GND"
        with self.assertRaisesRegex(ValueError, "speaker output grounded"):
            harness.validate(wires=wires)
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "U1-DIN")["to"] = "u059_1:white"
        with self.assertRaisesRegex(ValueError, "NC must stay isolated"):
            harness.validate(wires=wires)

    def test_six_servo_and_vibration_channels_cannot_be_swapped(self):
        for identifier in ("S6-S", "U6-DIN"):
            wires = self.wires()
            next(wire for wire in wires if wire["id"] == identifier)["channel"] = 0
            with self.subTest(identifier=identifier), self.assertRaisesRegex(ValueError, "wrong PCA channel"):
                harness.validate(wires=wires)

    def test_every_moving_load_has_free_loop_and_both_anchor_labels(self):
        wires = self.wires()
        loop = next(wire for wire in wires if wire["id"] == "RGB-3")
        self.assertEqual(16, loop["lift_mm"])
        self.assertGreaterEqual(loop["motion_mm"], 64)
        loop["motion_mm"] = 16
        with self.assertRaisesRegex(ValueError, "moving harness"):
            harness.validate(wires=wires)
        loop["motion_mm"] = 64
        loop["moving_anchor"] = None
        with self.assertRaisesRegex(ValueError, "moving harness"):
            harness.validate(wires=wires)

    def test_power_does_not_use_perfboard_and_lengths_cannot_be_negative(self):
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "L3-FEED")["to"] = "perfboard_1:VCC"
        with self.assertRaisesRegex(ValueError, "power must not pass"):
            harness.validate(wires=wires)
        wires = self.wires()
        wires[0]["estimated_cut_mm"] = -1
        with self.assertRaisesRegex(ValueError, "invalid estimated length"):
            harness.validate(wires=wires)
        wires = self.wires()
        wires[0]["estimated_total_mm"] = 1
        with self.assertRaisesRegex(ValueError, "insufficient route"):
            harness.validate(wires=wires)








    def test_three_contact_rockers_have_no_external_fourth_led_positive_pin(self):
        endpoints = {endpoint for wire in harness.HARNESSES for endpoint in (wire["from"], wire["to"])}
        for rocker in ("red_main", "green_drives"):
            self.assertNotIn(f"{rocker}:LED+", endpoints)
            self.assertIn(f"{rocker}:LED-GND", endpoints)
            self.assertIn(f"{rocker}:IN", endpoints)
            self.assertIn(f"{rocker}:OUT", endpoints)
        self.assertEqual(["white_ptt:LED+"], harness.FANOUTS["F_PANEL+"]["loads"])



    def test_buck_negative_labels_alias_one_common_physical_terminal(self):
        self.assertEqual("buck:GND", harness.TERMINAL_ALIASES["buck:IN-"])
        self.assertEqual("buck:GND", harness.TERMINAL_ALIASES["buck:OUT-"])
        self.assertEqual("GND", harness.NET_ALIASES["PD_RETURN"])
        self.assertIn("One 1.5mm2 lead", harness.BUCK_GROUND_JOINT["construction"])









    def test_topology_ids_and_channels_are_unchanged(self):
        topology = [(w['id'],w['from'],w['to'],w['net'],w['channel']) for w in harness.HARNESSES]
        self.assertEqual(135, len(topology))
        self.assertEqual('54d060b24bedb1f7cc1ee525a4bb26102210a62b5969a3d31a6da066ad28c841',
                         hashlib.sha256(json.dumps(topology).encode()).hexdigest())

    def test_every_wire_uses_actual_named_terminal_anchors_and_metadata(self):
        registry = harness.endpoint_registry()
        for wire in harness.routes():
            with self.subTest(wire=wire['id']):
                self.assertEqual(registry[wire['from']]['point'], wire['points'][0])
                self.assertEqual(registry[wire['to']]['point'], wire['points'][-1])
                self.assertTrue(wire['bundle'] and wire['colour'] and wire['spec'])
                self.assertTrue(wire['from_termination'] and wire['to_termination'])
                self.assertGreater(wire['outer_diameter_mm'],0)
                self.assertIn('provisional', registry[wire['from']]['position_status'])
                for index,(a,b) in enumerate(zip(wire['points'],wire['points'][1:])):
                    if sum(x!=y for x,y in zip(a,b))>1:
                        if wire['id']=='USB-PD':
                            self.assertEqual(10,wire["curve_sampling_degrees"])
                            self.assertLessEqual(math.dist(a,b),14.5)
                        else:
                            self.assertGreater(wire['curve_chord_error_mm'],0)
                            self.assertTrue(any(row[index]==row[index+1]==.5
                                                for row in wire['point_lift_weights'].values()))
                            self.assertLessEqual(math.dist(a,b),1.4)

    def test_component_parameter_change_moves_pin_and_routes_without_topology_change(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "harness.py").write_bytes(Path(harness.__file__).read_bytes())
            (root / "flex_routing.py").write_bytes(Path(harness.flex_routing.__file__).read_bytes())
            params = deepcopy(harness.PARAMS)
            params["components"]["esp32"]["origin"][0] += 1
            (root / "parameters.json").write_text(json.dumps(params), encoding="utf-8")
            command = "import harness,json; print(json.dumps({'pin':harness.endpoint_registry()['esp32:GPIO1']['point'],'wire':next(w['points'][0] for w in harness.HARNESSES if w['id']=='SIG-SDA-IN'),'ids':[w['id'] for w in harness.HARNESSES]}))"
            completed = subprocess.run([sys.executable, "-c", command], cwd=root,
                                       capture_output=True, text=True, timeout=20, check=True)
            result = json.loads(completed.stdout)
            initial = harness.endpoint_registry()["esp32:GPIO1"]["point"]
            self.assertEqual([initial[0]+1,*initial[1:]], result["pin"])
            self.assertEqual(result["pin"],result["wire"])
            self.assertEqual([w["id"] for w in harness.HARNESSES],result["ids"])

    def test_routes_remain_inside_case_and_above_frame(self):
        case = harness.PARAMS['case']
        for wire in harness.HARNESSES:
            for point in wire['points']:
                with self.subTest(wire=wire['id'],point=point):
                    self.assertTrue(0 <= point[0] <= case['inside_x'])
                    self.assertTrue(0 <= point[1] <= case['inside_y'])
                    self.assertTrue(harness.FRAME_TOP <= point[2] <= case['inside_z'])

    def test_packed_rear_field_stays_below_lowest_reinforced_bed_beam(self):
        for wire in harness.HARNESSES:
            slot=wire.get('rear_corridor_slot')
            if slot:
                with self.subTest(wire=wire['id']):
                    self.assertLess(slot['z']+wire['outer_diameter_mm']/2+.3,52)

    def test_left_approach_skips_complete_tray_shaft_lift(self):
        low=harness.REST_TRAY_Z-14.5-1.5
        high=low+3+harness.LIFT_MM
        for wire in harness.HARNESSES:
            if 'left_corridor_z' not in wire:
                continue
            centre=wire['left_corridor_z']
            radius=wire['outer_diameter_mm']/2+.3
            with self.subTest(wire=wire['id']):
                self.assertTrue(centre+radius<low or centre-radius>high)

    def test_moving_front_loop_fixed_ties_clear_joint_beam_throughout_lift(self):
        import mechanism
        from geometry import bounds
        start,depth=mechanism.TRAY_LOWER_BEAMS[0]
        lowest_beam_z=harness.REST_TRAY_Z+min(bounds(shape)[2] for shape in mechanism.lower_beam_solids(start,depth))
        for wire in harness.HARNESSES:
            for moving in wire['moving_endpoints']:
                with self.subTest(wire=wire['id'],endpoint=moving['endpoint']):
                    # The fixed leg stays below the beam even when their Y
                    # envelopes overlap; raising the tray only grows this gap.
                    radius=wire['outer_diameter_mm']/2+.3+harness.flex_routing.CHORD_ERROR_MM
                    self.assertLess(moving['fixed_point'][2]+radius,lowest_beam_z)
                    self.assertIn(moving['fixed_point'],wire['points'])

    def test_transport_pd_is_parked_disconnected_and_coil_stays_beside_bank(self):
        wire = next(w for w in harness.routes() if w['id']=='USB-PD')
        self.assertEqual('usb_park_clip:unplugged_C', wire['transport_from'])
        self.assertEqual(list(harness.USB_PARK_POINT),wire['transport_points'][0])
        self.assertNotEqual(wire['points'][0],wire['transport_points'][0])
        self.assertEqual(wire['points'][-1],wire['transport_points'][-1])
        self.assertIn('disconnected',wire['transport_connection'])
        bank = harness.PARAMS['components']['powerbank']
        self.assertGreater(harness.USB_COIL['origin'][0],bank['origin'][0]+bank['size'][0])
        self.assertLess(harness.USB_COIL['origin'][0]+harness.USB_COIL['size'][0],harness.M['origin_x'])

    def test_parked_pd_enters_same_usb_tunnel_after_leaving_clip(self):
        wire = next(w for w in harness.routes() if w['id']=='USB-PD')
        transport=wire['transport_points']
        join=[harness.M['origin_x']-2.4,harness.USB_COIL['origin'][1],42]
        join_index=wire['points'].index(join)
        # The parked source keeps its existing Y97 tunnel. Coil/PD stay fixed.
        self.assertEqual(transport[0][1:],transport[1][1:])
        self.assertEqual(join[2],transport[2][2])
        self.assertEqual(transport[3][1:], [97,42])
        self.assertEqual(wire['points'][join_index:],transport[5:])

    def test_all_six_modules_reserve_independent_moving_endpoint_loops(self):
        modules = set()
        for wire in harness.routes():
            self.assertEqual(harness._moving_end_count(wire['from'],wire['to']),len(wire['moving_endpoints']))
            for moving in wire['moving_endpoints']:
                index = int(moving['component'].split('_')[-1])
                modules.add(index)
                self.assertEqual(harness.loop_reserve(index),moving['loop'])
                self.assertIn(moving['fixed_point'],wire['points'])
                self.assertEqual(harness.LIFT_MM,moving['lift_mm'])
        self.assertEqual(set(range(1,7)),modules)

    def test_usb_supply_is_only_passive_main5_and_short_added_cable(self):
        self.assertIn('passive USB-A',harness.SUPPLY_CONNECTOR['type'])
        self.assertIn('MAIN5 only',harness.SUPPLY_CONNECTOR['supply'])
        wire = next(w for w in harness.HARNESSES if w['id']=='USB-ESP')
        self.assertEqual(300,wire['stock_mm'])
        self.assertLessEqual(wire['estimated_total_mm'],wire['stock_mm'])
        self.assertFalse(any(w['from']=='powerbank:C1' and w['to'].startswith('esp') for w in harness.HARNESSES))
        self.assertEqual('camera:FPC',harness.CAMERA_FPC['to'])
        self.assertIn('logical adjacency only',harness.CAMERA_FPC['status'])

    def test_validator_rejects_unknown_pin_and_detached_registry_endpoint(self):
        wires=self.wires()
        wires[0]['to']='pd_trigger:invented'
        with self.assertRaisesRegex(ValueError,'unknown component or pin'):
            harness.validate(wires)
        wires=self.wires()
        wires[0]['points'][0][0]+=1
        with self.assertRaisesRegex(ValueError,'route endpoint'):
            harness.validate(wires)

    def test_export_is_complete_and_routes_are_copies(self):
        with tempfile.TemporaryDirectory() as directory:
            harness.export(directory)
            payload = json.loads((Path(directory) / "cable-plan.json").read_text(encoding="utf-8"))
            with (Path(directory) / "cable-cut-list.csv").open(encoding="utf-8-sig", newline="") as stream:
                cuts = list(csv.DictReader(stream))
            self.assertEqual(len(harness.HARNESSES), len(cuts))
            self.assertEqual(len(cuts), len(payload["harnesses"]))
            self.assertTrue(any("adapter" in gap for gap in payload["missing_acceptance"]))
            self.assertTrue(all(row["label_from"] and row["label_to"] for row in cuts))
        routes = harness.routes()
        routes[0]["points"][0][0] = -100
        self.assertNotEqual(-100, harness.HARNESSES[0]["points"][0][0])

    def test_rgb_links_reserve_slack_for_each_independent_moving_end(self):
        for index in range(1, 7):
            wire = next(wire for wire in harness.HARNESSES if wire["id"] == f"RGB-{index}")
            required_motion = 64 if index == 1 else 128
            with self.subTest(wire=wire["id"]):
                self.assertEqual(required_motion, wire["motion_mm"])
                expected_total = math.ceil((harness.route_length(wire["points"]) +
                                           wire["service_mm"] + wire["termination_mm"] + required_motion) / 10) * 10
                self.assertEqual(expected_total, wire["estimated_total_mm"])
                self.assertEqual(expected_total, wire["estimated_cut_mm"])
        for identifier in ("F_LED+-02", "U1-DIN"):
            wire = next(wire for wire in harness.HARNESSES if wire["id"] == identifier)
            self.assertEqual(64, wire["motion_mm"])

    def test_validator_rejects_only_one_loop_for_two_moving_rgb_ends(self):
        wires = self.wires()
        wire = next(wire for wire in wires if wire["id"] == "RGB-2")
        wire["motion_mm"] = 64
        with self.assertRaisesRegex(ValueError, "moving harness"):
            harness.validate(wires=wires)

    def test_validator_rejects_undersized_rgb_total_and_cut_length(self):
        wires = self.wires()
        wire = next(wire for wire in wires if wire["id"] == "RGB-2")
        wire["estimated_total_mm"] -= 60
        wire["estimated_cut_mm"] = wire["estimated_total_mm"]
        with self.assertRaisesRegex(ValueError, "insufficient route/slack"):
            harness.validate(wires=wires)
        wires = self.wires()
        next(wire for wire in wires if wire["id"] == "RGB-2")["estimated_cut_mm"] -= 10
        with self.assertRaisesRegex(ValueError, "insufficient cut length"):
            harness.validate(wires=wires)


    def test_every_occupied_degson_port_keeps_edges_after_physical_scene_filter(self):
        scene_names = {"degson_1", "degson_2", "degson_3", "buck", "pca9685", "pixel_shifter",
                       "amplifier_left", "amplifier_right", "esp32", "white_ptt", "red_main", "green_drives",
                       "mcp23017", "microphone"}
        scene_names |= {f"{prefix}_{index}" for prefix in ("led", "u059", "capacitor") for index in range(1, 7)}
        visible = [edge for edge in harness.CONNECTIONS
                   if edge["from"] in scene_names and edge["to"] in scene_names]
        self.assertEqual(len(harness.CONNECTIONS), len(visible))
        for block, ports in harness.TERMINALS.items():
            for port, endpoint in ports.items():
                if endpoint is None:
                    continue
                identifier = f"D{block[-1]}-{port}"
                with self.subTest(port=identifier):
                    self.assertTrue(any(edge["from"] == block and
                                        (edge["label"].startswith(f"{identifier}:") or edge["label"].startswith(f"{identifier} /"))
                                        for edge in visible))



class FiniteGeometryContractTests(unittest.TestCase):
    def test_declared_curve_error_cannot_hide_a_near_return(self):
        wire={'id':'curve-error','outer_diameter_mm':1.2,
              'points':[[0,0,0],[10,0,0],[10,10,0],[0,10,0],[0,1.801,0],[8,1.801,0]]}
        self.assertEqual(self.verifier.self_clearance(wire)['status'],'PASS')
        wire['curve_chord_error_mm']=harness.flex_routing.CHORD_ERROR_MM
        self.assertEqual(self.verifier.self_clearance(wire)['status'],'FAIL')

    def test_exact_curve_cache_distinguishes_same_endpoints_with_different_bends(self):
        from geometry import Item,box
        from flex_routing import usb_bank_tail
        obstacle=Item('probe',box(.4,.4,1,(75.6,80.8,41.5)),'fixed','metal')
        def cable(radius):
            curve=usb_bank_tail((33.25,114.35,46),68.6,165,radius)
            return {'id':'USB-PD','from':'powerbank:C1','to':'pd_trigger:USB-C',
                    'outer_diameter_mm':4.5,'points':curve['points'],'bank_tail':curve}
        cache={}
        self.assertEqual(self.verifier.audit([cable(20)],[obstacle],cache=cache)['status'],'PASS')
        for candidate in (cache,{}):
            result=self.verifier.audit([cable(21)],[obstacle],cache=candidate)
            self.assertEqual(result['status'],'FAIL')
            self.assertGreater(result['collisions'][0]['volume_mm3'],0)

    @classmethod
    def setUpClass(cls):
        try:
            import verify_harness_geometry
            cls.verifier=verify_harness_geometry
        except ImportError:
            raise unittest.SkipTest("Run finite geometry tests in the existing CadQuery environment")

    def test_corner_is_full_sphere_and_covers_lower_half(self):
        v=self.verifier
        sphere=v.tube((30,40,20),(30,40,20),2)
        self.assertAlmostEqual(4*math.pi*8/3,sphere.Volume(),places=6)
        self.assertTrue(sphere.isInside(v.cq.Vector(30,40,18.5)))

    def test_parked_usb_first_bend_clears_actual_clip_without_endpoint_exemption(self):
        v=self.verifier
        wire=next(row for row in harness.HARNESSES if row['id']=='USB-PD')
        radius=wire['outer_diameter_mm']/2+v.CLEARANCE_MM
        clip=v.layout.usb_clip()
        points=wire['transport_points'][:4]
        for index,point in enumerate(points[1:],1):
            with self.subTest(corner=index):
                self.assertLess(v.tube(point,point,radius).intersect(clip).Volume(),1e-5)
        for index,(start,end) in enumerate(zip(points[1:],points[2:]),1):
            with self.subTest(segment=index):
                self.assertLess(v.tube(start,end,radius).intersect(clip).Volume(),1e-5)

    def test_segment_distance_handles_crossing_parallel_and_finite_ends(self):
        distance=self.verifier.segment_distance
        self.assertAlmostEqual(0,distance((0,0,0),(10,0,0),(5,-2,0),(5,2,0)))
        self.assertAlmostEqual(5,distance((0,0,0),(10,0,0),(2,3,4),(8,3,4)))
        self.assertAlmostEqual(3,distance((0,0,0),(2,0,0),(5,0,0),(7,0,0)))

    def test_closed_usb_lap_and_returning_tail_fail_self_clearance(self):
        wire={"id":"USB-test","outer_diameter_mm":4.5,
              "points":[[0,0,0],[0,40,0],[0,40,40],[0,0,40],[0,0,0],[20,0,0]]}
        self.assertEqual("FAIL",self.verifier.self_clearance(wire)["status"])

    def test_native_usb_spiral_and_both_complete_tails_are_disjoint(self):
        wire=deepcopy(next(row for row in harness.HARNESSES if row["id"]=="USB-PD"))
        for points in (wire["points"],wire["transport_points"]):
            with self.subTest(transport=points is wire["transport_points"]):
                self.assertEqual(len(points),len({tuple(point) for point in points}))
                wire["points"]=points
                result=self.verifier.self_clearance(wire)
                self.assertEqual("PASS",result["status"],result["collisions"])
                self.assertGreaterEqual(result["minimum_nonlocal_distance_mm"],5.1)
                self.assertLessEqual(harness.route_length(points)+wire["service_mm"]+wire["termination_mm"],wire["stock_mm"])

    def test_all_wires_have_complete_self_geometry_coverage(self):
        result=self.verifier.verify_self_geometry()
        self.assertEqual("PASS",result["status"],result["collisions"])
        self.assertEqual(135,result["coverage"]["rest_wire_count"])
        self.assertEqual(135,result["coverage"]["transport_wire_count"])
        self.assertEqual([0,80,160],result["coverage"]["motion_angles"])
        self.assertEqual(list(range(1,7)),result["coverage"]["modules"])
        json.dumps(result,allow_nan=False)

    def test_short_straight_wire_has_no_nonlocal_minimum(self):
        result=self.verifier.self_clearance({"id":"short","outer_diameter_mm":4.5,
                                            "points":[[0,0,0],[5,0,0]]})
        self.assertEqual("PASS",result["status"])
        self.assertIsNone(result["minimum_nonlocal_distance_mm"])
        json.dumps(result,allow_nan=False)

    def test_endzone_never_exempts_middle_return_through_owner(self):
        v=self.verifier
        from geometry import Item,box
        wire={"id":"return-test","from":"buck:IN+","to":"esp32:GPIO1",
              "outer_diameter_mm":1.2,"points":[[30,40,20],[45,40,20],[45,41,20],[30,41,20],[35,41,20]]}
        result=v.audit([wire],[Item("buck",box(2,2,2,(30,40,19)),"hardware","metal")])
        self.assertEqual("FAIL",result["status"])
        self.assertTrue(any(row["segment"]==2 for row in result["collisions"]))
        self.assertTrue(result["endpoint_contacts"])

    def test_sampled_motion_preserves_fixed_loop_anchor_and_moves_exact_pin(self):
        v=self.verifier
        for module in range(1,7):
            wire=next(row for row in harness.HARNESSES if row["to"]==f"u059_{module}:red")
            points=v.points_for_pose(wire,module,160)
            fixed=wire["moving_endpoints"][0]["fixed_point"]
            self.assertIn(fixed,points)
            self.assertEqual(wire["points"][-1][2]+16,points[-1][2])
            self.assertEqual(wire["points"][0],points[0])

    def test_rear_packing_uses_finite_diameters_and_disjoint_physical_slots(self):
        result=self.verifier.bundle_fit(harness.HARNESSES)
        self.assertEqual("PASS",result["status"])
        self.assertGreater(result["rear_wire_count"],80)
        self.assertGreater(result["peak_conductor_area_mm2"],0)
        self.assertLess(result["peak_conductor_area_mm2"],result["window_area_mm2"])

    def test_bounded_search_proposes_clear_route_around_real_solid(self):
        v=self.verifier
        from geometry import Item,box
        items=[Item("obstacle",box(6,4,4,(42,38,18)),"fixed","metal")]
        points=v.search_route((30,40,20),(60,40,20),items,.9,max_nodes=5000)
        wire={"id":"search-test","from":"buck:IN+","to":"esp32:GPIO1",
              "outer_diameter_mm":1.2,"points":points}
        self.assertEqual("PASS",v.audit([wire],items)["status"])


if __name__ == "__main__":
    unittest.main()
