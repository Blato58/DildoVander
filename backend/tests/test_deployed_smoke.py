"""The deployment smoke checks auth and upgrade contracts without generation."""
import contextlib
import io
import unittest
from unittest.mock import patch

import deployed_smoke
from diagnostic_test_fixtures import capabilities


class DeployedSmokeTests(unittest.TestCase):
    def test_no_conversation_is_created_and_all_retired_routes_require_upgrade(self):
        requests = []
        def call(method, path, **arguments):
            requests.append((method, path, arguments))
            if path == "/healthz":
                return 200, {"status": "ok"}
            if path == "/v1/device-access/token":
                return (200, {"deviceId": "fixture", "accessToken": "private-token"}) if arguments.get("bearer") else (401, {})
            if path == "/v1/diagnostics":
                return 200, {"engines": capabilities()}
            return 410, {"code": "conversation_engine_upgrade_required"}
        with patch.dict(deployed_smoke.os.environ, {"DEVICE_ID": "fixture", "DEV_DEVICE_CREDENTIAL": "private-credential"}, clear=True), \
                patch.object(deployed_smoke, "call", side_effect=call), contextlib.redirect_stdout(io.StringIO()) as output:
            deployed_smoke.main()
        self.assertFalse(any(path == "/v1/conversations" or path.startswith("/v1/operations") for _, path, _ in requests))
        retired = [path.rsplit("/", 1)[-1] for _, path, _ in requests if "/conversations/" in path]
        self.assertEqual(retired, ["opening", "turns", "transcriptions", "speech", "realtime-turns"])
        self.assertNotIn("private-", output.getvalue())


if __name__ == "__main__":
    unittest.main()
