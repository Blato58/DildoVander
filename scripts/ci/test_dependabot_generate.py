"""Generator boundaries; real package resolution is verified separately."""

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "dependabot_generate", Path(__file__).with_name("dependabot_generate.py"))
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


class DependencyGeneratorTests(unittest.TestCase):
    def test_generator_commands_resolve_real_checkout_entry_points(self):
        root = Path(__file__).resolve().parents[2]
        for kind in generator.OUTPUTS:
            with self.subTest(kind=kind), patch.object(generator, "command") as run:
                generator.regenerate(root, kind)
                for invocation in run.call_args_list:
                    arguments, cwd = invocation.args
                    self.assertTrue(cwd.is_dir(), cwd)
                    if arguments[:2] == ["dotnet", "restore"]:
                        self.assertTrue((cwd / arguments[2]).is_file())
                    if arguments[:2] == ["npm", "ci"]:
                        directory = cwd
                        if "--prefix" in arguments:
                            directory /= arguments[arguments.index("--prefix") + 1]
                        self.assertTrue((directory / "package-lock.json").is_file(), directory)

    def test_backend_regenerates_and_checks_both_runtime_graphs(self):
        with patch.object(generator, "command") as run:
            generator.regenerate(Path("checkout"), "backend")
        self.assertEqual(run.call_count, 2)
        self.assertEqual(run.call_args_list[0].args, ([
            "dotnet", "restore", "backend-tests/BackendTests.csproj",
            "-p:SelfContained=true", "--force-evaluate"], Path("checkout")))
        self.assertEqual(run.call_args_list[1].args[0][-1], "--locked-mode")

    def test_native_builds_web_before_real_cli_and_xcode_resolution(self):
        with patch.object(generator, "command") as run:
            generator.regenerate(Path("checkout"), "native")
        self.assertEqual([call.args[0] for call in run.call_args_list], [
            ["npm", "ci", "--ignore-scripts"],
            ["npm", "ci", "--ignore-scripts", "--prefix", "../contracts"],
            ["npm", "ci", "--ignore-scripts", "--prefix", "../firmware/simulator"],
            ["npm", "run", "build"],
            ["npx", "--no-install", "cap", "sync", "ios"],
            ["xcodebuild", "-resolvePackageDependencies", "-project",
             "ios/App/App.xcodeproj", "-scheme", "App"],
        ])

    def test_failed_regeneration_stops_before_collecting_outputs(self):
        with patch.object(generator, "command", side_effect=subprocess.CalledProcessError(1, "dotnet")):
            with self.assertRaises(subprocess.CalledProcessError):
                generator.regenerate(Path("checkout"), "backend")

    def test_payload_uses_only_selected_regular_outputs_and_normalizes_newlines(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in generator.OUTPUTS["backend"]:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'{"version": 1}\r\n')
            plan = {"number": 198, "head": "a" * 40, "base": "b" * 40, "backend": True}
            with patch.object(generator.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "")):
                result = generator.payload(root, "backend", plan)
            self.assertEqual(result["head"], plan["head"])
            self.assertEqual([f["path"] for f in result["files"]], list(generator.OUTPUTS["backend"]))
            self.assertTrue(all("\r" not in f["content"] for f in result["files"]))
            json.dumps(result)

    def test_payload_rejects_unexpected_tracked_mutation(self):
        with patch.object(generator.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "AGENTS.md\n")):
            with self.assertRaisesRegex(ValueError, "unexpected tracked"):
                generator.payload(Path("checkout"), "backend", {
                    "number": 198, "head": "a" * 40, "base": "b" * 40, "backend": True})

    def test_payload_rejects_wrong_plan_and_missing_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaisesRegex(ValueError, "immutable"):
                generator.payload(root, "backend", {"number": 1, "head": "main", "base": "b" * 40})
            with self.assertRaisesRegex(ValueError, "not selected"):
                generator.payload(root, "native", {"number": 1, "head": "a" * 40, "base": "b" * 40})
            with patch.object(generator.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "")):
                with self.assertRaisesRegex(ValueError, "regular generated"):
                    generator.payload(root, "backend", {
                        "number": 1, "head": "a" * 40, "base": "b" * 40, "backend": True})


if __name__ == "__main__":
    unittest.main()
