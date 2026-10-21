"""The fast path must retain external gates and failed runs cannot bless outputs."""
from contextlib import ExitStack
import io
import json
from pathlib import Path
import tempfile
import subprocess
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import regenerate


class RegenerationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.out = self.root / "exports"
        self.out.mkdir()
        (self.out / "slicer-validation.json").write_text('{"status":"PASS_SLICER"}', encoding="utf-8")
        self.identity = {"sources": {"input.py": "a" * 64},
                         "environment": {"python": "3.11", "slicer": {"status": "AVAILABLE"}}}
        self.events = []

    def environment(self, *, hit=False, failure=None, identities=None):
        stack = ExitStack()
        self.addCleanup(stack.close)
        stack.enter_context(patch.object(regenerate, "ROOT", self.root))
        stack.enter_context(patch.object(regenerate, "prepared_identity",
                                        side_effect=identities or [self.identity] * 3))
        def run_command(args, **kwargs):
            self.assertTrue(kwargs.get("check"))
            self.events.append(args)
            if args[0] != "node":
                self.assertEqual(args[1:3], ["-X", "utf8"])
            if args[0] != "node" and Path(args[3]).name in ("regenerate.py", "artifacts.py", "verify.py", "package.py"):
                self.assertEqual(kwargs.get("cwd"), self.root)
            if len(args) > 2 and args[-1] == "--python-checks":
                self.assertFalse((self.out / "regeneration-cache.json").exists())
                if failure:
                    raise failure
        stack.enter_context(patch.object(regenerate.subprocess, "run", side_effect=run_command))
        stack.enter_context(patch("regeneration_cache.check_cache", return_value=hit))
        def write_cache(*args):
            self.events.append("cache")
            (self.out / "regeneration-cache.json").write_text("valid", encoding="utf-8")
        stack.enter_context(patch("regeneration_cache.write_cache", side_effect=write_cache))
        stack.enter_context(patch("sys.stdout", new_callable=io.StringIO))

    def test_hit_retains_header_and_javascript_gates_without_rebuilding(self):
        self.environment(hit=True)
        regenerate.regenerate()
        self.assertEqual(len(self.events), 5)
        self.assert_external_gates()
        self.assertFalse((self.out / ".regeneration-lock").exists())

    def test_validation_only_reuse_keeps_fresh_checks_and_force_restores_generation(self):
        for force in (False, True):
            with self.subTest(force=force):
                self.events.clear()
                self.environment(hit=False)
                regenerate.regenerate(force=force, generation_ready=True)
                scripts = [Path(command[3]).name for command in self.events[5:-1]]
                expected = ["regenerate.py", "verify.py", "package.py"]
                if force:
                    expected.insert(1, "artifacts.py")
                self.assertEqual(scripts, expected)
                self.assertEqual(self.events[-1], "cache")
                for command in self.events[5:-1]:
                    self.assertIsInstance(command, list)

    def assert_external_gates(self):
        self.assertIn("generate_effects.py", self.events[0][3])
        self.assertEqual(self.events[0][1:3], ["-X", "utf8"])
        self.assertEqual(self.events[0][-1], "--check")
        for index, flag, filename in (
            (1, "--check", "viewer.js"), (2, "--test", "test_viewer.cjs"),
            (3, "--check", "show_timeline.js"), (4, "--test", "test_show_timeline.cjs"),
        ):
            self.assertEqual(self.events[index], ["node", flag, str(self.root / filename)])

    def test_miss_and_force_run_all_gates_before_cache_publication(self):
        for hit, force in [(False, False), (True, True)]:
            with self.subTest(hit=hit, force=force):
                self.events.clear()
                (self.out / "regeneration-cache.json").write_text("old", encoding="utf-8")
                self.environment(hit=hit)
                regenerate.regenerate(force)
                self.assert_external_gates()
                self.assertEqual([Path(command[3]).name for command in self.events[5:9]],
                                 ["regenerate.py", "artifacts.py", "verify.py", "package.py"])
                self.assertTrue(all(command[1:3] == ["-X", "utf8"] for command in self.events[5:9]))
                self.assertEqual(self.events[5][-1], "--python-checks")
                self.assertEqual(self.events[6][-1], "--generate")
                self.assertEqual(self.events[9], "cache")
                self.assertEqual(len(self.events), 10)

    def test_timeline_gate_failures_prevent_cache_reuse(self):
        for filename in ("show_timeline.js", "test_show_timeline.cjs"):
            with self.subTest(filename=filename):
                self.events.clear()
                self.environment(hit=True)
                def run_command(args, **kwargs):
                    self.assertTrue(kwargs.get("check"))
                    self.events.append(args)
                    if Path(args[-1]).name == filename:
                        raise subprocess.CalledProcessError(1, args)
                with patch.object(regenerate.subprocess, "run", side_effect=run_command), \
                        patch("regeneration_cache.check_cache") as cache_lookup:
                    with self.assertRaises(subprocess.CalledProcessError):
                        regenerate.regenerate()
                    cache_lookup.assert_not_called()
                self.assertNotIn("cache", self.events)
                self.assertFalse((self.out / ".regeneration-lock").exists())

    def test_failed_checks_remove_old_stamp_and_release_lock(self):
        (self.out / "regeneration-cache.json").write_text("old", encoding="utf-8")
        self.environment(failure=RuntimeError("test failure"))
        with self.assertRaisesRegex(RuntimeError, "test failure"):
            regenerate.regenerate()
        self.assertFalse((self.out / "regeneration-cache.json").exists())
        self.assertFalse((self.out / ".regeneration-lock").exists())
        self.assertEqual(len(self.events), 6)
        self.assertEqual(self.events[-1][-1], "--python-checks")

    def test_failed_pristine_verification_cannot_publish_cache(self):
        self.environment()
        def run_command(args, **kwargs):
            self.assertTrue(kwargs.get("check"))
            self.events.append(args)
            if args[0] != "node" and Path(args[3]).name == "verify.py":
                self.assertEqual(kwargs.get("cwd"), self.root)
                raise subprocess.CalledProcessError(1, args)
        with patch.object(regenerate.subprocess, "run", side_effect=run_command):
            with self.assertRaises(subprocess.CalledProcessError):
                regenerate.regenerate()
        self.assertNotIn("cache", self.events)
        self.assertFalse((self.out / "regeneration-cache.json").exists())
        self.assertFalse((self.out / ".regeneration-lock").exists())

    def test_uncached_analytical_run_keeps_all_cold_gates_without_publishing(self):
        for availability in ("UNAVAILABLE", "AVAILABLE"):
            with self.subTest(availability=availability):
                self.events.clear()
                self.identity["environment"]["slicer"]["status"] = availability
                (self.out / "slicer-validation.json").write_text(
                    json.dumps({"status": "SKIP_SLICER_UNAVAILABLE"}), encoding="utf-8")
                (self.out / "regeneration-cache.json").write_text("old", encoding="utf-8")
                self.environment()
                regenerate.regenerate()
                self.assertEqual([Path(command[3]).name for command in self.events[5:]],
                                 ["regenerate.py", "artifacts.py", "verify.py", "package.py"])
                self.assertNotIn("cache", self.events)
                self.assertFalse((self.out / "regeneration-cache.json").exists())
                self.assertFalse((self.out / ".regeneration-lock").exists())

    def test_sources_changing_during_hit_or_cold_run_are_rejected(self):
        changed = {**self.identity, "sources": {"input.py": "b" * 64}}
        for hit in [False, True]:
            with self.subTest(hit=hit):
                self.environment(hit=hit, identities=[self.identity, changed])
                with self.assertRaisesRegex(RuntimeError, "Sources"):
                    regenerate.regenerate()
                self.assertFalse((self.out / "regeneration-cache.json").exists())
                self.assertFalse((self.out / ".regeneration-lock").exists())

    def test_sources_changing_during_cache_publication_remove_stamp(self):
        changed = {**self.identity, "sources": {"input.py": "b" * 64}}
        self.environment(identities=[self.identity, self.identity, changed])
        with self.assertRaisesRegex(RuntimeError, "publishing"):
            regenerate.regenerate()
        self.assertFalse((self.out / "regeneration-cache.json").exists())

    def test_second_regeneration_cannot_use_in_progress_exports(self):
        (self.out / ".regeneration-lock").mkdir()
        with patch.object(regenerate, "ROOT", self.root), \
                self.assertRaisesRegex(RuntimeError, "already running"):
            regenerate.regenerate()
        self.assertTrue((self.out / ".regeneration-lock").is_dir())

    def test_current_support_free_suites_keep_order_and_upstream_gates(self):
        expected = (
            "test_effects", "test_viewer_style", "test_viewer_document",
            "test_model_bundle_export", "test_model_verification", "test_source_inputs",
            "test_packaging", "test_verification_gate", "test_printing_gate",
            "test_printability", "test_slicer_validation", "test_bridge_audit",
            "test_mesh_quality", "test_manufacturing", "test_support_free",
            "test_split_mounts", "test_layout_printing", "test_tray_joint_clearance",
            "test_flex_routing", "test_servo_reference", "test_servo_horn", "test_inspect_data", "test_harness", "test_f2_cad",
            "test_sliding_follower", "test_floor_anchors", "test_follower_load",
            "test_strength", "test_assembly_access", "test_regeneration_cache", "test_regenerate",
            "test_part_cache", "test_refresh_package",
        )
        self.assertEqual(regenerate.TEST_MODULES, expected)

    def test_assembly_validation_runs_only_after_all_python_suites_pass(self):
        events = []
        assembly = SimpleNamespace(validate=lambda: events.append("validate_assembly"))
        def load(name):
            events.append(name)
            return name
        success = SimpleNamespace(wasSuccessful=lambda: True)
        runner = SimpleNamespace(run=lambda suite: success)
        with patch.dict(sys.modules, {"validate_assembly": assembly}), \
                patch.object(regenerate.unittest.defaultTestLoader, "loadTestsFromName", side_effect=load), \
                patch.object(regenerate.unittest, "TextTestRunner", return_value=runner), \
                patch.object(regenerate, "stage", side_effect=lambda name, action: action()):
            regenerate.python_checks()
            self.assertEqual(events, [*regenerate.TEST_MODULES, "validate_assembly"])
            events.clear()
            runner.run = lambda suite: SimpleNamespace(wasSuccessful=lambda: False)
            with self.assertRaises(RuntimeError):
                regenerate.python_checks()
            self.assertEqual(events, [regenerate.TEST_MODULES[0]])

    def test_output_symlinks_cannot_send_regeneration_outside_exports(self):
        destination = self.root / "outside"
        destination.mkdir()
        link = self.out / "stl"
        try:
            link.symlink_to(destination, target_is_directory=True)
        except OSError as error:
            self.skipTest(f"Symlink creation unavailable: {error}")
        with patch.object(regenerate, "ROOT", self.root), \
                self.assertRaisesRegex(RuntimeError, "escapes exports"):
            regenerate.regenerate()
        self.assertEqual(list(destination.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
