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
FINAL = "kufr/mechanika/final/"


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
            FINAL + "viewer.js", "kufr/backend/Program.cs",
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
        paths = ["README.md", "docs/project/STATUS.md", "kufr/docs/dv01.md",
                 "kufr/backend/Program.cs", "kufr/firmware/src/main.cpp",
                 "kufr/mobilni-aplikace/src/styles.css", "kufr/web/build-site.py",
                 "kufr/design-system/tokens/colors.css", FINAL + "viewer.js"]
        self.assertFalse(requires_manufacturing(paths))
        self.assertTrue(requires_manufacturing([*paths, FINAL + "mechanism.py"]))

    def test_main_warming_uses_policy_before_install_and_required_ci_has_no_path_filter(self):
        workflow = (ROOT / ".github/workflows/kufr-cad.yml").read_text()
        self.assertNotIn("    paths:", workflow)
        self.assertLess(workflow.index("scripts/ci/select_projects.py"), workflow.index("uses: actions/setup-node"))
        self.assertEqual(workflow.count("if: steps.impact.outputs.cad == 'true'"), 2)
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        triggers = ci.split("permissions:", 1)[0]
        self.assertNotIn("paths:", triggers)
        self.assertIn("merge_group:", triggers)
        self.assertIn("github.event.merge_group.base_sha", ci)
        self.assertIn("github.event.merge_group.head_sha", ci)
        self.assertIn("name: CI required\n    if: always()", ci)

    def test_force_dispatch_is_explicit_and_propagates_to_both_verification_paths(self):
        ci = (ROOT / ".github/workflows/ci.yml").read_text()
        cad = (ROOT / ".github/workflows/kufr-cad.yml").read_text()
        site = (ROOT / ".github/workflows/kufr-site.yml").read_text()
        self.assertIn("--force-cad", ci)
        # Missing dispatch inputs on PRs evaluate to an empty string. A typed
        # reusable boolean must compare explicitly instead of forwarding it.
        self.assertIn("force-cad: ${{ inputs.force_cad == true && inputs.pr_number == '' }}", ci)
        self.assertIn("force: ${{ inputs.force_cad == true && inputs.pr_number == '' }}", ci)
        self.assertIn("force_cad:", ci)
        self.assertIn("force-cad: ${{ inputs.force_cad", ci)
        self.assertIn("force: ${{ inputs.force_cad", ci)
        self.assertIn("force: ${{ inputs.force || false }}", cad)
        self.assertIn("force: ${{ inputs.force-cad }}", site)


if __name__ == "__main__":
    unittest.main()
