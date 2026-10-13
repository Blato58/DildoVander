"""Acceptance regressions use isolated synthetic fixtures, never production receipts."""

from copy import deepcopy
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

try:
    import project_status as tracking
except ModuleNotFoundError:
    from scripts import project_status as tracking


ARTIFACT = hashlib.sha256(b"synthetic compiled fixture bytes").hexdigest()
CONFIG = hashlib.sha256(b"synthetic configuration bytes").hexdigest()
SOFTWARE = {"artifactDigest": ARTIFACT}
PHYSICAL = {"assemblyId": "fixture-assembly", "firmwareDigest": ARTIFACT,
            "wiringRevision": "fixture-wiring-1", "mechanicsRevision": "fixture-mechanics-1",
            "configDigest": CONFIG, "operator": "fixture-operator"}


def criterion(criterion_id="C1", kinds=None, applies_to=None, refs=None):
    return {"id": criterion_id, "revision": 1, "expected": "Fixture behavior meets the specified condition.",
            "requiredEvidenceKinds": kinds or ["unit"], "rule": "allOf",
            "appliesTo": deepcopy(SOFTWARE if applies_to is None else applies_to), "evidenceRefs": refs or []}


def work_item(item_id="ITEM", initiative="DV08", state="active"):
    value = {"schemaVersion": 1, "id": item_id, "initiative": initiative,
             "title": "Synthetic fixture work", "state": state, "priority": "P1", "sequence": 1,
             "scope": ["fixture/component"], "dependencies": [], "acceptance": [criterion()],
             "blockers": [], "nextAction": "Perform the fixture test."}
    if state in {"deferred", "cancelled"}:
        value["decisionRef"] = "Fixture owner decision recorded in test setup."
    return value


def receipt(evidence_id="PASS", kind="unit", result="pass", identities=None, item_id="ITEM", criterion_id="C1"):
    return {"schemaVersion": 1, "evidenceId": evidence_id, "observedAtUtc": "2026-10-13T10:00:00Z",
            "kind": kind, "result": result,
            "claims": [{"workItemId": item_id, "criterionId": criterion_id, "criterionRevision": 1, "result": result}],
            "subject": deepcopy(SOFTWARE if identities is None else identities),
            "method": "Synthetic test fixture method", "environment": "Isolated Python unittest fixture",
            "limitations": ["Synthetic data exercises evaluator logic only."],
            "observation": "Fixture command produced the recorded result."}


def blocker(blocker_id="B1", blocks=None):
    return {"id": blocker_id, "problem": "Fixture prerequisite unavailable.", "severity": "critical",
            "blocks": blocks or ["C1"], "unblockCondition": "Supply the fixture prerequisite."}


class TrackingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.directory = self.root / "docs/project"
        self.item = work_item()
        self.write("work-items/ITEM.json", self.item)
        self.write("project.json", {"schemaVersion": 1, "focusInitiative": "DV08", "currentTarget": "TARGET"})
        self.target = {"schemaVersion": 1, "targetId": "TARGET", "purpose": "Synthetic test candidate",
                       "components": deepcopy(SOFTWARE), "requiredCriteria": [{"workItemId": "ITEM", "criterionId": "C1"}]}
        self.write("targets/TARGET.json", self.target)

    def write(self, reference, value):
        path = self.directory / reference
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    def project(self):
        value = tracking.Project(self.root)
        value.validate()
        return value

    def invoke(self, *arguments):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            result = tracking.main(["--root", str(self.root), *arguments])
        return result, stdout.getvalue(), stderr.getvalue()

    def passed(self):
        self.write("evidence/PASS.json", receipt())
        self.item["acceptance"][0]["evidenceRefs"] = ["PASS"]
        self.write("work-items/ITEM.json", self.item)

    def close(self):
        self.passed()
        self.item["state"] = "closed"
        self.item["closure"] = {"outcome": "accepted", "reason": "Synthetic acceptance met.",
                                "subject": deepcopy(SOFTWARE), "evidenceRefs": ["PASS"],
                                "acceptedCriteria": deepcopy(self.item["acceptance"])}
        self.write("work-items/ITEM.json", self.item)

    def test_01_software_only_can_close_without_physical_phase(self):
        self.close()
        project = self.project()
        self.assertEqual("passed", project.verification(project.items["ITEM"]))
        self.assertEqual("ready", project.gate("TARGET")["state"])

    def test_02_unit_cannot_satisfy_physical_kinds(self):
        for kind in ("esp_runtime", "electrical_measurement"):
            with self.subTest(kind=kind):
                self.passed()
                self.item["acceptance"][0]["requiredEvidenceKinds"] = [kind]
                self.write("work-items/ITEM.json", self.item)
                self.assertEqual("unverified", self.project().verification(self.item))

    def test_03_revision_change_stales_old_evidence_preserving_closure(self):
        self.close()
        self.item["acceptance"][0]["revision"] = 2
        self.write("work-items/ITEM.json", self.item)
        project = self.project()
        self.assertEqual("stale", project.verification(self.item))
        self.assertEqual(1, project.items["ITEM"]["closure"]["acceptedCriteria"][0]["revision"])

    def physical_passed(self):
        self.item["acceptance"] = [criterion(kinds=["esp_runtime"], applies_to={key: PHYSICAL[key] for key in
                                      ("assemblyId", "firmwareDigest", "wiringRevision", "mechanicsRevision", "configDigest")}, refs=["PHYSICAL"])]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/PHYSICAL.json", receipt("PHYSICAL", "esp_runtime", identities=PHYSICAL))
        self.target["components"] = {"device": deepcopy(PHYSICAL)}
        self.write("targets/TARGET.json", self.target)

    def test_04_relevant_firmware_and_wiring_changes_stale(self):
        self.physical_passed()
        for key, changed in (("firmwareDigest", CONFIG), ("wiringRevision", "fixture-wiring-2")):
            with self.subTest(key=key):
                self.target["components"] = {"device": {**PHYSICAL, key: changed}}
                self.write("targets/TARGET.json", self.target)
                self.assertEqual("stale", self.project().gate("TARGET")["state"])

    def test_05_document_change_does_not_stale_identical_physical_subject(self):
        self.physical_passed()
        self.assertEqual("ready", self.project().gate("TARGET")["state"])
        (self.root / "README.md").write_text("Documentation change\n", encoding="utf-8")
        self.target["components"]["documentation"] = {"commit": "abcdef1234567", "dirty": False}
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])

    def test_06_dirty_test_cannot_accept_clean_head(self):
        identities = {"commit": "abcdef1234567", "dirty": False, "artifactDigest": ARTIFACT}
        self.item["acceptance"] = [criterion(applies_to=identities, refs=["DIRTY"])]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/DIRTY.json", receipt("DIRTY", identities={**identities, "dirty": True}))
        self.target["components"] = identities
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("stale", self.project().gate("TARGET")["state"])
        invalid = receipt("DIRTY", identities={"commit": "abcdef1234567", "dirty": True})
        self.write("evidence/DIRTY.json", invalid)
        self.assertEqual(2, self.invoke("validate")[0])

    def test_07_unselected_fail_is_not_hidden_by_older_pass(self):
        self.passed()
        failure = receipt("FAIL", result="fail")
        failure["observedAtUtc"] = "2026-10-13T11:00:00Z"
        self.write("evidence/FAIL.json", failure)
        project = self.project()
        self.assertEqual("conflict", project.verification(self.item))
        self.assertEqual("failed", project.gate("TARGET")["state"])
        self.assertEqual(1, self.invoke("validate", "--readiness")[0])

    def test_failure_resolution_requires_retest_and_explicit_selection(self):
        self.passed()
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        retest = receipt("RETEST")
        retest["supersedes"] = ["FAIL", "PASS"]
        self.write("evidence/RETEST.json", retest)
        self.assertEqual(2, self.invoke("validate")[0])
        retest["resolution"] = "Corrected fixture cause and reran the same test successfully."
        self.write("evidence/RETEST.json", retest)
        self.assertEqual("conflict", self.project().verification(self.item))
        self.item["acceptance"][0]["evidenceRefs"] = ["RETEST"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("passed", self.project().verification(self.item))

    def test_cross_subject_supersedes_explanation_cannot_hide_original_fail(self):
        self.passed()
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        changed = receipt("CHANGED", identities={"artifactDigest": CONFIG})
        changed["supersedes"] = ["FAIL"]
        changed["resolution"] = "Retested modified fixture."
        self.write("evidence/CHANGED.json", changed)
        self.assertEqual(2, self.invoke("validate")[0])
        changed["supersedesReason"] = "New artifact identity after fixture repair."
        self.write("evidence/CHANGED.json", changed)
        self.assertEqual("conflict", self.project().verification(self.item))

    def test_inconclusive_supersedes_cannot_hide_failure(self):
        self.passed()
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        corrected = receipt("CORRECTED", result="inconclusive")
        corrected["supersedes"] = ["FAIL"]
        self.write("evidence/CORRECTED.json", corrected)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])
        retest = receipt("RETEST")
        retest["supersedes"] = ["FAIL"]
        retest["resolution"] = "Reran the corrected fixture successfully."
        self.write("evidence/RETEST.json", retest)
        corrected["supersedes"] = ["RETEST"]
        self.write("evidence/CORRECTED.json", corrected)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])

    def test_resolution_requires_selected_available_required_kind(self):
        self.passed()
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        retest = receipt("RETEST")
        retest["supersedes"] = ["FAIL"]
        retest["resolution"] = "Reran the corrected fixture successfully."
        self.write("evidence/RETEST.json", retest)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])
        self.item["acceptance"][0]["evidenceRefs"] = ["PASS", "RETEST"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])
        retest.pop("observation")
        retest["artifactRefs"] = ["missing-fixture-output.log"]
        self.write("evidence/RETEST.json", retest)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])
        retest["observation"] = "Self-contained fixture re-test."
        retest["kind"] = "source_review"
        self.write("evidence/RETEST.json", retest)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])

    def test_supersedes_chain_retains_explicit_successful_resolution(self):
        self.passed()
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        retest = receipt("RETEST")
        retest["supersedes"] = ["FAIL", "PASS"]
        retest["resolution"] = "Reran the corrected fixture successfully."
        self.write("evidence/RETEST.json", retest)
        latest = receipt("LATEST")
        latest["supersedes"] = ["RETEST"]
        self.write("evidence/LATEST.json", latest)
        self.assertEqual("conflict", self.project().verification(self.item))
        self.item["acceptance"][0]["evidenceRefs"] = ["LATEST"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("passed", self.project().verification(self.item))

    def test_08_deferred_required_dependency_still_blocks(self):
        dependency = work_item("DEP", state="deferred")
        self.write("work-items/DEP.json", dependency)
        self.item["dependencies"] = [{"itemId": "DEP", "requiredMilestone": "accepted"}]
        self.write("work-items/ITEM.json", self.item)
        item, reason, _ = self.project().next_item()
        self.assertIsNone(item)
        self.assertIn("requires accepted (state deferred)", reason)

    def test_09_next_explains_block_without_switching_initiative(self):
        self.item["blockers"] = [blocker(blocks=["continuation"])]
        self.write("work-items/ITEM.json", self.item)
        self.write("work-items/OTHER.json", work_item("OTHER", "OTHER"))
        result, stdout, _ = self.invoke("next")
        self.assertEqual(1, result)
        self.assertIn("blocker B1", stdout)
        self.assertNotIn("OTHER:", stdout)
        self.assertEqual(0, self.invoke("next", "--initiative", "OTHER", "--scope", "fixture")[0])

    def test_implemented_milestone_is_explicit_not_lifecycle_guess(self):
        dependency = work_item("DEP", state="planned")
        self.write("work-items/DEP.json", dependency)
        self.item["dependencies"] = [{"itemId": "DEP", "requiredMilestone": "implemented"}]
        self.item["priority"] = "P0"
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("DEP", self.project().next_item()[0]["id"])
        dependency["implementedSubject"] = deepcopy(SOFTWARE)
        self.write("work-items/DEP.json", dependency)
        self.assertEqual("ITEM", self.project().next_item()[0]["id"])

    def test_historical_acceptance_does_not_authorize_stale_dependency(self):
        self.close()
        child = work_item("CHILD")
        child["dependencies"] = [{"itemId": "ITEM", "requiredMilestone": "accepted"}]
        self.write("work-items/CHILD.json", child)
        self.assertEqual("CHILD", self.project().next_item()[0]["id"])
        self.target["components"]["artifactDigest"] = CONFIG
        self.write("targets/TARGET.json", self.target)
        project = self.project()
        self.assertIsNone(project.next_item()[0])
        self.assertEqual("accepted", project.items["ITEM"]["closure"]["outcome"])

    def test_implemented_dependency_checks_known_target_identity(self):
        dependency = work_item("DEP")
        dependency["implementedSubject"] = deepcopy(SOFTWARE)
        self.write("work-items/DEP.json", dependency)
        self.item["dependencies"] = [{"itemId": "DEP", "requiredMilestone": "implemented"}]
        self.item["priority"] = "P0"
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("ITEM", self.project().next_item()[0]["id"])
        self.target["components"]["artifactDigest"] = CONFIG
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("DEP", self.project().next_item()[0]["id"])

    def test_implemented_subject_requires_exact_bytes(self):
        for identities in ({"revision": 1}, {"assemblyId": "fixture-assembly"}, {"commit": "abcdef1234567"}):
            with self.subTest(identities=identities):
                self.item["implementedSubject"] = identities
                self.write("work-items/ITEM.json", self.item)
                self.assertEqual(2, self.invoke("validate")[0])

    def test_gate_respects_direct_and_transitive_hard_dependencies(self):
        self.passed()
        dependency = work_item("DEP", state="deferred")
        self.write("work-items/DEP.json", dependency)
        self.item["dependencies"] = [{"itemId": "DEP", "requiredMilestone": "accepted"}]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])
        dependency["state"] = "planned"
        dependency["implementedSubject"] = deepcopy(SOFTWARE)
        dependency["dependencies"] = [{"itemId": "GRAND", "requiredMilestone": "accepted"}]
        self.write("work-items/DEP.json", dependency)
        self.write("work-items/GRAND.json", work_item("GRAND", state="deferred"))
        self.item["dependencies"][0]["requiredMilestone"] = "implemented"
        self.write("work-items/ITEM.json", self.item)
        gate = self.project().gate("TARGET")
        self.assertEqual("failed", gate["state"])
        self.assertIn("GRAND requires accepted", gate["details"][0]["reason"])

    def test_gate_dependencies_use_explicit_target_instead_of_config_target(self):
        self.passed()
        dependency = work_item("DEP", state="closed")
        dependency["acceptance"][0]["evidenceRefs"] = ["DEP-PASS"]
        dependency["closure"] = {"outcome": "accepted", "reason": "Fixture historical acceptance.", "subject": SOFTWARE,
                                 "evidenceRefs": ["DEP-PASS"], "acceptedCriteria": deepcopy(dependency["acceptance"])}
        self.write("work-items/DEP.json", dependency)
        self.write("evidence/DEP-PASS.json", receipt("DEP-PASS", item_id="DEP"))
        self.item["dependencies"] = [{"itemId": "DEP", "requiredMilestone": "accepted"}]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/PASS.json", receipt(identities={"artifactDigest": CONFIG}))
        second = deepcopy(self.target)
        second["targetId"] = "SECOND"
        second["components"] = {"artifactDigest": CONFIG}
        self.write("targets/SECOND.json", second)
        gate = self.project().gate("SECOND")
        self.assertEqual("blocked", gate["details"][0]["state"])
        self.assertIn("DEP requires accepted", gate["details"][0]["reason"])

    def test_10_hidden_sixth_blocker_changes_gate(self):
        self.passed()
        self.item["blockers"] = [blocker(f"B{number}", blocks=["continuation"]) for number in range(1, 7)]
        self.write("work-items/ITEM.json", self.item)
        project = self.project()
        self.assertEqual("failed", project.gate("TARGET")["state"])
        rendered = project.render().decode("utf-8")
        self.assertIn("hidden: 1", rendered)
        self.assertNotIn("ITEM/B6", rendered)
        self.assertIn("critical: 6", rendered)
        self.assertIn("ITEM/B6", self.invoke("blockers")[1])

    def test_11_empty_gate_not_configured(self):
        self.target["requiredCriteria"] = []
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("not_configured", self.project().gate("TARGET")["state"])
        self.assertEqual(1, self.invoke("validate", "--readiness")[0])

    def test_12_published_release_does_not_imply_acceptance(self):
        self.write("releases/RELEASE.json", {"schemaVersion": 1, "releaseId": "RELEASE",
                   "publishedAtUtc": "2026-10-13T10:00:00Z", "subject": SOFTWARE,
                   "sourceRevision": "abcdef1234567", "artifactRefs": ["https://github.com/fixture/release"], "targetId": "TARGET"})
        project = self.project()
        self.assertEqual("unknown", project.gate("TARGET")["state"])
        self.assertEqual("unverified", project.verification(self.item))

    def test_13_repeated_render_bytes_and_digest_excludes_snapshot_archives(self):
        project = self.project()
        before = project.render()
        digest = project.digest()
        tracking.atomic_write(self.directory / "STATUS.md", before)
        archive = self.directory / "archives/old.md"
        archive.parent.mkdir()
        archive.write_bytes(b"Archived text\r\n")
        self.assertEqual(before, project.render())
        self.assertEqual(digest, project.digest())
        self.assertEqual(0, self.invoke("render", "--check")[0])

    def test_14_render_and_validation_never_require_network(self):
        self.passed()
        with patch("urllib.request.urlopen", side_effect=AssertionError("network used")):
            self.assertEqual(0, self.invoke("render")[0])
            self.assertEqual(0, self.invoke("validate")[0])

    def test_15_invalid_reference_cycle_placeholder_and_types_are_clean_errors(self):
        cases = []
        unknown = deepcopy(self.item)
        unknown["acceptance"][0]["evidenceRefs"] = ["MISSING"]
        cases.append(unknown)
        cyclic = deepcopy(self.item)
        cyclic["dependencies"] = [{"itemId": "ITEM", "requiredMilestone": "accepted"}]
        cases.append(cyclic)
        placeholder = deepcopy(self.item)
        placeholder["title"] = "TODO"
        cases.append(placeholder)
        wrong_type = deepcopy(self.item)
        wrong_type["priority"] = {}
        cases.append(wrong_type)
        empty_subject = deepcopy(self.item)
        empty_subject["acceptance"][0]["appliesTo"] = {}
        cases.append(empty_subject)
        malformed_closure = deepcopy(self.item)
        malformed_closure["closure"] = []
        cases.append(malformed_closure)
        for value in cases:
            with self.subTest(value=value):
                self.write("work-items/ITEM.json", value)
                result, _, stderr = self.invoke("validate")
                self.assertEqual(2, result)
                self.assertIn("Invalid tracking data:", stderr)
                self.assertNotIn("Traceback", stderr)

    def test_work_item_replacement_cycles_rejected(self):
        self.item["state"] = "closed"
        self.item["closure"] = {"outcome": "superseded", "reason": "Synthetic replacement.", "replacementItemId": "OTHER"}
        self.write("work-items/ITEM.json", self.item)
        other = work_item("OTHER", state="closed")
        other["closure"] = {"outcome": "superseded", "reason": "Synthetic replacement.", "replacementItemId": "ITEM"}
        self.write("work-items/OTHER.json", other)
        self.assertEqual(2, self.invoke("validate")[0])

    def test_16_failed_gate_is_valid_tracking_but_fails_readiness(self):
        self.item["acceptance"][0]["evidenceRefs"] = ["FAIL"]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/FAIL.json", receipt("FAIL", result="fail"))
        self.assertEqual(0, self.invoke("validate")[0])
        self.assertEqual(1, self.invoke("validate", "--readiness")[0])

    def migration(self):
        original = b"# Puvodni dokument\r\n\r\nOriginal current claim.\r\n"
        source = self.root / "docs/original.md"
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes(original)
        archive = self.directory / "archives/original.md"
        archive.parent.mkdir(parents=True, exist_ok=True)
        archive.write_bytes(original)
        self.item["blockers"] = [blocker()]
        self.write("work-items/ITEM.json", self.item)
        migration = {"schemaVersion": 1, "sources": [{"path": "docs/original.md", "sha256": hashlib.sha256(original).hexdigest(),
                      "archivePath": "docs/project/archives/original.md"}],
                     "mappings": [{"source": "docs/original.md", "section": "Original", "lineStart": 1, "lineEnd": 3,
                      "classification": "current", "destinations": ["docs/project/work-items/ITEM.json#B1"], "reason": "Preserve fixture blocker."}],
                     "openBlockers": [{"source": "docs/original.md", "section": "Original", "workItemId": "ITEM", "blockerId": "B1"}],
                     "links": [{"source": "docs/original.md", "href": "project/work-items/ITEM.json#C1"}],
                     "rollback": "Restore the original archived bytes and prior instruction routing."}
        self.write("migration.json", migration)
        return source, archive, migration

    def test_17_migration_hash_checks_original_bytes(self):
        _, archive, _ = self.migration()
        self.assertEqual(0, self.invoke("migration-check")[0])
        archive.write_bytes(archive.read_bytes().replace(b"\r\n", b"\n"))
        result, _, stderr = self.invoke("migration-check")
        self.assertEqual(2, result)
        self.assertIn("archive hash mismatch", stderr)

    def test_18_migration_rejects_lost_open_blocker(self):
        _, _, migration = self.migration()
        migration["openBlockers"][0]["blockerId"] = "MISSING"
        self.write("migration.json", migration)
        self.assertEqual(2, self.invoke("migration-check")[0])

    def test_19_dry_run_does_not_change_old_workflow_and_repeats_without_duplicates(self):
        source, _, _ = self.migration()
        snapshot = {path: path.read_bytes() for path in self.root.rglob("*") if path.is_file()}
        self.assertEqual(0, self.invoke("migration-check")[0])
        self.assertEqual(0, self.invoke("migration-check")[0])
        self.assertEqual(snapshot, {path: path.read_bytes() for path in self.root.rglob("*") if path.is_file()})
        self.assertIn(b"Original current claim", source.read_bytes())

    def test_20_fixture_paths_and_render_are_platform_independent(self):
        self.passed()
        rendered = self.project().render()
        self.assertNotIn(str(self.root).encode(), rendered)
        self.assertNotIn(b"\r\n", rendered)
        self.assertIn(b"work-items/ITEM.json", rendered)
        result = subprocess.run([sys.executable, str(Path(tracking.__file__)), "validate", "--root", str(self.root)],
                                capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr.decode("utf-8"))

    def test_windows_codepage_does_not_break_unicode_cli(self):
        self.item["nextAction"] = "Ověřit příští krok sestavy."
        self.write("work-items/ITEM.json", self.item)
        result = subprocess.run([sys.executable, str(Path(tracking.__file__)), "next", "--root", str(self.root)],
                                env={**os.environ, "PYTHONIOENCODING": "cp1252"}, capture_output=True, check=False)
        self.assertEqual(0, result.returncode, result.stderr.decode("utf-8"))
        self.assertIn(self.item["nextAction"], result.stdout.decode("utf-8"))

    def test_artifact_traversal_and_malicious_references_are_rejected(self):
        for reference in ("../secret", "/secret", "C:/secret", "C:\\secret", "%2e%2e/secret",
                          "file:///secret", "https://user:password@github.com/a", "a//b", "a/%2e%2e/secret"):
            with self.subTest(reference=reference):
                value = receipt()
                value["artifactRefs"] = [reference]
                self.write("evidence/PASS.json", value)
                self.assertEqual(2, self.invoke("validate")[0])

    def test_missing_or_remote_artifacts_are_unavailable_not_failed_test(self):
        self.passed()
        value = receipt()
        del value["observation"]
        for reference in ("missing.log", "https://github.com/fixture/artifact"):
            with self.subTest(reference=reference):
                value["artifactRefs"] = [reference]
                self.write("evidence/PASS.json", value)
                with patch("urllib.request.urlopen", side_effect=AssertionError("offline network used")):
                    self.assertEqual(0, self.invoke("validate")[0])
                    self.assertEqual(3, self.invoke("verify-evidence")[0])
                    self.assertEqual(3, self.invoke("validate", "--readiness")[0])
                self.assertEqual("unverified", self.project().verification(self.item))
        value["observation"] = "Self-contained fixture observation."
        self.write("evidence/PASS.json", value)
        self.assertEqual(0, self.invoke("verify-evidence")[0])
        self.assertEqual("passed", self.project().verification(self.item))

    def test_local_artifact_availability_and_online_failure(self):
        self.passed()
        value = receipt()
        del value["observation"]
        value["artifactRefs"] = ["fixture.log"]
        self.write("evidence/PASS.json", value)
        (self.root / "fixture.log").write_text("Passed fixture output\n", encoding="utf-8")
        self.assertEqual(0, self.invoke("verify-evidence")[0])
        self.assertEqual("passed", self.project().verification(self.item))
        value["artifactRefs"] = ["https://github.com/fixture/artifact"]
        self.write("evidence/PASS.json", value)
        with patch("urllib.request.urlopen", side_effect=OSError("offline")):
            self.assertEqual(3, self.invoke("verify-evidence", "--online")[0])

    def test_physical_fields_and_electrical_instrument_are_required(self):
        self.physical_passed()
        value = receipt("PHYSICAL", "electrical_measurement", identities=PHYSICAL)
        self.write("evidence/PHYSICAL.json", value)
        self.assertEqual(2, self.invoke("validate")[0])
        value["instrument"] = "fixture-meter"
        value["measurements"] = [{"quantity": "voltage", "value": 5.0, "unit": "V"}]
        self.write("evidence/PHYSICAL.json", value)
        self.assertEqual(0, self.invoke("validate")[0])
        del value["subject"]["wiringRevision"]
        self.write("evidence/PHYSICAL.json", value)
        self.assertEqual(2, self.invoke("validate")[0])

    def test_underspecified_physical_target_cannot_accept_exact_receipt(self):
        self.physical_passed()
        self.item["acceptance"][0]["appliesTo"] = {"assemblyId": PHYSICAL["assemblyId"]}
        self.write("work-items/ITEM.json", self.item)
        self.target["components"] = {"assemblyId": PHYSICAL["assemblyId"]}
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("stale", self.project().gate("TARGET")["state"])
        self.target["components"] = deepcopy(PHYSICAL)
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])
        self.assertEqual("stale", self.project().verification(self.item))

    def test_physical_retest_can_change_operator_without_changing_subject_axes(self):
        self.physical_passed()
        self.write("evidence/FAIL.json", receipt("FAIL", "esp_runtime", result="fail", identities=PHYSICAL))
        retest = receipt("RETEST", "esp_runtime", identities={**PHYSICAL, "operator": "fixture-operator-2"})
        retest["supersedes"] = ["FAIL", "PHYSICAL"]
        retest["supersedesReason"] = "Second fixture operator repeated the same physical procedure."
        retest["resolution"] = "Corrected fixture cause and observed successful repeated run."
        self.write("evidence/RETEST.json", retest)
        self.item["acceptance"][0]["evidenceRefs"] = ["RETEST"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])

    def test_required_cross_kind_retest_cannot_clear_physical_failure(self):
        self.physical_passed()
        self.item["acceptance"][0]["requiredEvidenceKinds"] = ["esp_runtime", "source_review"]
        self.item["acceptance"][0]["appliesTo"]["artifactDigest"] = ARTIFACT
        self.target["components"]["device"]["artifactDigest"] = ARTIFACT
        self.write("targets/TARGET.json", self.target)
        physical = {**PHYSICAL, "artifactDigest": ARTIFACT}
        self.write("evidence/PHYSICAL.json", receipt("PHYSICAL", "esp_runtime", identities=physical))
        self.write("evidence/FAIL.json", receipt("FAIL", "esp_runtime", result="fail", identities=physical))
        reviewed = receipt("REVIEW", "source_review", identities=physical)
        reviewed["supersedes"] = ["FAIL"]
        reviewed["resolution"] = "Inspected corrected fixture source."
        self.write("evidence/REVIEW.json", reviewed)
        self.item["acceptance"][0]["evidenceRefs"] = ["PHYSICAL", "REVIEW"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("failed", self.project().gate("TARGET")["state"])

    def test_assembly_only_native_receipt_does_not_identify_software(self):
        self.item["acceptance"] = [criterion(kinds=["native_ios"], applies_to={"assemblyId": "fixture-assembly"}, refs=["NATIVE"])]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/NATIVE.json", receipt("NATIVE", "native_ios", identities={**SOFTWARE, "assemblyId": "fixture-assembly"}))
        self.target["components"] = {"assemblyId": "fixture-assembly"}
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("stale", self.project().gate("TARGET")["state"])

    def test_distinct_component_artifacts_scope_revalidation(self):
        self.item["acceptance"] = [criterion("IOS", ["native_ios"], {"iosArtifactDigest": ARTIFACT}, ["IOS-PASS"]),
                                   criterion("BACKEND", ["integration"], {"backendArtifactDigest": CONFIG}, ["BACKEND-PASS"])]
        self.write("work-items/ITEM.json", self.item)
        self.write("evidence/IOS-PASS.json", receipt("IOS-PASS", "native_ios", identities={"iosArtifactDigest": ARTIFACT}, criterion_id="IOS"))
        self.write("evidence/BACKEND-PASS.json", receipt("BACKEND-PASS", "integration", identities={"backendArtifactDigest": CONFIG}, criterion_id="BACKEND"))
        self.target["components"] = {"ios": {"iosArtifactDigest": ARTIFACT}, "backend": {"backendArtifactDigest": CONFIG}}
        self.target["requiredCriteria"] = [{"workItemId": "ITEM", "criterionId": key} for key in ("IOS", "BACKEND")]
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])
        self.target["components"]["documentation"] = {"revision": 2}
        self.write("targets/TARGET.json", self.target)
        self.assertEqual("ready", self.project().gate("TARGET")["state"])
        self.target["components"]["backend"]["backendArtifactDigest"] = ARTIFACT
        self.write("targets/TARGET.json", self.target)
        gate = self.project().gate("TARGET")
        self.assertEqual("stale", gate["state"])
        states = {value["criterionId"]: value["state"] for value in gate["details"]}
        self.assertEqual({"IOS": "passed", "BACKEND": "stale"}, states)

    def test_generic_component_identity_collisions_require_explicit_names(self):
        self.target["components"] = {"ios": {"artifactDigest": ARTIFACT}, "backend": {"artifactDigest": CONFIG}}
        self.write("targets/TARGET.json", self.target)
        result, _, error = self.invoke("validate")
        self.assertEqual(2, result)
        self.assertIn("conflicting identity key artifactDigest", error)

    def test_configuration_digest_alone_is_not_software_or_implementation_bytes(self):
        self.write("evidence/PASS.json", receipt(identities={"configDigest": CONFIG}))
        self.assertEqual(2, self.invoke("validate")[0])
        (self.directory / "evidence/PASS.json").unlink()
        self.item["implementedSubject"] = {"configDigest": CONFIG}
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual(2, self.invoke("validate")[0])

    def test_allof_anyof_and_partial_are_explicit(self):
        self.passed()
        self.item["acceptance"][0]["requiredEvidenceKinds"] = ["unit", "integration"]
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("partial", self.project().verification(self.item))
        self.item["acceptance"][0]["rule"] = "anyOf"
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual("passed", self.project().verification(self.item))

    def test_supersedes_cycles_and_claim_mismatch_are_rejected(self):
        self.passed()
        first = receipt()
        second = receipt("SECOND")
        first["supersedes"] = ["SECOND"]
        second["supersedes"] = ["PASS"]
        self.write("evidence/PASS.json", first)
        self.write("evidence/SECOND.json", second)
        self.assertEqual(2, self.invoke("validate")[0])
        first.pop("supersedes")
        second["claims"][0]["criterionRevision"] = 2
        self.write("evidence/PASS.json", first)
        self.write("evidence/SECOND.json", second)
        self.assertEqual(2, self.invoke("validate")[0])

    def test_snapshot_check_is_readonly_and_atomic_write_failure_preserves_bytes(self):
        path = self.directory / "STATUS.md"
        path.write_bytes(b"Prior snapshot\n")
        self.assertEqual(1, self.invoke("render", "--check")[0])
        self.assertEqual(b"Prior snapshot\n", path.read_bytes())
        with patch("os.replace", side_effect=OSError("simulated interrupted replacement")):
            self.assertEqual(2, self.invoke("render")[0])
        self.assertEqual(b"Prior snapshot\n", path.read_bytes())
        self.assertEqual([], list(self.directory.glob(".STATUS-*")))

    def test_unicode_migration_anchors_and_missing_coverage(self):
        _, _, migration = self.migration()
        (self.root / "docs/czech.md").write_text("# Příští měření\n\n# Příští měření\n", encoding="utf-8")
        migration["links"].append({"source": "docs/original.md", "href": "czech.md#příští-měření-1"})
        self.write("migration.json", migration)
        self.assertEqual(0, self.invoke("migration-check")[0])
        migration["links"][-1]["href"] = "czech.md#missing"
        self.write("migration.json", migration)
        self.assertEqual(2, self.invoke("migration-check")[0])
        migration["links"].pop()
        migration["mappings"][0]["lineEnd"] = 1
        self.write("migration.json", migration)
        result, _, error = self.invoke("migration-check")
        self.assertEqual(2, result)
        self.assertIn("unmapped nonempty source lines", error)

    def test_migration_destinations_are_repository_relative(self):
        _, _, migration = self.migration()
        migration["mappings"][0]["destinations"] = ["https://github.com/fixture/external"]
        self.write("migration.json", migration)
        self.assertEqual(2, self.invoke("migration-check")[0])

    def test_exception_preserves_failure_and_expiration_uses_explicit_time(self):
        self.item["blockers"] = [blocker()]
        self.write("work-items/ITEM.json", self.item)
        self.target["exceptions"] = [{"targetId": "TARGET", "workItemId": "ITEM", "criterionId": "C1",
                                      "reason": "Fixture owner explicitly accepts limitation.",
                                      "decisionRef": "Fixture recorded owner decision", "expiresAtUtc": "2026-10-14T00:00:00Z"}]
        self.write("targets/TARGET.json", self.target)
        project = self.project()
        self.assertEqual("failed", project.gate("TARGET")["state"])
        gate = project.gate("TARGET", tracking.timestamp("2026-10-13T00:00:00Z", "fixture"))
        self.assertEqual("ready_with_exceptions", gate["state"])
        self.assertEqual("blocked", gate["details"][0]["state"])
        self.assertEqual("failed", project.gate("TARGET", tracking.timestamp("2026-10-14T00:00:00Z", "fixture"))["state"])

    def test_duplicate_json_keys_and_nonfinite_values_rejected(self):
        path = self.directory / "work-items/ITEM.json"
        path.write_text('{"schemaVersion":1,"schemaVersion":1}', encoding="utf-8")
        self.assertEqual(2, self.invoke("validate")[0])
        path.write_text('{"schemaVersion":1,"sequence":NaN}', encoding="utf-8")
        self.assertEqual(2, self.invoke("validate")[0])

    def baseline_git(self, originals):
        def command(arguments, **_):
            if "rev-parse" in arguments:
                output = b"f" * 40 + b"\n"
            elif "ls-tree" in arguments:
                output = ("\0".join(sorted(originals)) + "\0").encode("utf-8")
            else:
                output = originals[arguments[-1].split(":", 1)[1]]
            return SimpleNamespace(returncode=0, stdout=output, stderr=b"")
        return command

    def test_baseline_receipt_immutability_and_used_criterion_revision(self):
        self.passed()
        originals = {path.relative_to(self.root).as_posix(): path.read_bytes()
                     for path in self.directory.glob("*/*.json") if "targets" not in str(path)}
        with patch("subprocess.run", side_effect=self.baseline_git(originals)):
            self.assertEqual(0, self.invoke("validate", "--base", "HEAD")[0])
            self.item["acceptance"][0]["expected"] = "Changed fixture semantics."
            self.write("work-items/ITEM.json", self.item)
            self.assertEqual(2, self.invoke("validate", "--base", "HEAD")[0])
            self.item["acceptance"][0]["revision"] = 2
            self.write("work-items/ITEM.json", self.item)
            self.assertEqual(0, self.invoke("validate", "--base", "HEAD")[0])
            modified = receipt()
            modified["observation"] = "Modified observation under same evidence ID."
            self.write("evidence/PASS.json", modified)
            self.assertEqual(2, self.invoke("validate", "--base", "HEAD")[0])

    def test_baseline_deleted_receipt_and_option_like_revision_rejected(self):
        self.passed()
        originals = {"docs/project/evidence/PASS.json": (self.directory / "evidence/PASS.json").read_bytes()}
        self.item["acceptance"][0]["evidenceRefs"] = []
        self.write("work-items/ITEM.json", self.item)
        (self.directory / "evidence/PASS.json").unlink()
        with patch("subprocess.run", side_effect=self.baseline_git(originals)):
            self.assertEqual(2, self.invoke("validate", "--base", "HEAD")[0])
            self.assertEqual(2, self.invoke("validate", "--base=--help")[0])

    def test_baseline_new_closure_cannot_omit_current_unproven_criterion(self):
        self.passed()
        self.item["acceptance"].append(criterion("C2"))
        self.write("work-items/ITEM.json", self.item)
        originals = {"docs/project/work-items/ITEM.json": (self.directory / "work-items/ITEM.json").read_bytes()}
        self.item["state"] = "closed"
        self.item["closure"] = {"outcome": "accepted", "reason": "Fixture closure.", "subject": SOFTWARE,
                                "evidenceRefs": ["PASS"], "acceptedCriteria": [deepcopy(self.item["acceptance"][0])]}
        self.write("work-items/ITEM.json", self.item)
        self.assertEqual(0, self.invoke("validate")[0])
        with patch("subprocess.run", side_effect=self.baseline_git(originals)):
            self.assertEqual(2, self.invoke("validate", "--base", "HEAD")[0])

    def test_baseline_historical_closure_is_immutable_but_current_criteria_can_change(self):
        self.close()
        originals = {"docs/project/work-items/ITEM.json": (self.directory / "work-items/ITEM.json").read_bytes()}
        self.item["acceptance"].append(criterion("C2"))
        self.write("work-items/ITEM.json", self.item)
        with patch("subprocess.run", side_effect=self.baseline_git(originals)):
            self.assertEqual(0, self.invoke("validate", "--base", "HEAD")[0])
            self.item["closure"]["reason"] = "Rewritten historical fixture closure."
            self.write("work-items/ITEM.json", self.item)
            self.assertEqual(2, self.invoke("validate", "--base", "HEAD")[0])


if __name__ == "__main__":
    unittest.main()
