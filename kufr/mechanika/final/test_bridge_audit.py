"""Actual-deposition support, modality and failure regressions for bridge audit."""
import json
import unittest

from bridge_audit import audit_long_bridges


def supported_stroke(anchors=(0,10,20), end=20, start=0):
    lines = ["G21","G90","M83","G1 X0 Y0 Z.2",
             ";TYPE:Perimeter",";WIDTH:.4"]
    for x in anchors:
        lines += [f"G0 X{x} Y-2",f"G1 Y2 E1"]
    lines += [f"G0 X{start} Y0 Z.4",";TYPE:Bridge infill",";WIDTH:.4",
              f"G1 X{end} E1"]
    return "\n".join(lines)


class BridgeAuditTests(unittest.TestCase):
    def test_long_stroke_over_two_holes_and_middle_island_passes(self):
        report = audit_long_bridges(supported_stroke())
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["long_bridge_segment_count"],1)
        evidence = report["evidence"][0]
        self.assertEqual(evidence["gap_count"],2)
        self.assertAlmostEqual(evidence["max_unsupported_gap_mm"],9.6)
        self.assertTrue(evidence["supported_start"])
        self.assertTrue(evidence["supported_end"])
        self.assertFalse(report["physical_validation"])
        json.dumps(report,allow_nan=False)

    def test_continuous_thirteen_mm_gap_fails(self):
        report = audit_long_bridges(supported_stroke((0,13.4),13.4))
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertAlmostEqual(report["max_unsupported_gap_mm"],13)

    def test_fully_supported_long_bridge_label_has_no_gap(self):
        text = "\n".join(["G21","G90","M83","G1 X0 Y0 Z.2",
                          ";TYPE:Perimeter",";WIDTH:.4","G1 X20 E1",
                          "G0 X0 Z.4",";TYPE:Bridge infill","G1 X20 E1"])
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["evidence"][0]["gap_count"],0)

    def test_unsupported_start_fails_even_if_every_gap_is_short(self):
        report = audit_long_bridges(supported_stroke((2,10,20)))
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertFalse(report["evidence"][0]["supported_start"])
        self.assertLess(report["max_unsupported_gap_mm"],12)

    def test_short_only_input_cannot_clear_a_long_bridge_warning(self):
        report = audit_long_bridges(supported_stroke((0,10),10))
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["positive_bridge_segment_count"],1)
        self.assertEqual(report["findings"][0]["kind"],"no_qualifying_bridge")

    def test_last_actual_layer_is_used_after_thick_bridge_z_jump(self):
        text = supported_stroke().replace("Z.4","Z.6")
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertAlmostEqual(report["evidence"][0]["previous_deposition_z_mm"],.2)
        self.assertAlmostEqual(report["evidence"][0]["layer_step_mm"],.4)

    def test_relative_xyz_absolute_e_and_coordinate_reset(self):
        text = "\n".join([
            "G21","G90","M82","G92 E0","G0 X0 Y-2 Z.2",
            ";TYPE:Perimeter",";WIDTH:.4","G1 Y2 E1",
            "G0 X10 Y-2","G1 Y2 E2","G0 X20 Y-2","G1 Y2 E3",
            "G0 X0 Y0 Z.4","G92 X100 Y100 E0","G91",
            ";TYPE:Bridge infill","G1 X20 E1"])
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["evidence"][0]["start_mm"],[0,0,.4])
        self.assertEqual(report["evidence"][0]["end_mm"],[20,0,.4])

    def test_absolute_xyz_reset_preserves_physical_support_coordinates(self):
        prefix = supported_stroke().rsplit("G1 X20 E1",1)[0]
        report = audit_long_bridges(prefix+"G92 X100 Y100\nG1 X120 Y100 E1")
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["evidence"][0]["end_mm"],[20,0,.4])

    def test_known_prusa_overhang_perimeter_still_records_actual_support(self):
        text = supported_stroke().replace("TYPE:Perimeter","TYPE:Overhang perimeter")
        self.assertEqual(audit_long_bridges(text)["status"],"PASS_LONG_BRIDGE_AUDIT")

    def test_g92_preserves_retraction_debt(self):
        prefix = supported_stroke().rsplit("G1 X20 E1",1)[0]
        text = prefix+"G1 E-2\nM82\nG92 E0\nG1 X20 E2\n"
        report = audit_long_bridges(text)
        self.assertEqual(report["long_bridge_segment_count"],0)
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")

    def test_mixed_unretraction_only_latter_part_is_deposited(self):
        prefix = supported_stroke((5,12.5,20)).rsplit("G1 X20 E1",1)[0]
        report = audit_long_bridges(prefix+"G1 E-1\nG1 X20 E4")
        self.assertEqual(report["status"],"PASS_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["evidence"][0]["start_mm"],[5,0,.4])
        self.assertEqual(report["evidence"][0]["xy_length_mm"],15)

    def test_retracted_travel_is_not_support(self):
        text = supported_stroke((0,20))
        text = text.replace("G0 X20 Y-2","G0 X10 Y-2\nG1 Y2 E-1\nG1 E1\nG0 X20 Y-2")
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertGreater(report["max_unsupported_gap_mm"],19)

    def test_later_lower_layer_deposition_cannot_supply_earlier_support(self):
        text = supported_stroke((0,20))+"\nG0 Z.2 X10 Y-2\n;TYPE:Perimeter\nG1 Y2 E1"
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertGreater(report["max_unsupported_gap_mm"],19)

    def test_same_layer_deposition_cannot_replace_previous_layer(self):
        text = supported_stroke((0,20)).replace(
            ";TYPE:Bridge infill",";TYPE:Perimeter\nG0 X10 Y-2\nG1 Y2 E1\nG0 X0 Y0\n;TYPE:Bridge infill")
        report = audit_long_bridges(text)
        self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
        self.assertEqual(report["evidence"][0]["previous_deposition_z_mm"],.2)

    def test_fail_closed_on_arc_unknown_type_and_missing_width(self):
        cases = {
            "arc":supported_stroke().replace("G1 X20 E1","G2 X20 E1 I10"),
            "unclassified":supported_stroke().replace("TYPE:Bridge infill","TYPE:Mystery bridge"),
            "missing_width":supported_stroke().replace(";WIDTH:.4\n",""),
            "negative_width":supported_stroke().replace("WIDTH:.4","WIDTH:-.4"),
            "nonfinite":supported_stroke().replace("G1 X20 E1","G1 Xnan E1"),
            "bad_word":supported_stroke()+"\nG1 Xoops",
            "nonplanar":supported_stroke().replace("G1 X20 E1","G1 X20 Z.6 E1"),
            "no_units":supported_stroke().replace("G21\n",""),
            "rotary_axis":supported_stroke().replace("G1 X20 E1","G1 X20 A5 E1"),
        }
        for name,text in cases.items():
            with self.subTest(case=name):
                report = audit_long_bridges(text)
                self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
                json.dumps(report,allow_nan=False)

    def test_floating_and_unknown_warning_never_pass(self):
        for comment in (";Floating bridge anchors",";WARNING: Long bridging extrusions, alien warning"):
            with self.subTest(comment=comment):
                report = audit_long_bridges(supported_stroke()+"\n"+comment)
                self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")

    def test_profile_and_empty_input_fail_closed(self):
        for profile in ({"max_gap_mm":float("nan")},{"nozzle_width_mm":0},
                        {"tolerance_mm":.2},{"tolerance_mm":-1}):
            with self.subTest(profile=profile):
                report = audit_long_bridges(supported_stroke(),**profile)
                self.assertEqual(report["status"],"FAIL_LONG_BRIDGE_AUDIT")
                json.dumps(report,allow_nan=False)
        self.assertEqual(audit_long_bridges("")["status"],"FAIL_LONG_BRIDGE_AUDIT")

    def test_bounded_evidence_retains_late_failure_and_total_counts(self):
        prefix = supported_stroke().rsplit("G1 X20 E1",1)[0]
        strokes = "\n".join("G0 X0 Y0\nG1 X20 E1" for _ in range(45))
        report = audit_long_bridges(prefix+strokes+"\nG0 X-2 Y0\nG1 X20 E1")
        self.assertEqual(report["long_bridge_segment_count"],46)
        self.assertEqual(len(report["evidence"]),40)
        self.assertTrue(report["evidence_truncated"])
        self.assertTrue(any(item["status"]=="FAIL" for item in report["evidence"]))


if __name__ == "__main__":
    unittest.main()
