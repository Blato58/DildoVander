"""Independent full-heading, eccentric-load and CAD-moment regression checks."""
import math
import unittest

import numpy as np

import chassis
import strength


# Immutable original 85-key, 6 mm neck layout: this configuration failed the
# corrected full-heading/eccentric-load screen and must never become a false PASS.
ORIGINAL_85_KEY_LAYOUT = {
  "floor_anchor_front_left": {
    "keys": [
      [
        23.9,
        46
      ],
      [
        23.9,
        82
      ],
      [
        35.9,
        46
      ],
      [
        35.9,
        58
      ],
      [
        35.9,
        70
      ],
      [
        35.9,
        82
      ],
      [
        35.9,
        94
      ],
      [
        35.9,
        106
      ],
      [
        47.9,
        22
      ],
      [
        47.9,
        34
      ],
      [
        47.9,
        46
      ],
      [
        47.9,
        58
      ],
      [
        47.9,
        70
      ],
      [
        47.9,
        82
      ],
      [
        47.9,
        94
      ],
      [
        47.9,
        106
      ],
      [
        59.9,
        34
      ],
      [
        95.9,
        22
      ],
      [
        143.9,
        22
      ],
      [
        191.9,
        22
      ]
    ],
    "nuts": [
      [
        79,
        30
      ],
      [
        109,
        30
      ],
      [
        127,
        30
      ],
      [
        157,
        30
      ],
      [
        175,
        30
      ],
      [
        205,
        30
      ],
      [
        27,
        30
      ]
    ]
  },
  "floor_anchor_front_right": {
    "keys": [
      [
        236.15,
        22
      ],
      [
        284.15,
        22
      ],
      [
        332.15,
        22
      ],
      [
        380.15,
        34
      ],
      [
        380.15,
        46
      ],
      [
        380.15,
        58
      ],
      [
        380.15,
        70
      ],
      [
        380.15,
        82
      ],
      [
        380.15,
        94
      ],
      [
        380.15,
        106
      ],
      [
        392.15,
        22
      ],
      [
        392.15,
        34
      ],
      [
        392.15,
        46
      ],
      [
        392.15,
        58
      ],
      [
        392.15,
        70
      ],
      [
        392.15,
        82
      ],
      [
        392.15,
        94
      ],
      [
        392.15,
        106
      ],
      [
        404.15,
        58
      ],
      [
        404.15,
        70
      ],
      [
        404.15,
        82
      ],
      [
        404.15,
        94
      ]
    ],
    "nuts": [
      [
        223,
        30
      ],
      [
        253,
        30
      ],
      [
        271,
        30
      ],
      [
        301,
        30
      ],
      [
        319,
        30
      ],
      [
        349,
        30
      ],
      [
        404.8,
        30
      ]
    ]
  },
  "floor_anchor_rear_left": {
    "keys": [
      [
        23.9,
        183.5
      ],
      [
        23.9,
        195.5
      ],
      [
        23.9,
        207.5
      ],
      [
        23.9,
        219.5
      ],
      [
        23.9,
        231.5
      ],
      [
        35.9,
        183.5
      ],
      [
        35.9,
        195.5
      ],
      [
        35.9,
        207.5
      ],
      [
        35.9,
        219.5
      ],
      [
        35.9,
        231.5
      ],
      [
        47.9,
        183.5
      ],
      [
        47.9,
        195.5
      ],
      [
        47.9,
        207.5
      ],
      [
        47.9,
        219.5
      ],
      [
        47.9,
        231.5
      ],
      [
        59.9,
        219.5
      ],
      [
        95.9,
        231.5
      ],
      [
        95.9,
        243.5
      ],
      [
        143.9,
        231.5
      ],
      [
        143.9,
        243.5
      ],
      [
        191.9,
        231.5
      ]
    ],
    "nuts": [
      [
        79,
        224
      ],
      [
        109,
        224
      ],
      [
        127,
        224
      ],
      [
        157,
        224
      ],
      [
        175,
        224
      ],
      [
        205,
        224
      ],
      [
        27,
        243
      ]
    ]
  },
  "floor_anchor_rear_right": {
    "keys": [
      [
        236.15,
        231.5
      ],
      [
        284.15,
        231.5
      ],
      [
        284.15,
        243.5
      ],
      [
        332.15,
        231.5
      ],
      [
        368.15,
        231.5
      ],
      [
        380.15,
        183.5
      ],
      [
        380.15,
        195.5
      ],
      [
        380.15,
        207.5
      ],
      [
        380.15,
        219.5
      ],
      [
        380.15,
        231.5
      ],
      [
        380.15,
        243.5
      ],
      [
        392.15,
        183.5
      ],
      [
        392.15,
        195.5
      ],
      [
        392.15,
        207.5
      ],
      [
        392.15,
        219.5
      ],
      [
        392.15,
        231.5
      ],
      [
        392.15,
        243.5
      ],
      [
        404.15,
        183.5
      ],
      [
        404.15,
        195.5
      ],
      [
        404.15,
        207.5
      ],
      [
        404.15,
        219.5
      ],
      [
        404.15,
        231.5
      ]
    ],
    "nuts": [
      [
        223,
        224
      ],
      [
        253,
        224
      ],
      [
        271,
        224
      ],
      [
        301,
        224
      ],
      [
        319,
        224
      ],
      [
        349,
        224
      ],
      [
        404.8,
        243
      ]
    ]
  }
}


