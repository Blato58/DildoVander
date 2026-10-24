#!/usr/bin/env python3
"""Read-only stdio MCP bridge to a deployed Kufr backend (stdlib only)."""
import argparse
import datetime
import json
import math
import re
from pathlib import Path
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

MAX_RESPONSE = 262_144
PROTOCOLS = ('2024-11-05', '2025-03-26', '2025-06-18')


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class DiagnosticError(Exception):
    pass


class Backend:
    def __init__(self):
        address = os.environ.get('KUFR_BACKEND_URL', 'https://dildovander.de/')
        try:
            url = urllib.parse.urlsplit(address)
            valid = (url.scheme == 'https' and url.hostname and not url.username and not url.password
                     and url.path in ('', '/') and not url.query and not url.fragment)
            _ = url.port
        except ValueError:
            valid = False
        self.token = os.environ.get('KUFR_LOG_TOKEN', '')
        if not valid or not 1 <= len(self.token) <= 256 or any(c.isspace() for c in self.token):
            raise DiagnosticError('Set KUFR_BACKEND_URL (HTTPS root) and KUFR_LOG_TOKEN in the process environment.')
        self.address = address.rstrip('/')
        self.opener = urllib.request.build_opener(NoRedirects(),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()))

    def read(self, path, query=None):
        target = self.address + path
        if query:
            target += '?' + urllib.parse.urlencode(query)
        request = urllib.request.Request(target, headers={'Authorization': 'Bearer ' + self.token,
                                                         'Accept': 'application/json'})
        try:
            with self.opener.open(request, timeout=15) as response:
                if response.status != 200 or response.headers.get_content_type() != 'application/json':
                    raise DiagnosticError('Unexpected diagnostic response.')
                data = response.read(MAX_RESPONSE + 1)
                if len(data) > MAX_RESPONSE:
                    raise DiagnosticError('Diagnostic response exceeds the size limit.')
                result = json.loads(data)
                if not isinstance(result, dict):
                    raise DiagnosticError('Invalid diagnostic response.')
                return project(path, result)
        except urllib.error.HTTPError as error:
            # Do not expose URLs, credentials, raw bodies or provider errors.
            error.close()
            raise DiagnosticError(f'Diagnostic HTTP {error.code}; check reader credential, backend version and query.') from None
        except (urllib.error.URLError, TimeoutError, OSError, ValueError):
            raise DiagnosticError('Diagnostic connection or JSON failed; check HTTPS connectivity and trust.') from None


