import copy
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from io import BytesIO
import json

from select_projects import changed_paths, check_results, main, PROJECTS, select_projects, select_recovery_projects


class ProjectSelectionTests(unittest.TestCase):
    def selected(self, *paths):
        return {name for name, enabled in select_projects(paths).items() if enabled}

    def test_documentation_never_selects_projects(self):
        self.assertEqual(self.selected(
            "README.md", "AGENTS.md", "docs/diagram.png", "docs/piny.md",
            "firmware/README.md", "backend/docs/scheme.json",
            "mobilni-aplikace/README.MD", "scripts/ci/README.rst",
            ".github/workflows/docs/example.yml",
        ), set())

    def test_each_project_and_real_build_inputs(self):
        for paths, expected in [
            (["firmware/src/production/main.cpp", "docs/a.md"], {"firmware"}),
            (["firmware/src/CMakeLists.txt"], {"firmware"}),
            (["firmware/CMakeLists.txt", "firmware/platformio.ini"], {"firmware"}),
            (["CMakeLists.txt", "platformio.ini"], {"firmware"}),
            (["backend-tests/Program.cs"], {"backend"}),
            (["global.json", "Directory.Build.props"], {"backend"}),
            (["NuGet.Config"], {"backend"}),
            (["mobilni-aplikace/public/icon.svg"], {"web", "ios", "site"}),
            (["mobilni-aplikace/ios/App/App/KufrDebugPlugin.swift"], {"web", "ios", "site"}),
            (["mobilni-aplikace/tests/native_debug/main.swift"], {"web", "ios", "site"}),
            (["ai-eval/cases.json"], {"ai-eval"}),
            (["firmware/src/production/main.cpp", "backend/Program.cs"], {"firmware", "backend"}),
            (["mechanika/final/mechanism.py", "docs/a.md"], {"repository"}),
            (["design-system/tokens/colors.css"], {"repository", "web", "ios", "site"}),
            (["web/build-site.py"], {"site"}),
        ]:
            with self.subTest(paths=paths):
                self.assertEqual(self.selected(*paths), expected)

    def test_shared_dependencies_select_their_consumers(self):
        self.assertEqual(self.selected("contracts/control-message-v1.schema.json"),
                         {"backend", "firmware", "web", "ios", "site"})
        self.assertEqual(self.selected("firmware/simulator/bridge.cpp"), {"firmware", "web", "ios"})
        self.assertEqual(self.selected("scripts/ci/package.py"), {"repository", "backend", "firmware", "web", "site"})

    def test_nested_simulator_does_not_inherit_the_broad_firmware_filter(self):
        for name in ("dashboard.html", "dashboard.js", "dashboard-ui.js", "verify-dashboard.mjs"):
            with self.subTest(name=name):
                self.assertEqual(self.selected("firmware/simulator/" + name), {"site"})
        self.assertEqual(self.selected("firmware/simulator/native/host_main.cpp"),
                         {"firmware", "web", "ios"})
        self.assertEqual(self.selected("firmware/simulator/dashboard.test.mjs"),
                         {"firmware", "web", "ios", "site"})

    def test_integrated_app_dependency_paths_select_real_consumers_without_cad(self):
        for name in ("controller.mjs", "serial.mjs", "profiles.mjs", "auto-calibration.mjs", "mcp.mjs"):
            self.assertEqual(self.selected("firmware/diagnostics/servo-bench/web/" + name), {"web", "ios", "site"})
        for name in ("generate-control-validator.mjs", "control-validator-core.mjs", "generated/control-message-v1.validator.mjs"):
            self.assertEqual(self.selected("contracts/" + name), {"backend", "firmware", "web", "ios", "site"})
        self.assertEqual(self.selected("backend/Features/ModelReleases/ModelReleaseStore.cs"), {"backend"})
        self.assertEqual(self.selected("mobilni-aplikace/src/features/identity/ModelReleasesPanel.vue"), {"backend", "web", "ios", "site"})

    def test_account_ui_runs_real_https_account_journey_without_cad(self):
        for path in ("src/features/identity/SignInView.vue", "tests/accounts.browser.mjs",
                     "tests/accounts.fixture.browser.mjs"):
            self.assertEqual(self.selected("mobilni-aplikace/" + path), {"backend", "web", "ios", "site"})
        self.assertEqual(self.selected("mobilni-aplikace/src/application/HomeView.vue"), {"web", "ios", "site"})

    def test_ios_impact_remains_conservative_for_shared_and_native_inputs(self):
        for path in (
            "src/application/HomeView.vue", "src/adapters/native-transport.ts",
            "capacitor.config.ts", "package.json", "package-lock.json", ".nvmrc",
            "vite.config.ts", "tsconfig.json", "ios/App/App/Info.plist",
            "ios/App/CapApp-SPM/Package.swift", "scripts/generate-native-assets.mjs",
        ):
            with self.subTest(path=path):
                self.assertTrue(select_projects(["mobilni-aplikace/" + path])["ios"])
        for path in ("contracts/new-contract.schema.json", "design-system/tokens.json",
                     "firmware/simulator/new-adapter.ts"):
            with self.subTest(path=path):
                self.assertTrue(select_projects([path])["ios"])

    def test_unknown_paths_inside_product_roots_keep_their_consumers(self):
        self.assertEqual(self.selected("mobilni-aplikace/new-adapter/config.custom"), {"web", "ios", "site"})
        self.assertEqual(self.selected("backend/new-feature/custom.input"), {"backend"})
        self.assertEqual(self.selected("contracts/new-contract.custom"), {"backend", "firmware", "web", "ios", "site"})

    def test_shared_build_inputs_and_unknown_root_files_never_select_zero_checks(self):
        for path in ("Directory.Build.targets", "Directory.Packages.props"):
            with self.subTest(path=path):
                self.assertEqual(self.selected(path), {"backend"})
        conservative = {"repository", "backend", "firmware", "web", "ios", "site"}
        self.assertEqual(self.selected("new-root-tooling.toml"), conservative)
        self.assertEqual(self.selected("shared-build/config.toml"), conservative)
        self.assertEqual(self.selected("backend/Program.cs", "new-root-tooling.toml"), conservative)
        self.assertEqual(self.selected("new-root-tooling.toml", "backend/Program.cs"), conservative)

    def test_native_stack_membership_uses_github_order_and_exact_candidate(self):
        from select_projects import verify_native_stack
        stacks = [{
            "id": 9876543, "number": 42, "base": {"ref": "main"},
            "pull_requests": [
                {"number": 70, "head": {"ref": "layer-one", "sha": "a" * 40}},
                {"number": 71, "head": {"ref": "layer-two", "sha": "b" * 40}},
            ],
        }]
        response = BytesIO(json.dumps(stacks).encode())
        trunk = BytesIO(json.dumps({"commit": {"sha": "c" * 40}}).encode())
        with patch("select_projects.urllib.request.urlopen", side_effect=[response, trunk]) as open_url:
            result = verify_native_stack("owner/repo", 71, "layer-two", "b" * 40, 42, 2, 2,
                                         "main", "c" * 40, "token", ancestor_check=lambda *_: True)
        self.assertEqual(result["id"], 9876543)
        self.assertRegex(result["candidate_revision"], r"^[0-9a-f]{64}$")
        request = open_url.call_args.args[0]
        self.assertIn("branches/main", request.full_url)
        self.assertEqual(request.get_header("Authorization"), "Bearer token")
        self.assertIn("pull_request=71", open_url.call_args_list[0].args[0].full_url)

    def test_native_stack_rejects_a_lower_head_not_ancestor_of_event_head(self):
        from select_projects import verify_native_stack
        stack = {"id": 9, "number": 42, "base": {"ref": "main"}, "pull_requests": [
            {"number": 70, "head": {"ref": "layer-one", "sha": "a" * 40}},
            {"number": 71, "head": {"ref": "layer-two", "sha": "b" * 40}},
        ]}
        with patch("select_projects.urllib.request.urlopen", side_effect=[
                BytesIO(json.dumps([stack]).encode()),
                BytesIO(json.dumps({"commit": {"sha": "c" * 40}}).encode())]):
            with self.assertRaisesRegex(ValueError, "lower native stack layer"):
                verify_native_stack("owner/repo", 71, "layer-two", "b" * 40, 42, 2, 2,
                                    "main", "c" * 40, "token", ancestor_check=lambda *_: False)

    def test_native_stack_verification_rejects_unregistered_or_stale_candidates(self):
        from select_projects import verify_native_stack
        stack = {"id": 9, "number": 42, "base": {"ref": "main"}, "pull_requests": [
            {"number": 71, "head": {"ref": "layer-two", "sha": "b" * 40}},
        ]}
        cases = [[], [stack, stack], [{**stack, "number": 43}],
                 [{**stack, "pull_requests": [{"number": 72, "head": {"ref": "other", "sha": "b" * 40}}]}]]
        for payload in cases:
            with self.subTest(payload=payload):
                with patch("select_projects.urllib.request.urlopen", side_effect=[BytesIO(json.dumps(payload).encode())]):
                    with self.assertRaises(ValueError):
                        verify_native_stack("owner/repo", 71, "layer-two", "b" * 40, 42, 1, 1,
                                            "main", "c" * 40, "token")

    def test_native_stack_verification_rejects_a_moved_trunk(self):
        from select_projects import verify_native_stack
        stack = {"id": 9, "number": 42, "base": {"ref": "main"}, "pull_requests": [
            {"number": 71, "head": {"ref": "layer-two", "sha": "b" * 40}},
        ]}
        with patch("select_projects.urllib.request.urlopen", side_effect=[
                BytesIO(json.dumps([stack]).encode()),
                BytesIO(json.dumps({"commit": {"sha": "d" * 40}}).encode())]):
            with self.assertRaisesRegex(ValueError, "trunk changed"):
                verify_native_stack("owner/repo", 71, "layer-two", "b" * 40, 42, 1, 1,
                                    "main", "c" * 40, "token")

    def test_workflow_changes_select_only_the_corresponding_build(self):
        self.assertEqual(self.selected(".github/workflows/kufr-firmware.yml"), {"repository", "firmware"})
        self.assertEqual(self.selected(".github/workflows/ci.yml"), {"repository"})
        self.assertEqual(self.selected("scripts/ci/select_projects.py"), {"repository"})
        self.assertEqual(self.selected(".github/workflows/kufr-site.yml"), {"repository", "site"})
        self.assertEqual(self.selected(".github/workflows/kufr-cad-generate.yml"), {"repository"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios.yml"), {"repository", "ios"})
        self.assertEqual(self.selected(".github/workflows/kufr-ios-sign.yml"), {"repository", "ios"})

    def test_manufacturing_inputs_do_not_select_cad(self):
        for path in ["mechanika/final/SPECIFIKACE.md",
                     "mechanika/final/reference/inspect-sources.json",
                     "docs/zapojeni-potvrzeneho-hardwaru.md",
                     "mechanika/rozmery-nakoupenych-soucastek.md",
                     "scripts/ci/install-cad-slicer.sh",
                     "scripts/cad.py", "scripts/cad.ps1"]:
            with self.subTest(path=path):
                self.assertNotIn("cad", self.selected(path))
        for path in ["mobilni-aplikace/src/main.ts", "mobilni-aplikace/.nvmrc", "web/build-site.py",
                     "contracts/package-lock.json", "backend/Program.cs",
                     "design-system/tokens.json", "design-system/ui.js",
                     "mechanika/final/viewer.js", "mechanika/final/viewer_document.py",
                     "mechanika/final/viewer-workshop.css"]:
            with self.subTest(path=path):
                self.assertNotIn("cad", self.selected(path))

    def test_ui_inputs_keep_real_consumers_and_unknown_mechanics_conservative(self):
        self.assertEqual(self.selected("design-system/tokens.json"),
                         {"repository", "backend", "web", "ios", "site"})
        self.assertEqual(self.selected("mechanika/final/viewer.js"), {"site"})
        self.assertEqual(self.selected("mechanika/final/new-mechanism.py"), {"repository"})
        self.assertEqual(self.selected("firmware/simulator/dashboard-ui.js"), {"site"})
        self.assertEqual(self.selected("firmware/diagnostics/servo-bench/web/panel.css"), {"site"})
        self.assertEqual(self.selected("firmware/src/diagnostics/servo-bench/main.cpp"), {"firmware"})

    def test_presentation_policy_matches_the_pr_selector(self):
        from cad_impact import SOURCE_POLICY
        for name in SOURCE_POLICY.PRESENTATION_SOURCES:
            with self.subTest(name=name):
                self.assertEqual(self.selected("mechanika/final/" + name), {"site"})

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
                self.assertEqual(self.selected("mechanika/final/" + path), {"site"})

    def test_git_diff_includes_both_sides_of_a_rename(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*args):
                return subprocess.check_output(["git", "-C", folder, *args], stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.name", "CI test")
            git("config", "user.email", "ci@example.invalid")
            source = Path(folder) / "firmware/source.cpp"
            source.parent.mkdir(parents=True)
            source.write_text("source\n")
            git("add", ".")
            git("commit", "-m", "base")
            base = git("rev-parse", "HEAD")
            target = Path(folder) / "mobilni-aplikace/source.cpp"
            target.parent.mkdir(parents=True)
            source.rename(target)
            git("add", "-A")
            git("commit", "-m", "move")
            head = git("rev-parse", "HEAD")
            original = subprocess.check_output
            with patch("select_projects.subprocess.check_output", side_effect=lambda args: original(args, cwd=folder)):
                self.assertEqual(set(changed_paths(base, head)),
                                 {"firmware/source.cpp", "mobilni-aplikace/source.cpp"})

    def test_native_trunk_diff_preserves_changes_canceled_against_event_base(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*arguments):
                return subprocess.check_output(["git", "-C", folder, *arguments], stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.name", "CI test")
            git("config", "user.email", "ci@example.invalid")
            source = Path(folder) / "backend/source.cs"
            source.parent.mkdir(parents=True)
            source.write_text("trunk\n")
            git("add", ".")
            git("commit", "-m", "trunk")
            trunk = git("rev-parse", "HEAD")
            source.write_text("lower\n")
            git("commit", "-am", "old lower")
            event_base = git("rev-parse", "HEAD")
            source.write_text("new lower\n")
            git("commit", "-am", "current lower")
            source.write_text("lower\n")
            git("commit", "-am", "upper cancels lower amendment")
            head = git("rev-parse", "HEAD")
            original = subprocess.check_output
            with patch("select_projects.subprocess.check_output", side_effect=lambda args: original(args, cwd=folder)):
                self.assertEqual(changed_paths(event_base, head), [])
                self.assertTrue(select_projects(changed_paths(trunk, head))["backend"])

    def test_git_diff_keeps_deleted_native_inputs(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*arguments):
                return subprocess.check_output(["git", "-C", folder, *arguments], stderr=subprocess.DEVNULL).decode().strip()
            git("init")
            git("config", "user.name", "CI test")
            git("config", "user.email", "ci@example.invalid")
            source = Path(folder) / "mobilni-aplikace/ios/native-adapter.swift"
            source.parent.mkdir(parents=True)
            source.write_text("source\n")
            git("add", ".")
            git("commit", "-m", "base")
            base = git("rev-parse", "HEAD")
            source.unlink()
            git("add", "-A")
            git("commit", "-m", "delete")
            head = git("rev-parse", "HEAD")
            original = subprocess.check_output
            with patch("select_projects.subprocess.check_output", side_effect=lambda arguments: original(arguments, cwd=folder)):
                paths = changed_paths(base, head)
            self.assertEqual(paths, ["mobilni-aplikace/ios/native-adapter.swift"])
            self.assertTrue(select_projects(paths)["ios"])

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

    def test_check_command_reports_missing_selection_after_selection_failure(self):
        with patch("sys.argv", ["select_projects.py", "check"]), patch.dict(
            "os.environ", {"SELECTED_PROJECTS": "{}", "RESULTS": '{"changes":{"result":"failure"}}'}
        ):
            with self.assertRaisesRegex(ValueError, "Missing or invalid project selection"):
                main()

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
        with self.assertRaisesRegex(ValueError, "native stack proof"):
            check_results(selected, jobs, "a" * 64)
        jobs["stack-proof"] = {"result": "success"}
        check_results(selected, jobs, "a" * 64)
        jobs["stack-proof"] = {"result": "skipped"}
        with self.assertRaisesRegex(ValueError, "native stack proof"):
            check_results(selected, jobs, "a" * 64)

    def test_every_selected_project_rejects_non_success_and_missing_results(self):
        selected = dict.fromkeys(PROJECTS, "true")
        jobs = {name: {"result": "success"} for name in (*PROJECTS, "changes")}
        check_results(selected, jobs)
        for name in PROJECTS:
            for result in ("failure", "cancelled", "skipped", "timed_out", "neutral", ""):
                with self.subTest(project=name, result=result), self.assertRaises(ValueError):
                    check_results(selected, {**jobs, name: {"result": result}})
            with self.subTest(project=name, result="missing"), self.assertRaises(ValueError):
                check_results(selected, {key: value for key, value in jobs.items() if key != name})



if __name__ == "__main__":
    unittest.main()
