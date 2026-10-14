import unittest
from mesh_quality import self_intersections


class MeshQualityTests(unittest.TestCase):
    def test_adjacent_coplanar_and_bent_faces_are_allowed(self):
        for other in ([[0,0,0],[1,0,0],[0,-1,0]],[[0,0,0],[1,0,0],[0,0,1]]):
            self.assertEqual(self_intersections([[[0,0,0],[1,0,0],[0,1,0]],other])['status'],'PASS_SELF_INTERSECTION')

    def test_crossing_disjoint_faces_are_rejected(self):
        result=self_intersections([[[0,0,0],[2,0,0],[0,2,0]],[[.5,.5,-1],[.5,.5,1],[1.5,.5,0]]])
        self.assertEqual(result['status'],'FAIL_SELF_INTERSECTION')

    def test_folded_neighbours_and_duplicate_faces_are_rejected(self):
        first=[[0,0,0],[2,0,0],[0,2,0]]
        for other in ([[0,0,0],[2,0,0],[.5,.5,0]],first,[[0,0,0],[1,.1,0],[.1,1,0]]):
            self.assertEqual(self_intersections([first,other])['status'],'FAIL_SELF_INTERSECTION')

    def test_vertex_only_contact_is_normal_topology(self):
        self.assertEqual(self_intersections([[[0,0,0],[1,0,0],[0,1,0]],
                                            [[0,0,0],[-1,0,0],[0,-1,0]]])['status'],'PASS_SELF_INTERSECTION')


if __name__=='__main__': unittest.main()