def transform(points,angle,translation=(0,0)):
    c,s=math.cos(angle),math.sin(angle)
    return [(c*x-s*y+translation[0],s*x+c*y+translation[1]) for x,y in points]


def polygon_properties(vertices):
    # Independent Green's-theorem polygon integrals, not CAD inertia APIs.
    area=sx=sy=xx=yy=xy=0.0
    for (x,y),(u,v) in zip(vertices,vertices[1:]+vertices[:1]):
        cross=x*v-u*y
        area+=cross/2
        sx+=(x+u)*cross/6; sy+=(y+v)*cross/6
        xx+=(x*x+x*u+u*u)*cross/12
        yy+=(y*y+y*v+v*v)*cross/12
        xy+=(2*x*y+x*v+u*y+2*u*v)*cross/24
    if area<0:
        return polygon_properties(list(reversed(vertices)))
    cx,cy=sx/area,sy/area
    return area,[cx,cy],[[xx-area*cx*cx,xy-area*cx*cy],[xy-area*cx*cy,yy-area*cy*cy]]


class StrengthTensorTests(unittest.TestCase):
    def setUp(self):
        self.keys=[(0,0),(4,0),(8,1),(0,3),(1,7),(2,4)]
        self.applications=[(-1,2),(8,1),(1,8)]

    def assert_dense_heading_match(self,points,centre,tensor,measure,applications):
        report=strength.normal_load_envelope(points,centre,tensor,measure,applications,73.575,73.575,170)
        # Independent numerical solve on every heading; no use of the analytic
        # inverse/directional maximizer under test. Step 0.05deg bounds error.
        angles=np.linspace(0,2*math.pi,7200,endpoint=False)
        headings=np.vstack((np.cos(angles),np.sin(angles)))
        offsets=np.asarray(points)-centre
        maximum=np.full(len(points),-np.inf)
        minimum=np.full(len(points),np.inf)
        for application in applications:
            q=73.575*(np.asarray(application)-centre)[:,None]-73.575*170*headings
            values=73.575/measure+offsets@np.linalg.solve(np.asarray(tensor),q)
            maximum=np.maximum(maximum,values.max(axis=1))
            minimum=np.minimum(minimum,values.min(axis=1))
        for index,sample in enumerate(report['samples']):
            self.assertAlmostEqual(sample['maximum']['value'],maximum[index],delta=max(1e-7,abs(maximum[index])*2e-6))
            self.assertAlmostEqual(sample['minimum']['value'],minimum[index],delta=max(1e-7,abs(minimum[index])*2e-6))
        return report

    def test_asymmetric_and_rotated_key_groups_match_dense_headings(self):
        for angle in (0,.37,1.9):
            points=transform(self.keys,angle,(183,-79))
            applications=transform(self.applications,angle,(183,-79))
            centre,tensor=strength.key_group_tensor(points)
            self.assert_dense_heading_match(points,centre,tensor,len(points),applications)

    def test_rotation_translation_invariance_and_force_moment_equilibrium(self):
        centre,tensor=strength.key_group_tensor(self.keys)
        original=strength.normal_load_envelope(self.keys,centre,tensor,len(self.keys),self.applications,73.575,73.575,170)
        for angle,translation in ((.51,(0,0)),(0,(400,-211)),(2.17,(90,118))):
            keys=transform(self.keys,angle,translation)
            applications=transform(self.applications,angle,translation)
            c,g=strength.key_group_tensor(keys)
            report=strength.normal_load_envelope(keys,c,g,len(keys),applications,73.575,73.575,170)
            self.assertAlmostEqual(report['maximum']['value'],original['maximum']['value'],places=8)
            self.assertAlmostEqual(report['minimum']['value'],original['minimum']['value'],places=8)
            self.assertEqual(report['maximum']['sample_index'],original['maximum']['sample_index'])
            self.assertEqual(report['maximum']['application_index'],original['maximum']['application_index'])
            heading=transform([original['maximum']['horizontal_force_unit_xy']],angle)[0]
            np.testing.assert_allclose(report['maximum']['horizontal_force_unit_xy'],heading,atol=1e-10)
            for sample in report['samples']:
                state=sample['maximum']
                forces=strength.normal_distribution(keys,c,g,len(keys),73.575,state['first_moment_Nmm'])
                self.assertAlmostEqual(sum(forces),73.575,places=8)
                for axis in (0,1):
                    first=sum(f*(point[axis]-c[axis]) for f,point in zip(forces,keys))
                    self.assertAlmostEqual(first,state['first_moment_Nmm'][axis],places=7)
                self.assertAlmostEqual(forces[state['sample_index']],state['value'],places=8)

    def test_continuous_asymmetric_l_and_rotations_match_dense_headings(self):
        polygon=[(0,0),(11,0),(11,2),(3,2),(3,9),(0,9)]
        for angle in (0,.63,2.4):
            vertices=transform(polygon,angle,(27,-14))
            applications=transform([(1,1),(10,1),(1,8)],angle,(27,-14))
            area,centre,tensor=polygon_properties(vertices)
            self.assert_dense_heading_match(vertices,centre,tensor,area,applications)

    def test_singular_and_nonfinite_geometry_rejected(self):
        for points in ([(0,0),(1,1),(2,2)],[(3,7)]*4,[(0,0),(1,0),(math.nan,2)]):
            with self.assertRaises(ValueError):
                strength.key_group_tensor(points)
        for tensor in ([[1,2],[2,4]],[[1,2],[0,4]],[[1,0],[0,-1]]):
            with self.assertRaises(ValueError):
                strength.inverse_tensor(tensor)

    def test_production_wood_tensor_matches_independent_l_polygon_integrals(self):
        for name,spec in chassis.floor_anchor_specs().items():
            x0,x1=spec['x_range']; y0,y1=spec['y_range']
            c0,c1=spec['cross_range']; leg0=spec['leg_x']; leg1=leg0+chassis.F['floor_anchors']['side_width']
            # Union the two actual rectangles as disjoint polygon boundary.
            if spec['end']=='front' and spec['side']=='left':
                polygon=[(x0,y0),(x1,y0),(x1,c1),(leg1,c1),(leg1,y1),(x0,y1)]
            elif spec['end']=='front':
                polygon=[(x0,y0),(x1,y0),(x1,y1),(leg0,y1),(leg0,c1),(x0,c1)]
            elif spec['side']=='left':
                polygon=[(x0,y0),(leg1,y0),(leg1,c0),(x1,c0),(x1,y1),(x0,y1)]
            else:
                polygon=[(leg0,y0),(x1,y0),(x1,y1),(x0,y1),(x0,c0),(leg0,c0)]
            area,centre,tensor=polygon_properties(polygon)
            data=chassis.floor_anchor_metrics()[name]
            self.assertAlmostEqual(data['wood_bond_area_mm2'],area,places=7)
            np.testing.assert_allclose(data['wood_bond_centroid_xy_mm'],centre,atol=1e-8)
            np.testing.assert_allclose(data['wood_bond_area_second_moment_tensor_mm4'],tensor,rtol=1e-9,atol=1e-4)

    def test_original_85_key_geometry_still_fails_corrected_neck_screen(self):
        expected=(3.911878419266063,3.7039388475782475,3.3945658110511516,3.0573184233370903)
        self.assertEqual(sum(len(data['keys']) for data in ORIGINAL_85_KEY_LAYOUT.values()),85)
        for data,stress in zip(ORIGINAL_85_KEY_LAYOUT.values(),expected):
            centre,tensor=strength.key_group_tensor(data['keys'])
            report=strength.normal_load_envelope(data['keys'],centre,tensor,len(data['keys']),data['nuts'],73.575,73.575,170)
            actual=report['maximum']['value']/(math.pi*3**2)
            self.assertAlmostEqual(actual,stress,places=7)
            self.assertGreater(actual,3)

    def test_approved_geometry_passes_explicit_neck_target_and_equilibrium(self):
        report=strength.calculate()
        self.assertTrue(report['normal_load_assumption_screen_passed'])
        for data in report['anchors'].values():
            self.assertGreaterEqual(data['assumed_sideways_key_neck_ratio'],1.2)
            self.assertGreater(data['assumed_sideways_key_lip_shear_ratio'],1)
            self.assertGreater(data['assumed_wood_linear_ratio'],1)
            self.assertAlmostEqual(data['key_force_all_heading_envelope']['equilibrium_force_residual_N'],0,places=7)
            np.testing.assert_allclose(data['key_force_all_heading_envelope']['equilibrium_first_moment_residual_Nmm'],[0,0],atol=1e-7)


if __name__=='__main__':
    unittest.main()
