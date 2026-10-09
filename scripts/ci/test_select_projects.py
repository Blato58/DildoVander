import copy
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from select_projects import changed_paths, check_results, PROJECTS, select_projects


class ProjectSelectionTests(unittest.TestCase):
    def selected(self, *paths):
        return {name for name, enabled in select_projects(paths).items() if enabled}

    def test_documentation_never_selects_projects(self):
        self.assertEqual(self.selected(
            "README.md", "AGENTS.md", "docs/diagram.png", "kufr/docs/piny.md",
            "kufr/firmware/README.md", "kufr/backend/docs/scheme.json",
            "kufr/mobilni-aplikace/README.MD", "scripts/ci/README.rst",
            ".github/workflows/docs/example.yml",
        ), set())

    def test_each_project_and_real_build_inputs(self):
        for paths, expected in [
            (["kufr/firmware/src/main.cpp", "docs/a.md"], {"firmware"}),
            (["kufr/firmware/src/CMakeLists.txt"], {"firmware"}),
            (["kufr/backend-tests/Program.cs"], {"backend"}),
            (["kufr/global.json", "kufr/Directory.Build.props"], {"backend"}),
            (["kufr/mobilni-aplikace/public/icon.svg"], {"web"}),
            (["kufr/ai-eval/cases.json"], {"ai-eval"}),
            (["kufr/firmware/src/main.cpp", "kufr/backend/Program.cs"], {"firmware", "backend"}),
            (["kufr/mechanika/model.py", "obludarium/docs/a.md"], set()),
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(self.selected(*paths), expected)

    def test_shared_dependencies_select_their_consumers(self):
        self.assertEqual(self.selected("kufr/contracts/control-message-v1.schema.json"),
                         {"backend", "firmware", "web"})
        self.assertEqual(self.selected("kufr/simulator/bridge.cpp"), {"firmware", "web"})
        self.assertEqual(self.selected("scripts/ci/package.py"), {"repository", "backend", "firmware", "web"})

    def test_workflow_changes_select_only_the_corresponding_build(self):
        self.assertEqual(self.selected(".github/workflows/kufr-firmware.yml"), {"repository", "firmware"})
        self.assertEqual(self.selected(".github/workflows/ci.yml"), {"repository"})
        self.assertEqual(self.selected("scripts/ci/select_projects.py"), {"repository"})

    def test_git_diff_includes_both_sides_of_a_rename(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.name", "CI test")
            git("config", "user.email", "ci@example.invalid")
            source = Path(folder) / "kufr/firmware/source.cpp"
            source.parent.mkdir(parents=True)
            source.write_text("source\n")
            git("add", ".")
            git("commit", "-m", "base")
            base = git("rev-parse", "HEAD")
            target = Path(folder) / "kufr/mobilni-aplikace/source.cpp"
            target.parent.mkdir(parents=True)
            source.rename(target)
            git("add", "-A")
            git("commit", "-m", "move")
            head = git("rev-parse", "HEAD")
            original = subprocess.check_output
            with patch("select_projects.subprocess.check_output", side_effect=lambda args: original(args, cwd=folder)):
                self.assertEqual(set(changed_paths(base, head)),
                                 {"kufr/firmware/source.cpp", "kufr/mobilni-aplikace/source.cpp"})

    def test_quality_gate_accepts_only_successful_selected_jobs(self):
        selected = {name: "true" if name == "firmware" else "false" for name in PROJECTS}
        jobs = {name: {"result": "success" if name == "firmware" else "skipped"} for name in PROJECTS}
        jobs["changes"] = {"result": "success"}
        check_results(selected, jobs)
        for name, result in (("firmware", "skipped"), ("firmware", "failure"),
                             ("firmware", "cancelled"), ("changes", "failure"), ("web", "success")):
            failed = copy.deepcopy(jobs)
            failed[name]["result"] = result
            with self.subTest(name=name, result=result), self.assertRaises(ValueError):
                check_results(selected, failed)
        with self.assertRaises(ValueError):
            check_results({}, jobs)


if __name__ == "__main__":
    unittest.main()
