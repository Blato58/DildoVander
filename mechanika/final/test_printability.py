"""Synthetic regressions for the tessellated FDM screen, without part exemptions."""
import json
import unittest

import cadquery as cq
import numpy as np
from shapely.geometry import Point, Polygon, box as section_box

from printability import _Mesh, _bridge_span, _profile, analyze


def block(x, y, z, origin=(0, 0, 0)):
    return cq.Solid.makeBox(x, y, z, cq.Vector(*origin))


def roof(gap):
    left = block(2, 6, 4)
    right = block(2, 6, 4, (gap + 2, 0, 0))
    return left.fuse(right, block(gap + 4, 6, 2, (0, 0, 4))).clean()


class PrintabilityTests(unittest.TestCase):
    def test_sub_tolerance_tangent_tip_preserves_resolved_bridge_checks(self):
        support=section_box(0,0,2,2).union(section_box(8,0,10,2))
        for width,expected in ((.02,6),(.08,None)):
            tip=Polygon([(5-width/2,0),(5,-1),(5+width/2,0)])
            current=section_box(0,0,10,2).union(tip)
            span=_bridge_span(current.difference(support),current,support,_profile(None))
            if expected is None:
                self.assertIsNone(span)
            else:
                self.assertAlmostEqual(span,expected,places=5)

    def test_unresolved_region_and_narrow_floating_island_cannot_pass(self):
        unresolved=section_box(0,0,.02,1)
        self.assertIsNone(_bridge_span(unresolved,unresolved,section_box(-2,0,-1,1),_profile(None)))
        shape=cq.Compound.makeCompound([block(3,3,2),block(.02,1,1,(5,0,4))])
        report=analyze(shape)
        self.assertEqual(report['status'],'FAIL_ANALYTICAL')
        self.assertGreater(report['floating_start_count'],0)

    def test_tangent_pocket_cancels_only_zero_area_duplicate_edges(self):
        shape = block(20, 20, 10).cut(cq.Solid.makeCylinder(
            5.25, 2, cq.Vector(7.25, 10, 3)))
        section = _Mesh(shape, .03).section(2.0, axis=0)
        self.assertAlmostEqual(section.area, 200, places=5)
        self.assertTrue(section.covers(Point(10, 4)))

    def test_genuine_missing_contour_fails_closed(self):
        mesh = _Mesh(block(10, 8, 6), .03)
        heights = mesh.triangles[:, :, 2]
        active = np.flatnonzero((heights.min(axis=1) <= .5) & (heights.max(axis=1) > .5))
        mesh.triangles = np.delete(mesh.triangles, active[0], axis=0)
        mesh.owners = np.delete(mesh.owners, active[0])
        with self.assertRaisesRegex(ValueError, "Incomplete mesh section"):
            mesh.section(.5)

    def test_opposite_edge_windings_preserve_closed_sections_at_rounding_ties(self):
        # Actual sloped frame seams produced different last bits when adjacent
        # triangles traversed their shared edge in opposite directions.
        for low, high, z0, z1, height in (
                (99.85, 119.875, 2.4, 4.0, 3.7),
                (0.0, 0.65, 0.0, 4.0, 0.001)):
            with self.subTest(height=height):
                shape = (cq.Workplane("YZ").polyline([
                    (low, z0), (high, z1), (high + 10, z1),
                    (low + 10, z0)]).close().extrude(10).val())
                section = _Mesh(shape, 0.03).section(height)
                y = low + (high - low) * (height - z0) / (z1 - z0)
                self.assertAlmostEqual(section.area, 100, delta=2e-5)
                self.assertTrue(section.covers(Point(5, y + 5)))
                self.assertFalse(section.covers(Point(5, y - 1)))

    def test_block_and_json_report(self):
        report = analyze(block(10, 8, 6))
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertEqual(report["layer_count"], 30)
        self.assertAlmostEqual(report["bed_contact_mm2"], 80)
        json.dumps(report, allow_nan=False)

    def test_chamfer_at_45_degrees(self):
        # A widening trapezoid, with each edge moving exactly 0.2 mm per layer.
        shape = (cq.Workplane("XZ").polyline([(2, 0), (8, 0), (12, 4), (-2, 4)])
                 .close().extrude(6).val())
        self.assertEqual(analyze(shape)["status"], "PASS")

    def test_chamfer_vertex_exactly_on_layer_midpoint_keeps_section_closed(self):
        ramp = (cq.Workplane("XZ").polyline([(10, 1.3), (12, 3.3), (10, 3.3)])
                .close().extrude(-10).val())
        shape = block(10, 10, 4).fuse(ramp).clean()
        mesh = _Mesh(shape, 0.03)
        # The ramp starts at a vertex lying exactly on the seventh layer centre.
        # Side-face tessellation above that vertex must contribute its lower edge.
        self.assertAlmostEqual(mesh.section(1.3).area, 100, places=4)
        self.assertAlmostEqual(mesh.section(1.3 - 1e-5).area, 100, places=4)
        self.assertAlmostEqual(mesh.section(1.3 + 1e-5).area, 100.0001, places=4)
        report = analyze(shape)
        self.assertEqual(report["floating_start_count"], 0, report["issues"])
        self.assertEqual(report["unsupported_count"], 0, report["issues"])

    def test_cavity_step_uses_upper_section_at_coplanar_edge(self):
        lower = cq.Solid.makeCylinder(2, 1.3, cq.Vector(5, 5, 0))
        upper = cq.Solid.makeCylinder(1, 2.7, cq.Vector(5, 5, 1.3))
        shape = block(10, 10, 4).cut(lower.fuse(upper)).clean()
        mesh = _Mesh(shape, 0.03)
        # The horizontal annular ledge itself contributes no contour segment;
        # upper side faces supply a closed hole ring at this half-open boundary.
        self.assertAlmostEqual(mesh.section(1.3).area, 100 - 3.14159, delta=0.08)
        self.assertAlmostEqual(mesh.section(1.3 - 1e-5).area, 100 - 4 * 3.14159, delta=0.08)
        self.assertAlmostEqual(mesh.section(1.3 + 1e-5).area, 100 - 3.14159, delta=0.08)

    def test_short_bridge_is_accepted_and_span_measured(self):
        report = analyze(roof(8))
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertAlmostEqual(report["max_bridge_mm"], 8, places=3)
        self.assertGreater(report["bridge_region_count"], 0)

    def test_bridge_direction_is_not_limited_to_xy_axes(self):
        shape = roof(6).rotate((0, 0, 0), (0, 0, 1), 30)
        bounds = shape.BoundingBox()
        shape = shape.translate((-bounds.xmin, -bounds.ymin, -bounds.zmin))
        report = analyze(shape)
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertAlmostEqual(report["max_bridge_mm"], 6, delta=0.01)

    def test_short_circular_closed_cap_accepts_a_verified_bridge_direction(self):
        outer = cq.Solid.makeCylinder(6, 6)
        cavity = cq.Solid.makeCylinder(3.5, 4)
        report = analyze(outer.cut(cavity).clean())
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertGreater(report["bridge_region_count"], 0)
        self.assertAlmostEqual(report["max_bridge_mm"], 7, delta=0.03)

    def test_bridge_above_numerical_tolerance_fails(self):
        self.assertGreater(analyze(roof(8.04))["unsupported_count"], 0)

    def test_wide_roof_on_two_pillars_fails(self):
        report = analyze(roof(12))
        self.assertEqual(report["status"], "FAIL_ANALYTICAL")
        self.assertGreater(report["unsupported_count"], 0)
        self.assertAlmostEqual(report["max_bridge_mm"], 12, places=3)

    def test_cantilever_shelf_fails(self):
        shape = block(2, 6, 6).fuse(block(8, 6, 2, (0, 0, 4))).clean()
        self.assertGreater(analyze(shape)["unsupported_count"], 0)

    def test_disconnected_floating_start_fails(self):
        shape = cq.Compound.makeCompound([block(3, 3, 2), block(3, 3, 2, (5, 0, 4))])
        self.assertGreater(analyze(shape)["floating_start_count"], 0)

    def test_vertical_hole_remains_a_cavity(self):
        shape = block(10, 10, 6).cut(cq.Solid.makeCylinder(2, 6, cq.Vector(5, 5, 0)))
        report = analyze(shape)
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertAlmostEqual(report["bed_contact_mm2"], 100 - 4 * 3.14159, delta=0.1)

    def test_nested_cavity_with_internal_island(self):
        outer = block(12, 12, 4).cut(block(8, 8, 4, (2, 2, 0)))
        island = block(2, 2, 4, (5, 5, 0))
        report = analyze(cq.Compound.makeCompound([outer, island]))
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertAlmostEqual(report["bed_contact_mm2"], 84)

    def test_overlapping_compound_members_do_not_cancel_material(self):
        shape = cq.Compound.makeCompound([block(10, 10, 4), block(10, 10, 4, (5, 0, 0))])
        report = analyze(shape)
        self.assertEqual(report["status"], "PASS", report["issues"])
        self.assertAlmostEqual(report["bed_contact_mm2"], 150)

    def test_round_underside_has_no_flat_bed_contact(self):
        shape = cq.Solid.makeSphere(3, cq.Vector(3, 3, 3), angleDegrees1=-90)
        report = analyze(shape)
        self.assertFalse(report["flat_bed_contact"])
        self.assertEqual(report["bed_contact_mm2"], 0)
        self.assertTrue(any(item["kind"] == "bed_contact" for item in report["issues"]))

    def test_vertical_thin_wall_fails(self):
        self.assertGreater(analyze(block(0.6, 10, 6))["thin_wall_count"], 0)

    def test_horizontal_thin_membrane_fails(self):
        shape = block(2, 8, 4).fuse(block(10, 8, 0.6, (0, 0, 4))).clean()
        self.assertGreater(analyze(shape)["thin_wall_count"], 0)

    def test_build_volume_and_shifted_base_fail(self):
        self.assertTrue(any(item["kind"] == "build_volume"
                            for item in analyze(block(221, 2, 2))["issues"]))
        self.assertTrue(any(item["kind"] == "bed_position"
                            for item in analyze(block(2, 2, 2, (0, 0, 1)))["issues"]))

    def test_steep_overhang_and_one_ended_short_bridge_fail(self):
        shape = (cq.Workplane("XZ").polyline([(2, 0), (8, 0), (14, 4), (-4, 4)])
                 .close().extrude(6).val())
        self.assertGreater(analyze(shape)["unsupported_count"], 0)
        shelf = block(2, 6, 4).fuse(block(6, 6, 2, (0, 0, 4))).clean()
        self.assertGreater(analyze(shelf)["unsupported_count"], 0)

    def test_issue_samples_are_bounded_and_tolerance_cannot_hide_defects(self):
        report = analyze(block(0.6, 10, 6), {"max_issue_samples": 2})
        self.assertLessEqual(len(report["issues"]), 2)
        self.assertTrue(report["issues_truncated"])
        with self.assertRaises(ValueError):
            analyze(block(2, 2, 2), {"tolerance_mm": 0.3})


if __name__ == "__main__":
    unittest.main()
