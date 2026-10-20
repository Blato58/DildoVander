"""Explicit deployment checks; optional store verification, no conversations."""
import json
import ipaddress
import os
import uuid
import urllib.error
import urllib.request

from engine_capabilities import validate_engine_capabilities


def verify_decision_model():
    if os.environ.get('LIVE_ACTOR_MODE', 'AutonomousActor') != 'AutonomousActor':
        raise ValueError('Retired actor mode; use the retained autonomous engine')
    model = os.environ['LIVE_DECISION_MODEL']
    key = os.environ.get('OPENAI_API_KEY')
    if model not in ('gpt-6.1-sol', 'gpt-6-luna') or not key:
        raise ValueError('Configured autonomous decision model and provider credential required')
    class NoRedirects(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, url):
            return None
    opener = urllib.request.build_opener(NoRedirects())
    request = urllib.request.Request('https://api.openai.com/v1/models/' + model,
                                     headers={'Authorization': 'Bearer ' + key})
    with opener.open(request, timeout=15) as response:
        payload = response.read(32769)
        assert response.status == 200 and len(payload) <= 32768
        assert json.loads(payload)['id'] == model
    print('Autonomous Live decision model access verified: ' + model + '; no inference.')


def main():
    base = 'https://' + str(ipaddress.IPv4Address(os.environ['PUBLIC_IPV4']))
    credential = os.environ['ADMIN_CREDENTIAL']
    class NoRedirects(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, url):
            return None
    opener = urllib.request.build_opener(NoRedirects())

    def read_response(response):
        payload = response.read(262_145)
        assert len(payload) <= 262_144, 'diagnostic_response_too_large'
        return json.loads(payload)

    def call(method='GET', body=None, bearer=None, path='/v1/admin/modes'):
        headers = {'Content-Type': 'application/json'}
        if bearer:
            headers['Authorization'] = 'Bearer ' + bearer
        request = urllib.request.Request(base + path,
            data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        try:
            with opener.open(request, timeout=15) as response:
                assert response.headers.get('Cache-Control') == 'no-store'
                return response.status, read_response(response)
        except urllib.error.HTTPError as error:
            with error:
                return error.code, read_response(error)

    assert call()[0] == 401
    status, initial = call(bearer=credential)
    assert status == 200
    validate_engine_capabilities(initial['engines'])
    if os.environ.get('EXPECTED_ENGINE_READY') is not None:
        expected_ready = os.environ['EXPECTED_ENGINE_READY'] == 'true'
        assert all(engine['ready'] == expected_ready for engine in initial['engines']['engines'])
    assert initial['lifetime'] == 'until_restart'
    status, token = call('POST', bearer=credential, path='/v1/device-access/token')
    assert status == 200 and token['deviceId'] == 'debug-server-admin'
    assert call(bearer=token['accessToken'])[0] == 401
    # An automatic production check never changes modes: another device may
    # start a conversation between requests. Real transitions are unit tested.
    body = {'expectedRevision': initial['revision'], 'personalityId': initial['personalityId']}
    status, current = call('PUT', body, credential)
    assert status == 200 and current['revision'] == initial['revision']
    status, _ = call('PUT', {**body, 'expectedRevision': str(uuid.uuid4())}, credential)
    assert status == 409
    status, metadata = call(bearer=token['accessToken'], path='/v1/diagnostics')
    assert status == 200
    validate_engine_capabilities(metadata['engines'])
    assert metadata['engines'] == current['engines']
    expected_actor = os.environ.get('EXPECTED_LIVE_ACTOR_MODE')
    if expected_actor:
        assert metadata['liveActorMode'] == expected_actor
        for variable, field, convert in (
            ('EXPECTED_LIVE_DECISION_MODEL', 'liveDecisionModel', str),
            ('EXPECTED_LIVE_TERMINAL_QUIET_MS', 'liveTerminalQuietMs', int),
            ('EXPECTED_HYBRID_MEMORY', 'memoryEnabled', lambda v: v == 'true'),
            ('EXPECTED_CLOUD_INDEXING', 'cloudIndexingEnabled', lambda v: v == 'true'),
        ):
            if os.environ.get(variable):
                assert metadata[field] == convert(os.environ[variable]), field
        if os.environ.get('EXPECTED_LONG_TERM_PERSONAL') is not None:
            assert metadata['longTermPersonalEnabled'] == (os.environ['EXPECTED_LONG_TERM_PERSONAL'] == 'true')
        assert call(path='/v1/admin/memory')[0] == 401
        assert call(bearer=token['accessToken'], path='/v1/admin/memory')[0] == 401
        status, memory = call(bearer=credential, path='/v1/admin/memory')
        assert status == 200 and memory['provider'] == 'openai_vector_store'
        configured = os.environ['EXPECTED_MEMORY_CONFIGURED'] == 'true'
        assert memory['configured'] == configured
        if configured:
            # Explicit deployment/provisioning verification: two store GETs,
            # no LLM, upload, search, conversation or personal data export.
            status, memory = call('POST', bearer=credential, path='/v1/admin/memory/verify')
            assert status == 200 and memory['lastVerifiedAt'] and not memory['degraded']
        print(json.dumps({field: metadata[field] for field in (
            'liveActorMode', 'liveDecisionModel', 'liveTerminalQuietMs', 'memoryEnabled',
            'cloudIndexingEnabled', 'longTermPersonalEnabled')}, sort_keys=True))
        print('Live actor, private memory diagnostics and provisioned stores verified; no guest export.')
    print('Public admin authentication, no-op persona update, revision conflict and both engine configurations verified; provider connection not exercised.')


if __name__ == '__main__':
    main()
