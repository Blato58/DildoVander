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
            (["kufr/firmware/src/production/main.cpp", "docs/a.md"], {"firmware"}),
            (["kufr/firmware/src/CMakeLists.txt"], {"firmware"}),
            (["kufr/backend-tests/Program.cs"], {"backend"}),
            (["kufr/global.json", "kufr/Directory.Build.props"], {"backend"}),
            (["NuGet.Config"], {"backend"}),
            (["kufr/mobilni-aplikace/public/icon.svg"], {"web", "ios", "site"}),
            (["kufr/mobilni-aplikace/ios/App/App/KufrDebugPlugin.swift"], {"web", "ios", "site"}),
            (["kufr/mobilni-aplikace/tests/native_debug/main.swift"], {"web", "ios", "site"}),
            (["kufr/ai-eval/cases.json"], {"ai-eval"}),
            (["kufr/firmware/src/production/main.cpp", "kufr/backend/Program.cs"], {"firmware", "backend"}),
            (["kufr/mechanika/final/mechanism.py", "obludarium/docs/a.md"], {"repository"}),
            (["kufr/design-system/tokens/colors.css"], {"repository", "web", "ios", "site"}),
            (["kufr/web/build-site.py"], {"site"}),
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(self.selected(*paths), expected)

    def test_shared_dependencies_select_their_consumers(self):
        self.assertEqual(self.selected("kufr/contracts/control-message-v1.schema.json"),
                         {"backend", "firmware", "web", "ios", "site"})
        self.assertEqual(self.selected("kufr/simulator/bridge.cpp"), {"firmware", "web", "ios"})
        self.assertEqual(self.selected("scripts/ci/package.py"), {"repository", "backend", "firmware", "web", "site"})

    def test_integrated_app_dependency_paths_select_real_consumers_without_cad(self):
        for name in ("controller.mjs", "serial.mjs", "profiles.mjs", "auto-calibration.mjs", "mcp.mjs"):
            self.assertEqual(self.selected("kufr/firmware/diagnostics/servo-bench/web/" + name), {"web", "ios", "site"})
        for name in ("generate-control-validator.mjs", "control-validator-core.mjs", "generated/control-message-v1.validator.mjs"):
            self.assertEqual(self.selected("kufr/contracts/" + name), {"backend", "firmware", "web", "ios", "site"})
        self.assertEqual(self.selected("kufr/backend/Features/ModelReleases/ModelReleaseStore.cs"), {"backend"})
        self.assertEqual(self.selected("kufr/mobilni-aplikace/src/features/identity/ModelReleasesPanel.vue"), {"backend", "web", "ios", "site"})

    def test_account_ui_runs_real_https_account_journey_without_cad(self):
        for path in ("src/features/identity/SignInView.vue", "tests/accounts.browser.mjs",
                     "tests/accounts.fixture.browser.mjs"):
            self.assertEqual(self.selected("kufr/mobilni-aplikace/" + path), {"backend", "web", "ios", "site"})
        self.assertEqual(self.selected("kufr/mobilni-aplikace/src/application/HomeView.vue"), {"web", "ios", "site"})

    def test_workflow_changes_select_only_the_corresponding_build(self):
        self.assertEqual(self.selected(".github/workflows/kufr-firmware.yml"), {"repository", "firmware"})
        self.assertEqual(self.selected(".github/workflows/ci.yml"), {"repository"})
        self.assertEqual(self.selected("scripts/ci/select_projects.py"), {"repository"})
        self.assertEqual(self.selected(".github/workflows/kufr-site.yml"), {"repository", "site"})
        self.assertEqual(self.selected(".github/workflows/kufr-cad-generate.yml"), {"repository"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios.yml"), {"repository", "ios"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios-sign.yml"), {"repository", "ios"})

    def test_manufacturing_inputs_do_not_select_cad(self):
        for path in ["kufr/mechanika/final/SPECIFIKACE.md",
                     "kufr/mechanika/final/reference/inspect-sources.json",
                     "kufr/docs/zapojeni-potvrzeneho-hardwaru.md",
                     "kufr/mechanika/rozmery-nakoupenych-soucastek.md",
                     "scripts/ci/install-cad-slicer.sh",
                     "scripts/cad.py", "scripts/cad.ps1"]:
            with self.subTest(path=path):
                self.assertNotIn("cad", self.selected(path))
        for path in ["kufr/mobilni-aplikace/src/main.ts", "kufr/mobilni-aplikace/.nvmrc", "kufr/web/build-site.py",
                     "kufr/contracts/package-lock.json", "kufr/backend/Program.cs",
                     "kufr/design-system/tokens.json", "kufr/design-system/ui.js",
                     "kufr/mechanika/final/viewer.js", "kufr/mechanika/final/viewer_document.py",
                     "kufr/mechanika/final/viewer-workshop.css"]:
            with self.subTest(path=path):
                self.assertNotIn("cad", self.selected(path))

    def test_ui_inputs_keep_real_consumers_and_unknown_mechanics_conservative(self):
        self.assertEqual(self.selected("kufr/design-system/tokens.json"),
                         {"repository", "backend", "web", "ios", "site"})
        self.assertEqual(self.selected("kufr/mechanika/final/viewer.js"), {"site"})
        self.assertEqual(self.selected("kufr/mechanika/final/new-mechanism.py"), {"repository"})
        self.assertEqual(self.selected("kufr/simulator/dashboard-ui.js"), {"site"})
        self.assertEqual(self.selected("kufr/firmware/diagnostics/servo-bench/web/panel.css"), {"site"})
        self.assertEqual(self.selected("kufr/firmware/src/diagnostics/servo-bench/main.cpp"), {"firmware"})

    def test_presentation_policy_matches_the_pr_selector(self):
        from cad_impact import SOURCE_POLICY
        for name in SOURCE_POLICY.PRESENTATION_SOURCES:
            with self.subTest(name=name):
                self.assertEqual(self.selected("kufr/mechanika/final/" + name), {"site"})

    def test_recovery_builds_exclude_cad(self):
        self.assertNotIn("cad", PROJECTS)
        self.assertTrue(all(select_recovery_projects("all").values()))
        self.assertEqual({name for name, enabled in select_recovery_projects("site").items() if enabled}, {"site"})
        with self.assertRaises(ValueError):
            select_recovery_projects("cad")
        for args in (("--project", "cad"), ("--project", "all", "--force-cad")):
            result = subprocess.run(["python", str(Path(__file__).with_name("select_projects.py")), "select", *args], capture_output=True)
            self.assertNotEqual(result.returncode, 0)

    def test_presentation_inputs_select_lightweight_site_checks(self):
        for path in ["viewer.js", "viewer_style.py", "viewer_document.py",
                     "viewer-template.html", "twin_media.css", "test_viewer_document.py",
                     "viewer_ui.js", "viewer_runtime.py", "workshop-ui-runtime.js",
                     "ui_test_runtime.cjs", "preview_ui_fixture.py", "refresh_viewer.py",
                     "README.md", "VIEWER.md"]:
            with self.subTest(path=path):
                self.assertEqual(self.selected("kufr/mechanika/final/" + path), {"site"})

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

    def test_required_gate_has_no_cad_dependency(self):
        selected = {name: "false" for name in PROJECTS}
        jobs = {name: {"result": "skipped"} for name in PROJECTS}
        jobs["changes"] = {"result": "success"}
        check_results(selected, jobs)



if __name__ == "__main__":
    unittest.main()
