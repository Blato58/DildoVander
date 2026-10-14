"""The fast path must retain external gates and failed runs cannot bless outputs."""
from contextlib import ExitStack
import io
from pathlib import Path
import tempfile
import subprocess
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
        self.identity = {"sources": {"input.py": "a" * 64}, "environment": {"python": "3.11"}}
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
            if Path(args[1]).name in ("regenerate.py", "artifacts.py", "verify.py", "package.py"):
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

    def assert_external_gates(self):
        self.assertIn("generate_effects.py", self.events[0][1])
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
                self.assertEqual([Path(command[1]).name for command in self.events[5:9]],
                                 ["regenerate.py", "artifacts.py", "verify.py", "package.py"])
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
            if Path(args[1]).name == "verify.py":
                self.assertEqual(kwargs.get("cwd"), self.root)
                raise subprocess.CalledProcessError(1, args)
        with patch.object(regenerate.subprocess, "run", side_effect=run_command):
            with self.assertRaises(subprocess.CalledProcessError):
                regenerate.regenerate()
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
