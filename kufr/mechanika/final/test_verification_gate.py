"""The package acceptance gate must reject partial or stale wire audits."""
from copy import deepcopy
import unittest

from verify import require_complete_wire_audit


class WireAcceptanceTests(unittest.TestCase):
    def complete(self):
        return {
            "status": "PASS", "collisions": [], "bounds_failures": [],
            "sources_unchanged_during_audit": True, "truncated": False,
            "bundle_fit": {"status": "PASS"},
            "self_geometry": {"status": "PASS", "collisions": [],
                              "coverage": {"rest_wire_count": 135, "transport_wire_count": 135,
                                           "motion_angles": [0, 80, 160], "modules": list(range(1, 7))}},
            "coverage": {"rest_covers": True, "transport": True,
                         "motion_angles": [0, 80, 160], "modules": list(range(1, 7)),
                         "capsule_elbows": True},
        }

    def test_complete_sampled_audit_is_accepted(self):
        require_complete_wire_audit(self.complete())

    def test_nominal_pass_does_not_hide_failed_or_incomplete_checks(self):
        changes = [
            ("status", "STALE"), ("collisions", [{"wire": "S1-S"}]),
            ("bounds_failures", [{"wire": "S1-S"}]),
            ("sources_unchanged_during_audit", False), ("truncated", True),
            ("bundle_fit", {"status": "FAIL"}),
            ("self_geometry", {"status": "FAIL"}),
        ]
        for key, value in changes:
            with self.subTest(key=key):
                result = self.complete()
                result[key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)

    def test_self_geometry_cannot_omit_transport_or_motion(self):
        for key, value in [("rest_wire_count", 134), ("transport_wire_count", 0),
                           ("motion_angles", [0]), ("modules", [1])]:
            with self.subTest(self_coverage=key):
                result = self.complete()
                result["self_geometry"]["coverage"][key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)
        for key, value in [("rest_covers", False), ("transport", False),
                           ("motion_angles", [0]), ("modules", [1]),
                           ("capsule_elbows", False)]:
            with self.subTest(coverage=key):
                result = deepcopy(self.complete())
                result["coverage"][key] = value
                with self.assertRaises(AssertionError):
                    require_complete_wire_audit(result)


if __name__ == "__main__":
    unittest.main()
