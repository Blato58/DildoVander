"""Geometric flex-return contract, without assuming physical cable compliance."""
import math
import unittest
from itertools import product

import flex_routing as flex
import harness


class RollingReturnTests(unittest.TestCase):
    def test_modules_have_clear_parallel_return_lanes_and_last_has_no_unused_dout(self):
        for module in range(1,7):
            rows=[(w,r) for w in harness.HARNESSES for r in w['moving_endpoints']
                  if r['component'].endswith('_'+str(module))]
            self.assertEqual(len(rows),7 if module<6 else 6)
            rows.sort(key=lambda pair:pair[1]['return_lane_x_mm'])
            for (a,ra),(b,rb) in zip(rows,rows[1:]):
                needed=(a['outer_diameter_mm']+b['outer_diameter_mm'])/2+.6
                self.assertGreaterEqual(rb['return_lane_x_mm']-ra['return_lane_x_mm']+1e-7,needed)

    def test_return_radius_and_conservative_chord_error(self):
        points,weights=flex.rolling_return(100,38,59)
        for p in points[1:-1]:
            self.assertAlmostEqual(math.hypot(p[1]-71,p[2]-28),8)
        self.assertEqual(set(weights[1:-1]),{.5})
        for a,b in zip(points[1:-2],points[2:-1]):
            mid=[(x+y)/2 for x,y in zip(a,b)]
            deficit=8-math.hypot(mid[1]-71,mid[2]-28)
            self.assertLessEqual(deficit,flex.CHORD_ERROR_MM+1e-10)

    def test_all_flex_routes_conserve_length_across_independent_lifts(self):
        for wire in harness.HARNESSES:
            if not wire['moving_endpoints']:
                continue
            length=harness.route_length(wire['points'])
            for step in range(17):
                for row in wire['moving_endpoints']:
                    module=int(row['component'].rsplit('_',1)[1])
                    posed=flex.posed_points(wire,{module:step})
                    self.assertAlmostEqual(harness.route_length(posed),length,places=7)
                    self.assertIn(row['fixed_point'],posed)

    def test_simultaneous_independent_lifts_preserve_every_flex_length(self):
        moving=[wire for wire in harness.HARNESSES if wire['moving_endpoints']]
        lengths={wire['id']:harness.route_length(wire['points']) for wire in moving}
        for values in product((0,8,16),repeat=6):
            lifts=dict(enumerate(values,start=1))
            for wire in moving:
                posed=flex.posed_points(wire,lifts)
                self.assertAlmostEqual(harness.route_length(posed),lengths[wire['id']],places=7)

    def test_flex_core_is_inside_shared_full_travel_reservation(self):
        for module in range(1,7):
            region=harness.loop_reserve(module)
            low=region['origin'];high=[a+b for a,b in zip(low,region['size'])]
            for wire in harness.HARNESSES:
                row=next((r for r in wire['moving_endpoints']
                          if r['component'].endswith('_'+str(module))),None)
                if row is None: continue
                radius=wire['outer_diameter_mm']/2+.3+flex.CHORD_ERROR_MM
                for lift in (0,8,16):
                    points=flex.posed_points(wire,{module:lift})
                    weights=wire['point_lift_weights'][str(module)]
                    for p,w in zip(points,weights):
                        if w!=.5: continue
                        for axis in range(3):
                            self.assertGreaterEqual(p[axis]-radius+1e-7,low[axis])
                            self.assertLessEqual(p[axis]+radius-1e-7,high[axis])

    def test_fixed_trunks_and_terminal_ids_are_preserved(self):
        self.assertEqual(len(harness.HARNESSES),135)
        self.assertEqual(harness.validate()['status'],'PASS')
        for wire in harness.HARNESSES:
            posed=flex.posed_points(wire,{i:16 for i in range(1,7)})
            for index,p in enumerate(wire['points']):
                if not any(row[index] for row in wire.get('point_lift_weights',{}).values()):
                    self.assertEqual(p,posed[index])

    def test_invalid_lifts_or_short_fixed_legs_fail_closed(self):
        with self.assertRaises(ValueError): flex.rolling_return(100,35,59)
        wire={'points':[[0,0,0]],'point_lift_weights':{'1':[.5]}}
        for lift in (-1,17,float('nan')):
            with self.assertRaises(ValueError): flex.posed_points(wire,{1:lift})
        wire['point_lift_weights']['1']=[1,0]
        with self.assertRaises(ValueError): flex.posed_points(wire,{1:0})

    def test_usb_exit_exact_primitives_are_connected_and_tangent(self):
        result=flex.usb_bank_tail((33.25,114.35,46),68.6,165)
        self.assertAlmostEqual(result['exact_length_mm'],189.719358506554,places=7)
        self.assertEqual(result['points'][0],[33.25,114.35,46])
        self.assertEqual(result['points'][-1],[68.6,165,42])
        def tangent(piece,end):
            if piece['kind']=='line':
                d=[b-a for a,b in zip(piece['start'],piece['end'])]
            else:
                angle=piece['last'] if end else piece['first']
                sign=1 if piece['last']>piece['first'] else -1
                d=[sign*(-u*math.sin(angle)+v*math.cos(angle)) for u,v in zip(piece['u'],piece['v'])]
            norm=math.sqrt(sum(v*v for v in d))
            return [v/norm for v in d]
        for a,b in zip(result['primitives'],result['primitives'][1:]):
            self.assertLess(math.dist(a['end'],b['start']),1e-8)
            self.assertLess(math.dist(tangent(a,True),tangent(b,False)),1e-8)
        for p in result['primitives']:
            if p['kind']=='arc': self.assertEqual(p['radius_mm'],20)


if __name__=='__main__':
    unittest.main()
