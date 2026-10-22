import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest


@unittest.skipUnless(sys.platform == "linux" and shutil.which("bash"), "Control runner hook requires Linux and Bash")
class ControlRunnerHookTests(unittest.TestCase):
    def run_hook(self, **overrides):
        environment = {
            "PATH": os.environ["PATH"],
            "GITHUB_REPOSITORY": "Duloc-Dungeon/DildoVander",
            "GITHUB_WORKFLOW_REF": "Duloc-Dungeon/DildoVander/.github/workflows/pr-sync.yml@refs/heads/main",
            "GITHUB_EVENT_NAME": "workflow_dispatch",
            "GITHUB_REF": "refs/heads/main",
        }
        environment.update(overrides)
        return subprocess.run(
            ["bash", str(Path(__file__).with_name("control-runner-hook.sh"))],
            env=environment, capture_output=True, text=True, timeout=5,
        )

    def test_trusted_main_controller_events(self):
        for event in ("pull_request_target", "push", "workflow_dispatch"):
            with self.subTest(event=event):
                self.assertEqual(self.run_hook(GITHUB_EVENT_NAME=event).returncode, 0)

    def test_untrusted_metadata_is_rejected(self):
        cases = (
            {"GITHUB_REPOSITORY": "attacker/DildoVander"},
            {"GITHUB_WORKFLOW_REF": "Duloc-Dungeon/DildoVander/.github/workflows/ci.yml@refs/heads/main"},
            {"GITHUB_WORKFLOW_REF": "Duloc-Dungeon/DildoVander/.github/workflows/pr-sync.yml@refs/pull/172/merge"},
            {"GITHUB_EVENT_NAME": "pull_request"},
            {"GITHUB_EVENT_NAME": "push", "GITHUB_REF": "refs/heads/feature"},
            {"GITHUB_WORKFLOW_REF": ""},
        )
        for metadata in cases:
            with self.subTest(metadata=metadata):
                result = self.run_hook(**metadata)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Control runner rejected", result.stderr)


if __name__ == "__main__":
    unittest.main()
