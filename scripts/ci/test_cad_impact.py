"""The lightweight selector must fail closed for unreviewed CAD dependencies."""
from pathlib import Path
import json
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from cad_impact import (EXTERNAL_CAD_INPUTS, changed_paths, classify_path,
                        impact_plan, requires_manufacturing)


ROOT = Path(__file__).resolve().parents[2]
FINAL = "mechanika/final/"


class CadImpactTests(unittest.TestCase):
    def test_local_changes_include_index_worktree_deletions_and_only_visible_untracked_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)

            def git(*arguments):
                return subprocess.check_output(["git", "-C", folder, *arguments], stderr=subprocess.DEVNULL).decode().strip()

            git("init")
            git("config", "user.name", "CI test")
            git("config", "user.email", "ci@example.invalid")
            git("config", "core.autocrlf", "false")
            (root / ".gitignore").write_text("*.ignored\n", encoding="utf-8")
            for name in ("deleted.py", "indexed.py", "working.py", "renamed.py"):
                (root / name).write_text("base\n", encoding="utf-8")
            git("add", ".")
            git("commit", "-m", "base")
            base = git("rev-parse", "HEAD")
            (root / "deleted.py").unlink()
            (root / "indexed.py").write_text("index\n", encoding="utf-8")
            git("add", "indexed.py")
            # Its worktree matches base, but the index still changed.
            (root / "indexed.py").write_text("base\n", encoding="utf-8")
            (root / "working.py").write_text("working\n", encoding="utf-8")
            (root / "renamed.py").rename(root / "replacement.py")
            (root / "new.py").write_text("new\n", encoding="utf-8")
            (root / "hidden.ignored").write_text("ignore\n", encoding="utf-8")
            self.assertEqual(changed_paths(base, root=root), [
                "deleted.py", "indexed.py", "new.py", "renamed.py", "replacement.py", "working.py",
            ])
            git("add", "-A")
            git("commit", "-m", "changes")
            head = git("rev-parse", "HEAD")
            (root / "later.py").write_text("not in commit\n", encoding="utf-8")
            self.assertEqual(changed_paths(base, head, root=root), [
                "deleted.py", "new.py", "renamed.py", "replacement.py", "working.py",
            ])

    def test_plan_names_dependency_owners_but_unknown_and_deleted_inputs_require_full_scope(self):
        graph = {"chassis": {"chassis.py": "hash", "mechanism.py": "hash"},
                 "mechanism": {"mechanism.py": "hash"}, "layout": {"layout.py": "hash"}}
        with patch("cad_impact.owner_dependencies", return_value=graph):
            plan = impact_plan([FINAL + "mechanism.py", FINAL + "viewer.js", "README.md"])
            self.assertTrue(plan["manufacturing_required"])
            self.assertEqual(plan["owners"], ["chassis", "mechanism"])
            self.assertEqual(plan["causes"]["viewer"], [FINAL + "viewer.js"])
            self.assertEqual(plan["skipped_stages"], [])
            self.assertEqual(plan["unrelated_paths"], 1)
            for path in [FINAL + "unknown.bin", FINAL + "deleted_input.py"]:
                with self.subTest(path=path):
                    self.assertEqual(impact_plan([path])["owners"], ["shared/full"])
        self.assertIn("mechanical verification", impact_plan([FINAL + "viewer.js"])["skipped_stages"])

    def test_public_cli_simulates_without_importing_cad_or_consulting_git(self):
        result = subprocess.check_output([
            sys.executable, "-I", str(ROOT / "scripts/ci/cad_impact.py"),
            FINAL + "viewer.js", "backend/Program.cs",
        ], cwd=tempfile.gettempdir(), text=True)
        plan = json.loads(result)
        self.assertEqual(plan["mode"], "paths")
        self.assertFalse(plan["manufacturing_required"])
        self.assertIn("slicer", plan["skipped_stages"])

    def test_authoritative_external_documents_remain_cad_inputs(self):
        for path in EXTERNAL_CAD_INPUTS:
            with self.subTest(path=path):
                self.assertEqual(classify_path(path), "manufacturing")

    def test_unknown_final_sources_deletions_and_nested_inputs_are_conservative(self):
        for name in ("new_geometry.py", "future-input.bin", "parameters.json",
                     "reference/new-dimensions.json", "new-tests/test_geometry.py",
                     "SPECIFIKACE.md", "deleted-input.csv", "source_inputs.py",
                     "effects.json", "effects.plan.json", "generate_effects.py",
                     "twin_manifest.py"):
            with self.subTest(name=name):
                self.assertEqual(classify_path(FINAL + name), "manufacturing")

    def test_unrelated_projects_and_generic_documentation_do_not_need_cad(self):
        paths = ["README.md", "docs/technical-note.md", "docs/dv01.md",
                 "backend/Program.cs", "firmware/src/production/main.cpp",
                 "mobilni-aplikace/src/styles.css", "web/build-site.py",
                 "design-system/tokens/colors.css", FINAL + "viewer.js", FINAL + "refresh_viewer.py"]
        self.assertFalse(requires_manufacturing(paths))
        self.assertTrue(requires_manufacturing([*paths, FINAL + "mechanism.py"]))

    def test_shared_executable_ui_runtime_and_helpers_are_presentation_only(self):
        for name in ("viewer_runtime.py", "viewer_ui.js", "workshop-ui-runtime.js",
                     "ui_test_runtime.cjs", "preview_ui_fixture.py", "refresh_viewer.py"):
            with self.subTest(name=name):
                self.assertEqual(classify_path(FINAL + name), "viewer")
        self.assertEqual(classify_path("design-system/ui.js"), "viewer")
        self.assertEqual(classify_path(FINAL + "future_runtime_dependency.py"), "manufacturing")

    def test_required_software_ci_has_no_path_filter(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        triggers = ci.split("permissions:", 1)[0]
        self.assertNotIn("paths:", triggers)
        self.assertIn("merge_group:", triggers)
        self.assertIn("github.event.merge_group.base_sha", ci)
        self.assertIn("github.event.merge_group.head_sha", ci)
        required = ci.split("  required:\n", 1)[1]
        self.assertIn('name: CI required', required)
        self.assertIn("    if: always()", required)



if __name__ == "__main__":
    unittest.main()
