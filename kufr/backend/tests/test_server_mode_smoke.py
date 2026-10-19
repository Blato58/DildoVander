"""Deployment verifies retained engine configuration without inference."""
import contextlib
import io
import json
import os
import unittest
import urllib.error
from unittest.mock import patch

from diagnostic_test_fixtures import capabilities
from engine_capabilities import validate_engine_capabilities
import server_mode_smoke


class Response(io.BytesIO):
    def __init__(self, status, body):
        super().__init__(json.dumps(body).encode())
        self.status = status
        self.headers = {"Cache-Control": "no-store"}


class DeploymentVerificationTests(unittest.TestCase):
    def test_model_access_is_metadata_only_and_retired_actor_cannot_call_provider(self):
        environment = {"LIVE_ACTOR_MODE": "AutonomousActor", "LIVE_DECISION_MODEL": "gpt-6-luna",
                       "OPENAI_API_KEY": "test-api"}
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch("server_mode_smoke.urllib.request.build_opener") as build:
            build.return_value.open.return_value = Response(200, {"id": "gpt-6-luna"})
            server_mode_smoke.verify_decision_model()
            request = build.return_value.open.call_args.args[0]
            self.assertEqual(request.get_method(), "GET")
            self.assertEqual(request.full_url, "https://api.openai.com/v1/models/gpt-6-luna")
            os.environ["LIVE_ACTOR_MODE"] = "LegacyBackendScripted"
            with self.assertRaises(ValueError):
                server_mode_smoke.verify_decision_model()
            self.assertEqual(build.return_value.open.call_count, 1)

    def test_missing_model_access_fails_before_activation(self):
        environment = {"LIVE_DECISION_MODEL": "gpt-6-luna", "OPENAI_API_KEY": "test-api"}
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch("server_mode_smoke.urllib.request.build_opener") as build:
            build.return_value.open.return_value = Response(200, {"id": "unexpected-model"})
            with self.assertRaises(AssertionError):
                server_mode_smoke.verify_decision_model()
            os.environ["OPENAI_API_KEY"] = ""
            with self.assertRaises(ValueError):
                server_mode_smoke.verify_decision_model()

    def run_verification(self, *, expected_personal="true", ready=True, **changes):
        metadata = {"engines": capabilities(ready), "liveActorMode": "AutonomousActor",
            "liveDecisionModel": "gpt-6-luna", "liveTerminalQuietMs": 1500, "memoryEnabled": True,
            "cloudIndexingEnabled": True, "longTermPersonalEnabled": True, **changes}
        modes = {"revision": "current", "engines": capabilities(ready),
                 "personalityId": "ollivander", "lifetime": "until_restart"}
        memory = {"provider": "openai_vector_store", "configured": True, "degraded": False,
                  "lastVerifiedAt": "2026-10-06T00:00:00Z"}
        requests = []
        def open_request(request, timeout):
            path = request.full_url.split("https://192.0.2.1", 1)[1]
            body = json.loads(request.data) if request.data else None
            requests.append((request.get_method(), path, body))
            bearer = request.get_header("Authorization")
            if path in ("/v1/admin/modes", "/v1/admin/memory") and bearer != "Bearer test-admin":
                raise urllib.error.HTTPError(request.full_url, 401, "Unauthorized", {}, io.BytesIO(b"{}"))
            if path == "/v1/device-access/token":
                return Response(200, {"deviceId": "debug-server-admin", "accessToken": "test-device"})
            if path == "/v1/diagnostics":
                return Response(200, metadata)
            if path.startswith("/v1/admin/memory"):
                return Response(200, memory)
            if body and body["expectedRevision"] != modes["revision"]:
                raise urllib.error.HTTPError(request.full_url, 409, "Conflict", {}, io.BytesIO(b"{}"))
            return Response(200, modes)
        environment = {"PUBLIC_IPV4": "192.0.2.1", "ADMIN_CREDENTIAL": "test-admin",
            "EXPECTED_LIVE_ACTOR_MODE": "AutonomousActor", "EXPECTED_LIVE_DECISION_MODEL": "gpt-6-luna",
            "EXPECTED_LIVE_TERMINAL_QUIET_MS": "1500", "EXPECTED_HYBRID_MEMORY": "true",
            "EXPECTED_CLOUD_INDEXING": "true", "EXPECTED_MEMORY_CONFIGURED": "true",
            "EXPECTED_ENGINE_READY": str(ready).lower()}
        if expected_personal is not None:
            environment["EXPECTED_LONG_TERM_PERSONAL"] = expected_personal
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch("server_mode_smoke.urllib.request.build_opener") as build:
            build.return_value.open.side_effect = open_request
            server_mode_smoke.main()
        return requests

    def test_two_engines_noop_persona_and_optional_personal_memory_configuration(self):
        for ready in (False, True):
            requests = self.run_verification(ready=ready)
            self.assertTrue(any(path.endswith("/memory/verify") for _, path, _ in requests))
            self.assertFalse(any("/conversations" in path for _, path, _ in requests))
            bodies = [body for method, path, body in requests if method == "PUT"]
            self.assertTrue(all(set(body) == {"expectedRevision", "personalityId"} for body in bodies))
        self.run_verification(expected_personal=None, longTermPersonalEnabled=False)

    def test_provisioned_stores_cannot_hide_wrong_configuration(self):
        for field, value in (("liveActorMode", "LegacyBackendScripted"), ("liveDecisionModel", "gpt-6.1-sol"),
            ("liveTerminalQuietMs", 4000), ("memoryEnabled", False), ("cloudIndexingEnabled", False),
            ("longTermPersonalEnabled", False)):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                self.run_verification(**{field: value})

    def test_engine_inventory_models_and_readiness_are_enforced(self):
        for field, value in (("model", "retired-model"), ("ready", "true"), ("unavailableReason", "private-error")):
            contract = capabilities()
            contract["engines"][0][field] = value
            with self.subTest(field=field), self.assertRaises(AssertionError):
                validate_engine_capabilities(contract)
        contract = capabilities()
        contract["engines"].append(dict(contract["engines"][0]))
        with self.assertRaises(AssertionError):
            validate_engine_capabilities(contract)
        with self.assertRaises(AssertionError):
            validate_engine_capabilities(capabilities(False), require_ready=True)


if __name__ == "__main__":
    unittest.main()
