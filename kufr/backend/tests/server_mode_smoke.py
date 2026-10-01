"""Explicit deployment check: mode control only, no conversations/provider calls."""
import json
import ipaddress
import os
import uuid
import urllib.error
import urllib.request


def main():
    base = 'https://' + str(ipaddress.IPv4Address(os.environ['PUBLIC_IPV4']))
    credential = os.environ['ADMIN_CREDENTIAL']
    class NoRedirects(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, url):
            return None
    opener = urllib.request.build_opener(NoRedirects())

    def call(method='GET', body=None, bearer=None, path='/v1/admin/modes'):
        headers = {'Content-Type': 'application/json'}
        if bearer:
            headers['Authorization'] = 'Bearer ' + bearer
        request = urllib.request.Request(base + path,
            data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
        try:
            with opener.open(request, timeout=15) as response:
                assert response.headers.get('Cache-Control') == 'no-store'
                return response.status, json.load(response)
        except urllib.error.HTTPError as error:
            return error.code, json.load(error)

    assert call()[0] == 401
    status, initial = call(bearer=credential)
    assert status == 200
    assert initial['dialogMode'] != 'responses' or initial['dialogAvailable']
    assert initial['voiceMode'] != 'openai' or initial['voiceAvailable']
    assert initial['lifetime'] == 'until_restart'
    status, token = call('POST', bearer=credential, path='/v1/device-access/token')
    assert status == 200 and token['deviceId'] == 'debug-server-admin'
    assert call(bearer=token['accessToken'])[0] == 401
    # An automatic production check never changes modes: another device may
    # start a conversation between requests. Real transitions are unit tested.
    body = {'expectedRevision': initial['revision'],
            'dialogMode': initial['dialogMode'], 'voiceMode': initial['voiceMode']}
    status, current = call('PUT', body, credential)
    assert status == 200 and current['revision'] == initial['revision']
    status, _ = call('PUT', {**body, 'expectedRevision': str(uuid.uuid4())}, credential)
    assert status == 409
    status, metadata = call(bearer=token['accessToken'], path='/v1/diagnostics')
    assert status == 200
    assert (metadata['dialogMode'], metadata['voiceMode']) == (current['dialogMode'], current['voiceMode'])
    print('Public admin authentication, no-op mode mutation, revision conflict and diagnostics verified; no provider calls or mode changes.')


if __name__ == '__main__':
    main()
