"""Deployment must verify active settings, not just existing store IDs."""
import contextlib
import io
import json
import os
import unittest
import urllib.error
from unittest.mock import patch

import server_mode_smoke


class Response(io.BytesIO):
    def __init__(self, status, body):
        super().__init__(json.dumps(body).encode())
        self.status = status
        self.headers = {'Cache-Control': 'no-store'}


class DeploymentVerificationTests(unittest.TestCase):
    def test_model_access_is_metadata_only_and_legacy_does_not_call_provider(self):
        environment = {'LIVE_ACTOR_MODE': 'AutonomousActor', 'LIVE_DECISION_MODEL': 'gpt-6-luna',
                       'OPENAI_API_KEY': 'test-api'}
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch('server_mode_smoke.urllib.request.build_opener') as build:
            build.return_value.open.return_value = Response(200, {'id': 'gpt-6-luna'})
            server_mode_smoke.verify_decision_model()
            request = build.return_value.open.call_args.args[0]
            self.assertEqual(request.get_method(), 'GET')
            self.assertEqual(request.full_url, 'https://api.openai.com/v1/models/gpt-6-luna')
            self.assertEqual(request.get_header('Authorization'), 'Bearer test-api')
            os.environ['LIVE_ACTOR_MODE'] = 'LegacyBackendScripted'
            server_mode_smoke.verify_decision_model()
            self.assertEqual(build.return_value.open.call_count, 1)

    def test_missing_model_access_fails_before_activation(self):
        environment = {'LIVE_ACTOR_MODE': 'AutonomousActor', 'LIVE_DECISION_MODEL': 'gpt-6-luna',
                       'OPENAI_API_KEY': 'test-api'}
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch('server_mode_smoke.urllib.request.build_opener') as build:
            build.return_value.open.return_value = Response(200, {'id': 'unexpected-model'})
            with self.assertRaises(AssertionError):
                server_mode_smoke.verify_decision_model()
            os.environ['OPENAI_API_KEY'] = ''
            with self.assertRaises(ValueError):
                server_mode_smoke.verify_decision_model()

    def run_verification(self, **changes):
        metadata = {
            'dialogMode': 'responses', 'voiceMode': 'openai',
            'liveActorMode': 'AutonomousActor', 'liveDecisionModel': 'gpt-6-luna',
            'liveTerminalQuietMs': 1500, 'memoryEnabled': True,
            'cloudIndexingEnabled': True, 'longTermPersonalEnabled': False,
            **changes,
        }
        modes = {'revision': 'current', 'dialogMode': 'responses', 'voiceMode': 'openai',
                 'dialogAvailable': True, 'voiceAvailable': True, 'lifetime': 'until_restart'}
        memory = {'provider': 'openai_vector_store', 'configured': True, 'degraded': False,
                  'lastVerifiedAt': '2026-10-06T00:00:00Z'}
        requests = []

        def open_request(request, timeout):
            requests.append(request.full_url)
            bearer = request.get_header('Authorization')
            path = request.full_url.split('https://192.0.2.1', 1)[1]
            if path in ('/v1/admin/modes', '/v1/admin/memory') and bearer != 'Bearer test-admin':
                raise urllib.error.HTTPError(request.full_url, 401, 'Unauthorized', {}, io.BytesIO(b'{}'))
            if path == '/v1/device-access/token':
                return Response(200, {'deviceId': 'debug-server-admin', 'accessToken': 'test-device'})
            if path == '/v1/diagnostics':
                return Response(200, metadata)
            if path.startswith('/v1/admin/memory'):
                return Response(200, memory)
            body = json.loads(request.data) if request.data else None
            if body and body['expectedRevision'] != modes['revision']:
                raise urllib.error.HTTPError(request.full_url, 409, 'Conflict', {}, io.BytesIO(b'{}'))
            return Response(200, modes)

        environment = {
            'PUBLIC_IPV4': '192.0.2.1', 'ADMIN_CREDENTIAL': 'test-admin',
            'EXPECTED_LIVE_ACTOR_MODE': 'AutonomousActor', 'EXPECTED_LIVE_DECISION_MODEL': 'gpt-6-luna',
            'EXPECTED_LIVE_TERMINAL_QUIET_MS': '1500', 'EXPECTED_HYBRID_MEMORY': 'true',
            'EXPECTED_CLOUD_INDEXING': 'true', 'EXPECTED_MEMORY_CONFIGURED': 'true',
        }
        with patch.dict(os.environ, environment, clear=True), contextlib.redirect_stdout(io.StringIO()), \
                patch('server_mode_smoke.urllib.request.build_opener') as build:
            build.return_value.open.side_effect = open_request
            server_mode_smoke.main()
        return requests

    def test_active_luna_memory_and_checkpoint_configuration(self):
        requests = self.run_verification()
        self.assertTrue(any(path.endswith('/v1/admin/memory/verify') for path in requests))
        self.assertFalse(any('/conversations' in path for path in requests))

    def test_provisioned_stores_cannot_hide_inactive_or_wrong_configuration(self):
        for field, value in (
            ('liveActorMode', 'LegacyBackendScripted'), ('liveDecisionModel', 'gpt-6.1-sol'),
            ('liveTerminalQuietMs', 4000), ('memoryEnabled', False),
            ('cloudIndexingEnabled', False), ('longTermPersonalEnabled', True),
        ):
            with self.subTest(field=field), self.assertRaises(AssertionError):
                self.run_verification(**{field: value})


if __name__ == '__main__':
    unittest.main()
