import importlib.util
import json
import os
from pathlib import Path
import ssl
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from unittest.mock import patch
from diagnostic_test_fixtures import capabilities

MODULE = Path(__file__).resolve().parents[1] / 'codex/server_logs_mcp.py'
spec = importlib.util.spec_from_file_location('server_logs_mcp', MODULE)
mcp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mcp)

PAGE = {'storeId': '00000000-0000-4000-8000-000000000001', 'oldestCursor': 1, 'latestCursor': 1,
        'nextCursor': 1, 'hasMore': False, 'truncated': False, 'droppedWrites': 0,
        'entries': [{'id': 1, 'timestamp': '2026-10-01T12:00:00+00:00', 'level': 'warning',
                     'eventCode': 'realtime_failed', 'code': 'cost_limit', 'privateExtra': 'private-secret'}]}
STATUS = {'revision': PAGE['storeId'], 'engines': capabilities(False),
          'activeConversations': 0, 'lifetime': 'until_restart'}


class MCPTests(unittest.TestCase):
    def test_provider_job_projection_preserves_safe_attempt_and_http_code(self):
        page = {**PAGE, 'entries': [{**PAGE['entries'][0], 'eventCode': 'testing_character_job_failed',
                                   'code': 'openai_http_429', 'status': 429, 'attempt': 3,
                                   'operationId': PAGE['storeId']}]}
        projected = mcp.project('/v1/admin/logs', page)['entries'][0]
        self.assertEqual(projected['attempt'], 3)
        self.assertEqual(projected['code'], 'openai_http_429')
        self.assertNotIn('private-secret', json.dumps(projected))
        for invalid in [True, 0, -1, 1001, 'private-attempt']:
            with self.subTest(attempt=invalid), self.assertRaises(mcp.DiagnosticError):
                mcp.project('/v1/admin/logs', {**page, 'entries': [{**page['entries'][0], 'attempt': invalid}]})

    def test_status_projects_only_the_two_retained_engine_contracts(self):
        source = {**STATUS, 'engines': capabilities(False), 'privateGuest': 'private-secret'}
        source['engines']['engines'][0]['providerRaw'] = 'private-secret'
        projected = mcp.project('/v1/admin/logs/status', source)
        self.assertEqual(projected['engines']['primaryEngine'], 'gpt-live')
        self.assertEqual(len(projected['engines']['engines']), 2)
        self.assertNotIn('private-secret', json.dumps(projected))
        for field, value in [('model', 'retired-model'), ('ready', 'true'), ('unavailableReason', 'private-secret')]:
            invalid = {**STATUS, 'engines': capabilities(False)}
            invalid['engines']['engines'][0][field] = value
            with self.subTest(field=field), self.assertRaises(mcp.DiagnosticError):
                mcp.project('/v1/admin/logs/status', invalid)

    def test_constrained_audio_route_from_deployed_server(self):
        route = '/v1/conversations/{id}/gpt-live/{streamId:guid}/audio'
        page = {**PAGE, 'entries': [{**PAGE['entries'][0], 'route': route, 'status': 204}]}
        projected = mcp.project('/v1/admin/logs', page)
        self.assertEqual(projected['entries'][0]['route'], route)
        self.assertNotIn('private-secret', json.dumps(projected))
        for invalid in [route + '?token=secret', route + '\nsecret']:
            with self.assertRaises(mcp.DiagnosticError):
                mcp.project('/v1/admin/logs', {**page, 'entries': [{**page['entries'][0], 'route': invalid}]})

    def test_protocol_and_argument_isolation(self):
        message = lambda method, params={}: {'jsonrpc': '2.0', 'id': 1, 'method': method, 'params': params}
        self.assertIn('capabilities', mcp.dispatch(message('initialize'))['result'])
        self.assertEqual(len(mcp.dispatch(message('tools/list'))['result']['tools']), 2)
        self.assertIsNone(mcp.dispatch({'jsonrpc': '2.0', 'method': 'notifications/initialized'}))
        self.assertEqual(mcp.dispatch(message('unknown'))['error']['code'], -32601)
        for args in [{'after': -1}, {'limit': True}, {'limit': 201}, {'level': 'private-text'},
                     {'traceId': 'secret'}, {'url': 'https://other.test'}, {'operationId': []}]:
            with self.assertRaises(mcp.DiagnosticError):
                mcp.validate(args)
        self.assertEqual(mcp.validate({'after': 3})['after'], 3)
        value = mcp.project('/v1/admin/logs', PAGE)
        self.assertNotIn('private-secret', json.dumps(value))
        with self.assertRaises(mcp.DiagnosticError):
            mcp.project('/v1/admin/logs', {**PAGE, 'nextCursor': 0})
        with self.assertRaises(mcp.DiagnosticError):
            mcp.project('/v1/admin/logs', {**PAGE, 'entries': PAGE['entries'] * 2})

    def test_stdio_without_credentials(self):
        lines = [json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2025-06-18'}}),
                 json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}),
                 json.dumps({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {'name': 'read_server_logs'}})]
        env = {**os.environ, 'KUFR_LOG_TOKEN': ''}
        output = subprocess.run([sys.executable, str(MODULE)], input='\n'.join(lines) + '\n', text=True,
                                capture_output=True, env=env, check=True)
        responses = [json.loads(line) for line in output.stdout.splitlines()]
        self.assertEqual(len(responses), 2)
        self.assertEqual(responses[0]['result']['protocolVersion'], '2025-06-18')
        self.assertTrue(responses[1]['result']['isError'])
        self.assertEqual(output.stderr, '')

    def test_https_auth_redirect_size_and_snapshot(self):
        with tempfile.TemporaryDirectory(prefix='kufr-mcp-') as directory:
            root = Path(directory)
            openssl = shutil.which('openssl')
            if openssl is None and os.name == 'nt' and shutil.which('git'):
                bundled = Path(shutil.which('git')).resolve().parent.parent / 'usr/bin/openssl.exe'
                if bundled.is_file():
                    openssl = str(bundled)
            if openssl is None:
                self.skipTest('OpenSSL is required for the local HTTPS transport fixture')
            subprocess.run([openssl, 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
                            '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost',
                            '-keyout', str(root / 'key.pem'), '-out', str(root / 'cert.pem')],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            paths = []
            class Peer(BaseHTTPRequestHandler):
                mode = 'ok'
                def log_message(self, *args):
                    pass
                def do_GET(self):
                    paths.append(self.path)
                    if self.headers.get('Authorization') != 'Bearer synthetic-reader-secret':
                        self.send_error(401)
                        return
                    if self.mode == 'redirect':
                        self.send_response(302)
                        self.send_header('Location', '/credential-leak')
                        self.end_headers()
                        return
                    if self.mode == 'denied':
                        self.send_response(401)
                        self.end_headers()
                        self.wfile.write(b'private-provider-secret')
                        return
                    payload = json.dumps(STATUS if self.path.endswith('/status') else PAGE).encode()
                    if self.mode == 'oversize':
                        payload = b'x' * (mcp.MAX_RESPONSE + 1)
                    self.send_response(200)
                    self.send_header('Content-Type', 'application/json')
                    self.send_header('Content-Length', str(len(payload)))
                    self.end_headers()
                    self.wfile.write(payload)
            server = HTTPServer(('localhost', 0), Peer)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(root / 'cert.pem', root / 'key.pem')
            server.socket = context.wrap_socket(server.socket, server_side=True)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            environment = {'KUFR_BACKEND_URL': f'https://localhost:{server.server_port}/',
                           'KUFR_LOG_TOKEN': 'synthetic-reader-secret', 'SSL_CERT_FILE': str(root / 'cert.pem')}
            try:
                with patch.dict(os.environ, environment):
                    backend = mcp.Backend()
                    self.assertEqual(backend.read('/v1/admin/logs', {'after': 0})['nextCursor'], 1)
                    self.assertIn('after=0', paths[-1])
                    for mode in ['redirect', 'oversize', 'denied']:
                        Peer.mode = mode
                        with self.assertRaises(mcp.DiagnosticError) as error:
                            backend.read('/v1/admin/logs')
                        self.assertNotIn('secret', str(error.exception))
                    self.assertNotIn('/credential-leak', paths)
                    Peer.mode = 'ok'
                    output = root / 'snapshot.json'
                    process = subprocess.run([sys.executable, str(MODULE), '--snapshot', str(output)],
                                             capture_output=True, text=True, check=True)
                    self.assertNotIn('secret', process.stdout + process.stderr + output.read_text())
                    self.assertEqual(json.loads(output.read_text())['status']['engines']['primaryEngine'], 'gpt-live')
                for address in ['http://localhost/', 'https://user@localhost/', 'https://localhost/path', 'https://localhost/?token=secret']:
                    with patch.dict(os.environ, {**environment, 'KUFR_BACKEND_URL': address}):
                        with self.assertRaises(mcp.DiagnosticError):
                            mcp.Backend()
            finally:
                server.shutdown()
                server.server_close()
                thread.join()


if __name__ == '__main__':
    unittest.main()
