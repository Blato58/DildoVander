import copy
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from select_projects import changed_paths, check_results, PROJECTS, select_projects, select_recovery_projects


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
            (["kufr/firmware/src/espidf_main.cpp", "docs/a.md"], {"firmware"}),
            (["kufr/firmware/src/CMakeLists.txt"], {"firmware"}),
            (["kufr/backend-tests/Program.cs"], {"backend"}),
            (["kufr/global.json", "kufr/Directory.Build.props"], {"backend"}),
            (["NuGet.Config"], {"backend"}),
            (["kufr/mobilni-aplikace/public/icon.svg"], {"web", "ios", "site"}),
            (["kufr/mobilni-aplikace/ios/App/App/KufrDebugPlugin.swift"], {"web", "ios", "site"}),
            (["kufr/mobilni-aplikace/tests/native_debug/main.swift"], {"web", "ios", "site"}),
            (["kufr/ai-eval/cases.json"], {"ai-eval"}),
            (["kufr/firmware/src/espidf_main.cpp", "kufr/backend/Program.cs"], {"firmware", "backend"}),
            (["kufr/mechanika/final/mechanism.py", "obludarium/docs/a.md"], {"site", "cad"}),
            (["kufr/design-system/tokens/colors.css"], {"web", "ios", "site", "cad"}),
            (["kufr/web/index.html"], {"site"}),
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(self.selected(*paths), expected)

    def test_shared_dependencies_select_their_consumers(self):
        self.assertEqual(self.selected("kufr/contracts/control-message-v1.schema.json"),
                         {"backend", "firmware", "web", "ios", "site"})
        self.assertEqual(self.selected("kufr/simulator/bridge.cpp"), {"firmware", "web", "ios"})
        self.assertEqual(self.selected("scripts/ci/package.py"), {"repository", "backend", "firmware", "web", "site"})

    def test_workflow_changes_select_only_the_corresponding_build(self):
        self.assertEqual(self.selected(".github/workflows/kufr-firmware.yml"), {"repository", "firmware"})
        self.assertEqual(self.selected(".github/workflows/ci.yml"), {"repository"})
        self.assertEqual(self.selected("scripts/ci/select_projects.py"), {"repository"})
        self.assertEqual(self.selected(".github/workflows/kufr-site.yml"), {"repository", "site"})
        self.assertEqual(self.selected(".github/workflows/kufr-cad.yml"), {"repository", "cad"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios.yml"), {"repository", "ios"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios-sign.yml"), {"repository", "ios"})

    def test_cad_runs_only_for_manufacturing_inputs(self):
        for path in ["kufr/mechanika/final/SPECIFIKACE.md",
                     "kufr/mechanika/final/reference/inspect-sources.json",
                     "kufr/docs/zapojeni-potvrzeneho-hardwaru.md",
                     "kufr/mechanika/rozmery-nakoupenych-soucastek.md",
                     "scripts/ci/install-cad-slicer.sh",
                     "scripts/ci/cad_cache.py",
                     ".github/actions/verified-cad/action.yml",
                     "kufr/mobilni-aplikace/.nvmrc"]:
            with self.subTest(path=path):
                self.assertIn("cad", self.selected(path))
        for path in ["kufr/mobilni-aplikace/src/main.ts", "kufr/web/index.html",
                     "kufr/contracts/package-lock.json", "kufr/backend/Program.cs"]:
            with self.subTest(path=path):
                self.assertNotIn("cad", self.selected(path))

    def test_recovery_site_covers_cad_without_a_duplicate_job(self):
        self.assertFalse(select_recovery_projects("all")["cad"])
        self.assertTrue(select_recovery_projects("all")["site"])
        self.assertEqual({name for name, enabled in select_recovery_projects("cad").items() if enabled}, {"cad"})

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

    def test_selected_ios_cannot_be_skipped_or_fail_the_required_gate(self):
        selected = {name: "true" if name == "ios" else "false" for name in PROJECTS}
        jobs = {name: {"result": "success" if name == "ios" else "skipped"} for name in PROJECTS}
        jobs["changes"] = {"result": "success"}
        check_results(selected, jobs)
        for result in ("skipped", "failure", "cancelled"):
            with self.subTest(result=result), self.assertRaises(ValueError):
                check_results(selected, {**jobs, "ios": {"result": result}})
        with self.assertRaises(ValueError):
            check_results(selected, {name: value for name, value in jobs.items() if name != "ios"})

    def test_selected_cad_is_a_required_gate(self):
        selected = {name: "true" if name == "cad" else "false" for name in PROJECTS}
        jobs = {name: {"result": "success" if name == "cad" else "skipped"} for name in PROJECTS}
        jobs["changes"] = {"result": "success"}
        check_results(selected, jobs)
        for result in ("skipped", "failure", "cancelled"):
            with self.subTest(result=result), self.assertRaises(ValueError):
                check_results(selected, {**jobs, "cad": {"result": result}})


if __name__ == "__main__":
    unittest.main()
