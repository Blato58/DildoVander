"""Envelope acceptance must fail conservatively and preserve moving ownership."""
import math
import unittest
from tray_joint_clearance import audit


def wire(points,moving=()):
    return {'id':'test','from':'fixed:pin','to':'led_1:pin','points':points,
            'outer_diameter_mm':1,'moving_endpoints':list(moving)}


class ContinuousJointTests(unittest.TestCase):
    def setUp(self):
        self.body=[{'name':'beam','module':1,'rest_bounds':[0,0,0,10,10,2]}]

    def test_static_wire_in_full_sweep_fails_between_sampled_positions(self):
        report=audit([wire([[2,2,9],[8,2,9]])],self.body,16)
        self.assertEqual(report['status'],'FAIL')

    def test_positive_envelope_gap_proves_every_translation(self):
        report=audit([wire([[12,2,9],[12,8,9]])],self.body,16)
        self.assertEqual(report['status'],'PASS')
        self.assertAlmostEqual(report['minimum']['clearance_beyond_inflated_capsule_mm'],1.2)

    def test_same_module_relative_clearance_avoids_false_swept_overlap(self):
        moving={'component':'led_1','endpoint':'led_1:pin','fixed_point':[18,2,4]}
        w=wire([[18,2,4],[14,2,4],[2,2,4],[8,2,4]],[moving])
        report=audit([w],self.body,16)
        self.assertEqual(report['status'],'PASS')
        self.assertTrue(any(row['co_moving_relative_proof'] for row in [report['minimum']]))

    def test_transport_tail_is_checked_independently(self):
        w=wire([[12,2,9],[12,8,9]]);w['transport_points']=[[2,2,9],[8,2,9]]
        report=audit([w],self.body,16)
        self.assertEqual(report['status'],'FAIL')
        self.assertTrue(all(row['state']=='transport' for row in report['collisions']))

    def test_other_module_motion_is_independent_of_the_cable_owner(self):
        body=[{'name':'owner','module':1,'rest_bounds':[30,0,0,40,10,2]},
              {'name':'other','module':2,'rest_bounds':[0,0,0,10,10,2]}]
        moving={'component':'led_1','endpoint':'led_1:pin','fixed_point':[18,2,4]}
        report=audit([wire([[18,2,4],[14,2,4],[2,2,4],[8,2,4]],[moving])],body,16)
        self.assertEqual(report['status'],'FAIL')
        self.assertTrue(any(row['obstacle']=='other' for row in report['collisions']))

    def test_half_lift_curve_cannot_claim_same_module_rigid_relative_clearance(self):
        w=wire([[2,2,4],[8,2,4]])
        w['point_lift_weights']={'1':[.5,.5]}
        report=audit([w],self.body,16)
        self.assertEqual(report['status'],'FAIL')
        self.assertTrue(all(not row['co_moving_relative_proof'] for row in report['collisions']))

    def test_missing_duplicate_or_foreign_fixed_anchor_fails_closed(self):
        for points,endpoint in (([[15,2,4],[2,2,4]],'led_1:pin'),
                                ([[15,2,4],[12,2,4],[15,2,4]],'led_1:pin'),
                                ([[12,2,4],[15,2,4]],'unknown:pin')):
            moving={'component':'led_1','endpoint':endpoint,'fixed_point':[15,2,4]}
            # First case intentionally uses a missing anchor instead.
            if len(points)==2 and points[1][0]==2: moving['fixed_point']=[16,2,4]
            with self.assertRaises(ValueError): audit([wire(points,[moving])],self.body,16)

    def test_nonfinite_diameter_and_missing_obstacles_fail_closed(self):
        w=wire([[12,2,9],[12,8,9]]);w['outer_diameter_mm']=math.nan
        with self.assertRaises(ValueError): audit([w],self.body,16)
        with self.assertRaises(ValueError): audit([wire([[12,2,9],[12,8,9]])],[],16)


if __name__=='__main__': unittest.main()
