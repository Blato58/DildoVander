"""Execute the real tracking CI step against a multi-commit receipt mutation."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
from test_project_status import receipt, work_item


def tracking_step():
    # Extract this literal Python run block; actionlint validates the surrounding YAML.
    text = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    step = text.split("      - name: Test tracking and validate the committed snapshot\n", 1)[1]
    body = step.split("        run: |\n", 1)[1]
    lines = []
    for line in body.splitlines():
        if line.strip() and not line.startswith("          "):
            break
        lines.append(line[10:])
    return "\n".join(lines)


class TrackingWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        project = self.root / "docs/project"
        (project / "work-items").mkdir(parents=True)
        (project / "evidence").mkdir()
        (self.root / "scripts").mkdir()
        shutil.copyfile(ROOT / "scripts/project_status.py", self.root / "scripts/project_status.py")
        item = work_item()
        item["acceptance"][0]["evidenceRefs"] = ["PASS"]
        (project / "work-items/ITEM.json").write_text(json.dumps(item), encoding="utf-8")
        self.receipt_path = project / "evidence/PASS.json"
        self.receipt_path.write_text(json.dumps(receipt()), encoding="utf-8")
        self.git("init", "--quiet")
        self.git("config", "user.email", "synthetic-fixture@localhost")
        self.git("config", "user.name", "Synthetic fixture")
        self.git("config", "core.autocrlf", "false")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Synthetic accepted baseline")
        self.base = self.git("rev-parse", "HEAD").strip()
        modified = receipt()
        modified["observation"] = "Synthetic illegal mutation in an earlier PR commit."
        self.receipt_path.write_text(json.dumps(modified), encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Synthetic earlier receipt mutation")
        (self.root / "unrelated.txt").write_text("Synthetic later change", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "Synthetic later PR commit")

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], text=True, encoding="utf-8")

    def execute_step(self, baseline):
        actual_run = subprocess.run
        self.commands = []

        def run(command, **kwargs):
            self.commands.append(command)
            if command[1:3] == ["scripts/project_status.py", "validate"]:
                return actual_run(command, cwd=self.root, capture_output=True, text=True, **kwargs)
            return subprocess.CompletedProcess(command, 0)

        with patch.dict(os.environ, {"TRACKING_BASE_SHA": baseline}), patch("subprocess.run", side_effect=run):
            exec(compile(tracking_step(), "<actual tracking CI step>", "exec"), {})

    def test_dispatched_multi_commit_pr_checks_explicit_base(self):
        with self.assertRaises(subprocess.CalledProcessError) as result:
            self.execute_step(self.base)
        self.assertEqual(2, result.exception.returncode)
        self.assertIn("immutable", result.exception.stderr)
        self.assertIn([sys.executable, "scripts/project_status.py", "validate", "--base", self.base], self.commands)

    def test_recovery_without_pr_base_uses_parent(self):
        self.execute_step("")
        self.assertIn([sys.executable, "scripts/project_status.py", "validate", "--base", "HEAD^"], self.commands)


if __name__ == "__main__":
    unittest.main()
