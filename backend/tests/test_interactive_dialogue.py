"""The retired workflow must report an upgrade without any remote action."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock

import interactive_dialogue as dialogue


class RetiredDialogueTests(unittest.TestCase):
    def test_all_old_actions_reject_locally_without_credentials_or_state_use(self):
        for action in ("start", "reply", "finish", "status", "clear", "unknown"):
            with self.subTest(action=action), tempfile.TemporaryDirectory() as directory:
                client = Mock(side_effect=AssertionError("No network adapter may be constructed"))
                environment = {"RUNNER_TEMP": directory, "INPUT_ACTION": action,
                    "INPUT_REQUEST_ID": "8150c7f7-9644-4eff-914e-47e1b216daf1",
                    "INPUT_TEXT": "private-visitor-detail", "INPUT_STATE": "private-prior-guest",
                    "DEV_DEVICE_CREDENTIAL": "private-credential"}
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    report = dialogue.run(environment, client_factory=client)
                client.assert_not_called()
                self.assertEqual(report["status"], "upgrade_required")
                self.assertEqual(report["error"], "conversation_engine_upgrade_required")
                self.assertEqual(report["supportedEngines"], ["gpt-live", "realtime"])
                self.assertEqual(report["remoteRequests"], 0)
                self.assertIsNone(report["state"])
                stored = Path(directory, "interactive-dialogue.json").read_text(encoding="utf-8")
                self.assertEqual(json.loads(stored), report)
                self.assertNotIn("private-", stored + output.getvalue())

    def test_invalid_request_identity_cannot_export_private_input(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            report = dialogue.run({"RUNNER_TEMP": directory, "INPUT_REQUEST_ID": "private-secret"})
        self.assertEqual(report["requestId"], "")
        self.assertNotIn("private-secret", json.dumps(report))


if __name__ == "__main__":
    unittest.main()