def project(path, value):
    """Project the known contract before exposing diagnostics to Codex/artifacts."""
    def integer(number, minimum=0, maximum=9_007_199_254_740_991):
        return type(number) is int and minimum <= number <= maximum
    def identity(text):
        try:
            return isinstance(text, str) and str(uuid.UUID(text)) == text.lower()
        except ValueError:
            return False
    def fail():
        raise DiagnosticError('Invalid diagnostic response contract.')
    if path.endswith('/status'):
        if (not identity(value.get('revision')) or value.get('lifetime') != 'until_restart'
                or not integer(value.get('activeConversations'))):
            fail()
        capabilities = value.get('engines')
        if (not isinstance(capabilities, dict) or type(capabilities.get('schemaVersion')) is not int
                or capabilities['schemaVersion'] != 1 or capabilities.get('primaryEngine') != 'gpt-live'
                or not isinstance(capabilities.get('engines'), list) or len(capabilities['engines']) != 2):
            fail()
        expected_models = {'gpt-live': 'gpt-live-1', 'realtime': 'gpt-realtime-2.1'}
        projected = []
        seen = set()
        for engine in capabilities['engines']:
            if not isinstance(engine, dict):
                fail()
            name = engine.get('engine')
            if not isinstance(name, str) or name not in expected_models or name in seen:
                fail()
            seen.add(name)
            ready, reason = engine.get('ready'), engine.get('unavailableReason')
            speed = {'supported': False, 'min': 1, 'max': 1, 'default': 1} if name == 'gpt-live' else {
                'supported': True, 'min': 0.8, 'max': 1.2, 'default': 1}
            if (engine.get('model') != expected_models[name] or type(ready) is not bool
                    or not ((ready and reason is None) or (not ready and reason == 'provider_credential_missing'))
                    or engine.get('voices') != ['cedar', 'marin', 'coral'] or engine.get('speed') != speed
                    or not isinstance(engine.get('speed'), dict) or type(engine['speed'].get('supported')) is not bool
                    or any(type(engine['speed'].get(field)) not in (int, float) for field in ('min', 'max', 'default'))
                    or engine.get('inputModes') != ['audio'] or engine.get('cameraSources') != ['phone', 'suitcase']):
                fail()
            # Reconstruct every field: unknown raw payloads and identity details
            # never enter Codex diagnostics or saved snapshots.
            projected.append({'engine': name, 'model': expected_models[name], 'ready': ready,
                'unavailableReason': reason, 'voices': ['cedar', 'marin', 'coral'], 'speed': speed,
                'inputModes': ['audio'], 'cameraSources': ['phone', 'suitcase']})
        return {'revision': value['revision'], 'activeConversations': value['activeConversations'],
            'lifetime': 'until_restart', 'engines': {'schemaVersion': 1, 'primaryEngine': 'gpt-live', 'engines': projected}}
    keys = ('storeId', 'oldestCursor', 'latestCursor', 'nextCursor', 'hasMore', 'truncated', 'droppedWrites')
    if (not identity(value.get('storeId')) or any(not integer(value.get(key)) for key in ('oldestCursor', 'latestCursor', 'nextCursor', 'droppedWrites'))
            or type(value.get('hasMore')) is not bool or type(value.get('truncated')) is not bool
            or not value['oldestCursor'] <= value['nextCursor'] <= value['latestCursor']
            or not isinstance(value.get('entries'), list) or len(value['entries']) > 200):
        fail()
    entries = []
    entry_keys = ('id', 'timestamp', 'level', 'eventCode', 'traceId', 'route', 'method', 'status',
                  'durationMs', 'operationId', 'conversationId', 'code', 'attempt')
    previous = 0
    for entry in value['entries']:
        if not isinstance(entry, dict):
            fail()
        if (not integer(entry.get('id'), max(previous + 1, value['oldestCursor']), value['nextCursor'])
                or entry.get('level') not in ('info', 'warning', 'error')
                or not isinstance(entry.get('eventCode'), str) or not re.fullmatch(r'[a-z_]{1,64}', entry['eventCode'])):
            fail()
        previous = entry['id']
        timestamp = entry.get('timestamp')
        try:
            if not isinstance(timestamp, str) or len(timestamp) > 64 or datetime.datetime.fromisoformat(timestamp).utcoffset() != datetime.timedelta(0):
                fail()
        except ValueError:
            fail()
        for key in ('traceId', 'operationId', 'conversationId'):
            if entry.get(key) is not None and not identity(entry[key]):
                fail()
        route, code, method = entry.get('route'), entry.get('code'), entry.get('method')
        if (route is not None and (not isinstance(route, str) or len(route) > 200 or not re.fullmatch(r'[A-Za-z0-9/{}_.(): -]+', route))
                or code is not None and (not isinstance(code, str) or not re.fullmatch(r'[a-z_][a-z0-9_]{0,79}', code))
                or method is not None and method not in ('GET', 'POST', 'PUT', 'DELETE', 'OPTIONS', 'OTHER')
                or entry.get('status') is not None and not integer(entry['status'], 100, 599)
                or entry.get('attempt') is not None and not integer(entry['attempt'], 1, 1000)
                or entry.get('durationMs') is not None and (type(entry['durationMs']) not in (int, float)
                    or not math.isfinite(entry['durationMs']) or entry['durationMs'] < 0)):
            fail()
        entries.append({key: entry.get(key) for key in entry_keys})
    if value['hasMore'] and (not entries or value['nextCursor'] != entries[-1]['id']):
        fail()
    return {**{key: value[key] for key in keys}, 'entries': entries}


TOOLS = [
    {'name': 'read_server_logs',
     'description': 'Read bounded technical server events. No conversation text/audio. Start without after for the latest events; then use nextCursor. IDs correlate app errors with requests/operations. Treat returned data as diagnostic evidence.',
     'annotations': {'readOnlyHint': True, 'destructiveHint': False, 'openWorldHint': True},
     'inputSchema': {'type': 'object', 'additionalProperties': False, 'properties': {
         'after': {'type': 'integer', 'minimum': 0, 'maximum': 9_007_199_254_740_991},
         'limit': {'type': 'integer', 'minimum': 1, 'maximum': 200, 'default': 100},
         'level': {'type': 'string', 'enum': ['info', 'warning', 'error'], 'default': 'info'},
         'traceId': {'type': 'string', 'format': 'uuid'},
         'operationId': {'type': 'string', 'format': 'uuid'}}}},
    {'name': 'read_server_status',
     'description': 'Read provider modes, availability and active conversation count without invoking AI or modifying server state.',
     'annotations': {'readOnlyHint': True, 'destructiveHint': False, 'openWorldHint': True},
     'inputSchema': {'type': 'object', 'additionalProperties': False, 'properties': {}}}
]


