"""Run workspace setup against an isolated copy of the repository layout."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("pwsh") or shutil.which("powershell")


@unittest.skipUnless(POWERSHELL, "PowerShell is required for workspace setup")
class WorkspaceLayoutTests(unittest.TestCase):
    def copy_setup(self, workspace):
        app = workspace / "DildoVander-app"
        setup = app / "devtools/workspace"
        shutil.copytree(ROOT / "devtools/workspace", setup)
        firmware = app / "firmware"
        firmware.mkdir()
        (firmware / "platformio.ini").write_text("[env:production]\n", encoding="utf-8")
        return [POWERSHELL, "-NoProfile", "-File", str(setup / "Sync-Workspace.ps1"),
                "-WorkspacePath", str(workspace)]

    def test_upgrade_migrates_only_the_old_managed_firmware_location(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            command = self.copy_setup(workspace)
            old_path = "DildoVander-app/kufr/firmware"
            custom_task = {"label": "My local task", "detail": "Personal task",
                           "type": "process", "command": "echo", "args": ["custom"],
                           "options": {"cwd": "${workspaceFolder}/custom-tools"}}
            managed_task = {"label": "Firmware: Build Production",
                            "detail": "Managed by the DildoVander workspace setup.",
                            "type": "process", "command": "pio",
                            "args": ["run", "-d", "${workspaceFolder}/" + old_path],
                            "options": {"cwd": "${workspaceFolder}/" + old_path}}
            custom_folder = {"name": "My tools", "path": "custom-tools"}
            document = workspace / "DildoVander.code-workspace"
            document.write_text(json.dumps({
                "folders": [{"name": "DildoVander", "path": "."},
                            {"name": "Firmware", "path": old_path}, custom_folder],
                "settings": {"editor.fontSize": 19},
                "tasks": {"version": "2.0.0", "tasks": [custom_task]},
            }), encoding="utf-8")
            vscode = workspace / ".vscode"
            vscode.mkdir()
            task_file = vscode / "tasks.json"
            task_file.write_text(json.dumps({"version": "2.0.0",
                                              "tasks": [managed_task, custom_task]}), encoding="utf-8")
            refresh = [*command, "-RefreshManagedTasks"]
            result = subprocess.run(refresh, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            upgraded = json.loads(document.read_text(encoding="utf-8-sig"))
            folders = {folder["name"]: folder for folder in upgraded["folders"]}
            self.assertEqual(folders["Firmware"]["path"], "DildoVander-app/firmware")
            self.assertEqual(folders["My tools"], custom_folder)
            self.assertEqual(upgraded["settings"]["editor.fontSize"], 19)
            self.assertIn(custom_task, upgraded["tasks"]["tasks"])
            tasks = json.loads(task_file.read_text(encoding="utf-8-sig"))["tasks"]
            self.assertIn(custom_task, tasks)
            migrated_task = next(task for task in tasks if task["label"] == managed_task["label"])
            self.assertEqual(migrated_task["options"]["cwd"], "${workspaceFolder}/DildoVander-app/firmware")
            self.assertIn("${workspaceFolder}/DildoVander-app/firmware", migrated_task["args"])
            before = {path: path.read_bytes() for path in (document, task_file)}
            rerun = subprocess.run(refresh, capture_output=True, text=True)
            self.assertEqual(rerun.returncode, 0, rerun.stdout + rerun.stderr)
            self.assertTrue(all(path.read_bytes() == content for path, content in before.items()))

    def test_custom_and_ambiguous_firmware_folders_are_rejected(self):
        for firmware_paths in (("custom/firmware",),
                               ("DildoVander-app/kufr/firmware", "DildoVander-app/firmware")):
            with self.subTest(paths=firmware_paths), tempfile.TemporaryDirectory() as temporary:
                workspace = Path(temporary)
                command = self.copy_setup(workspace)
                document = workspace / "DildoVander.code-workspace"
                document.write_text(json.dumps({"folders": [
                    {"name": "Firmware", "path": path} for path in firmware_paths
                ]}), encoding="utf-8")
                before = document.read_bytes()
                result = subprocess.run([*command, "-RefreshManagedTasks"], capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                output = result.stdout + result.stderr
                self.assertIn("Firmware", output)
                self.assertIn("conflict", output)
                self.assertEqual(document.read_bytes(), before)

    def test_setup_resolves_root_firmware_and_preserves_local_configuration(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            command = self.copy_setup(workspace)
            firmware = workspace / "DildoVander-app/firmware"
            vscode = workspace / ".vscode"
            vscode.mkdir()
            (vscode / "settings.json").write_text(
                json.dumps({"editor.fontSize": 17}), encoding="utf-8")
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            document = workspace / "DildoVander.code-workspace"
            generated = json.loads(document.read_text(encoding="utf-8-sig"))
            folders = {folder["name"]: (workspace / folder["path"]).resolve()
                       for folder in generated["folders"]}
            self.assertEqual(folders["Firmware"], firmware.resolve())
            self.assertTrue((folders["Firmware"] / "platformio.ini").is_file())
            settings = json.loads((vscode / "settings.json").read_text(encoding="utf-8-sig"))
            self.assertEqual(settings["editor.fontSize"], 17)
            tasks = json.loads((vscode / "tasks.json").read_text(encoding="utf-8-sig"))
            for task in tasks["tasks"]:
                cwd = task.get("options", {}).get("cwd")
                if cwd:
                    self.assertEqual(Path(cwd.replace("${workspaceFolder}", str(workspace))).resolve(),
                                     firmware.resolve())
            before = document.read_bytes()
            rerun = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(rerun.returncode, 0, rerun.stdout + rerun.stderr)
            self.assertEqual(document.read_bytes(), before)


if __name__ == "__main__":
    unittest.main()