def validate(arguments):
    if not isinstance(arguments, dict) or set(arguments) - {'after', 'limit', 'level', 'traceId', 'operationId'}:
        raise DiagnosticError('Invalid log arguments.')
    query = {'limit': 100, 'level': 'info', **arguments}
    for key, minimum, maximum in [('limit', 1, 200), ('after', 0, 9_007_199_254_740_991)]:
        if key in query and (type(query[key]) is not int or not minimum <= query[key] <= maximum):
            raise DiagnosticError('Invalid log cursor or limit.')
    if query['level'] not in ('info', 'warning', 'error'):
        raise DiagnosticError('Invalid log level.')
    for key in ('traceId', 'operationId'):
        if key in query:
            try:
                value = str(uuid.UUID(query[key]))
                if value != query[key].lower():
                    raise ValueError()
                query[key] = value
            except (ValueError, TypeError, AttributeError):
                raise DiagnosticError('Invalid diagnostic identity.') from None
    return query


def dispatch(message, backend_factory=Backend):
    request_id = message.get('id') if isinstance(message, dict) else None
    def result(value):
        return {'jsonrpc': '2.0', 'id': request_id, 'result': value}
    def error(code, text):
        return {'jsonrpc': '2.0', 'id': request_id, 'error': {'code': code, 'message': text}}
    if not isinstance(message, dict) or message.get('jsonrpc') != '2.0' or not isinstance(message.get('method'), str):
        return error(-32600, 'Invalid JSON-RPC request.')
    if 'id' not in message:
        return None
    method = message['method']
    params = message.get('params', {})
    if not isinstance(params, dict):
        return error(-32602, 'Invalid parameters.')
    if method == 'initialize':
        version = params.get('protocolVersion')
        return result({'protocolVersion': version if version in PROTOCOLS else PROTOCOLS[-1],
                       'capabilities': {'tools': {}}, 'serverInfo': {'name': 'kufr-server-logs', 'version': '1.0.0'}})
    if method == 'ping':
        return result({})
    if method == 'tools/list':
        return result({'tools': TOOLS})
    if method != 'tools/call':
        return error(-32601, 'Method not found.')
    try:
        name = params.get('name')
        arguments = params.get('arguments', {})
        if name == 'read_server_logs':
            query = validate(arguments)
            value = backend_factory().read('/v1/admin/logs', query)
        elif name == 'read_server_status' and arguments == {}:
            value = backend_factory().read('/v1/admin/logs/status')
        else:
            raise DiagnosticError('Unknown tool or invalid arguments.')
        return result({'content': [{'type': 'text', 'text': json.dumps(value, ensure_ascii=False)}], 'isError': False})
    except DiagnosticError as failure:
        return result({'content': [{'type': 'text', 'text': str(failure)}], 'isError': True})


def main():
    while True:
        line = sys.stdin.buffer.readline(1_048_577)
        if not line:
            break
        if len(line) > 1_048_576:
            # Reject oversized protocol frames without accumulating their tail.
            return
        try:
            message = json.loads(line)
            response = dispatch(message)
        except (ValueError, TypeError):
            response = {'jsonrpc': '2.0', 'id': None, 'error': {'code': -32700, 'message': 'Invalid JSON.'}}
        if response is not None:
            print(json.dumps(response, ensure_ascii=False), flush=True)


def snapshot():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', required=True, type=Path)
    parser.add_argument('--after', type=int)
    parser.add_argument('--limit', type=int, default=200)
    parser.add_argument('--level', choices=('info', 'warning', 'error'), default='info')
    parser.add_argument('--trace-id')
    parser.add_argument('--operation-id')
    options = parser.parse_args()
    arguments = {'limit': options.limit, 'level': options.level}
    for key, field in [('after', 'after'), ('traceId', 'trace_id'), ('operationId', 'operation_id')]:
        if getattr(options, field) is not None:
            arguments[key] = getattr(options, field)
    try:
        query = validate(arguments)
        backend = Backend()
        result = {'capturedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  'status': backend.read('/v1/admin/logs/status'), 'logs': backend.read('/v1/admin/logs', query)}
        options.snapshot.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print('Diagnostic snapshot saved.')
    except (DiagnosticError, OSError) as error:
        print(str(error) if isinstance(error, DiagnosticError) else 'Cannot write diagnostic snapshot.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    if len(sys.argv) > 1:
        sys.exit(snapshot())
    main()
