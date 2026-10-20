"""Deterministic transport/protocol tests; these are not real multi-agent acceptance."""
import io
import json
import os
from unittest import mock, TestCase, main
from urllib.error import HTTPError

import coordination as c


class Response(io.BytesIO):
    def __init__(self, data):
        super().__init__(json.dumps(data).encode())


class FakeIssues:
    def __init__(self):
        self.rows = [dict(number=1, title=c.HUB_TITLE, html_url='private/hub', labels=[], body=''),
                     dict(number=2, title='Backend task', html_url='private/2', labels=[], body=''),
                     dict(number=3, title='Firmware task', html_url='private/3', labels=[], body='')]
        self.messages = {1: [], 2: [], 3: []}
        self.on_write = None
        self.writes = 0

    def issues(self, state='open'):
        return self.rows

    def comments(self, issue):
        return self.messages[issue]

    def comment(self, issue, body):
        self.writes += 1
        self.messages[issue].append({'body': body})
        if self.on_write:
            callback, self.on_write = self.on_write, None
            callback()

    def request(self, path, method, data):
        assert method == 'POST' and path == c.BASE + '/issues'
        self.writes += 1
        row = dict(number=4, html_url='private/4', labels=[], **data)
        self.rows.append(row)
        return row


class ProtocolTests(TestCase):
    def setUp(self):
        self.client = FakeIssues()

    def claim(self, issue, session, scope):
        return c.claim(self.client, issue, session, 'branch-' + session, 'worktree-' + session, [scope])

    def test_discovery_is_filtered_but_retains_conflicting_claims(self):
        self.claim(2, 'A', 'backend')
        snapshot = c.discover(self.client, 'Firmware')
        self.assertEqual([3], [x['number'] for x in snapshot['tasks']])
        self.assertEqual('A', snapshot['activeClaims'][0]['session'])

    def test_independent_scopes_and_resumption(self):
        self.claim(2, 'A', 'backend')
        self.claim(3, 'B', 'firmware')
        self.claim(2, 'A', 'backend')
        self.assertEqual(2, self.client.writes)

    def test_overlapping_task_or_prefix_blocks_before_write(self):
        self.claim(2, 'A', 'firmware')
        with self.assertRaises(c.CoordinationError):
            self.claim(3, 'B', 'firmware/simulator')
        self.assertEqual(1, self.client.writes)

    def test_same_task_blocks_even_with_different_scopes(self):
        self.claim(2, 'A', 'backend')
        with self.assertRaises(c.CoordinationError):
            self.claim(2, 'B', 'firmware')

    def test_concurrent_announcement_is_detected_and_retained(self):
        event = dict(kind='CLAIM', session='B', branch='b', worktree='w', scopes=['backend'])
        self.client.on_write = lambda: self.client.messages[3].append(
            {'body': c.MARKER + '\n' + json.dumps(event)})
        with self.assertRaisesRegex(c.CoordinationError, 'Concurrent'):
            self.claim(2, 'A', 'backend')
        self.assertEqual(2, len(c.discover(self.client)['activeClaims']))
        with self.assertRaisesRegex(c.CoordinationError, 'Overlapping'):
            self.claim(2, 'A', 'backend')

    def test_handoff_allows_separate_session_without_local_state(self):
        self.claim(2, 'A', 'backend')
        self.client.comment(2, c.MARKER + '\n' + json.dumps(dict(kind='HANDOFF', session='A')))
        self.claim(2, 'B', 'backend')
        self.assertEqual(['B'], [x['session'] for x in c.active_claims(self.client.comments(2))])

    def test_stale_claim_never_expires_automatically(self):
        self.claim(2, 'A', 'backend')
        self.client.messages[2][0]['body'] = self.client.messages[2][0]['body'].replace('2026-', '2000-')
        with self.assertRaises(c.CoordinationError):
            self.claim(3, 'B', 'backend')

    def test_restarted_task_creation_reuses_server_record(self):
        first = c.ensure(self.client, 'stable-key', 'Task', 'Body')
        second = c.ensure(self.client, 'stable-key', 'Task', 'Body')
        self.assertEqual(first['number'], second['number'])
        self.assertFalse(second['created'])
        self.assertEqual(1, self.client.writes)

    def test_duplicate_keys_stop_without_creation(self):
        c.ensure(self.client, 'stable-key', 'Task', 'Body')
        self.client.rows.append(dict(self.client.rows[-1], number=5))
        with self.assertRaises(c.CoordinationError):
            c.ensure(self.client, 'stable-key', 'Task', 'Body')
        self.assertEqual(1, self.client.writes)

    def test_missing_hub_and_malformed_events_stop_claims(self):
        self.client.messages[2].append({'body': c.MARKER + '\n{}'})
        with self.assertRaises(c.CoordinationError):
            c.discover(self.client)
        self.client.rows.pop(0)
        with self.assertRaises(c.CoordinationError):
            c.discover(self.client)

    def test_scope_boundaries_reject_traversal_and_distinguish_siblings(self):
        for value in ['../outside', 'C:/outside', '/outside', '*']:
            with self.assertRaises(c.CoordinationError):
                c.normalize_scope(value)
        self.assertFalse(c.overlaps('backend', 'backend-tests'))
        self.assertTrue(c.overlaps('.', 'firmware'))

    def test_completion_requires_real_merge_exact_commit_and_explicit_acceptance(self):
        self.claim(2, 'A', 'backend')
        request = mock.Mock(return_value={'merged': False})
        self.client.request = request
        for accepted in [False, True]:
            with self.assertRaises(c.CoordinationError):
                c.complete(self.client, 2, 'A', 123, 'a' * 40, 'Actual test evidence', accepted)
        self.assertEqual(1, self.client.writes)
        request.assert_called_once_with('repos/Blato58/DildoVander/pulls/123', anonymous=True)
        request.side_effect = [dict(merged=True, merge_commit_sha='a' * 40,
                                   base={'repo': {'full_name': 'Blato58/DildoVander'}}),
                               {}, {'state': 'closed'}]
        result = c.complete(self.client, 2, 'A', 123, 'a' * 40, 'Actual test evidence', True)
        self.assertEqual('closed', result['state'])
        self.assertEqual(2, self.client.writes)


class TransportTests(TestCase):
    def test_app_credential_is_obtained_only_through_a_captured_pipe(self):
        issued = mock.Mock(returncode=0, stdout=json.dumps({'token': 'synthetic-installation'}))
        with mock.patch.dict(os.environ, {'DILDOVANDER_COORDINATION_APP_ID': '123'}, clear=True):
            with mock.patch.object(c.subprocess, 'run', return_value=issued) as run:
                self.assertEqual('synthetic-installation', c.Client().token)
                args, kwargs = run.call_args
                self.assertTrue(kwargs['capture_output'])
                self.assertEqual('1', kwargs['env']['DILDOVANDER_ISSUER_PIPE'])
                self.assertNotIn('synthetic-installation', repr(args))
            with mock.patch.object(c.subprocess, 'run', return_value=mock.Mock(returncode=2)):
                with self.assertRaises(c.CoordinationError):
                    c.Client()

    def test_missing_credential_never_uses_owner_environment(self):
        with mock.patch.dict(os.environ, {'GH_TOKEN': 'synthetic-owner'}, clear=True):
            with self.assertRaisesRegex(c.CoordinationError, 'No public/owner'):
                c.Client()

    def test_absolute_urls_and_redirects_cannot_receive_token(self):
        client = c.Client('synthetic', opener=lambda *a, **k: self.fail('network'))
        with self.assertRaises(c.CoordinationError):
            client.request('https://elsewhere.invalid/')
        with self.assertRaises(c.CoordinationError):
            c.NoRedirects().redirect_request(None, None, 302, '', {}, 'https://elsewhere.invalid')

    def test_get_retries_are_bounded_and_writes_are_not_retried(self):
        for method, expected in [('GET', 3), ('POST', 1)]:
            calls = []
            def denied(request, **kwargs):
                calls.append(request)
                raise HTTPError(request.full_url, 503, 'unavailable', {}, None)
            client = c.Client('synthetic', opener=denied, sleeper=lambda delay: None)
            with self.assertRaises(c.CoordinationError):
                client.request(c.BASE + '/issues', method, {} if method == 'POST' else None)
            self.assertEqual(expected, len(calls))

    def test_pagination_fetches_all_pages(self):
        calls = []
        def respond(request, **kwargs):
            calls.append(request.full_url)
            return Response([{}] * (100 if len(calls) == 1 else 1))
        self.assertEqual(101, len(c.Client('synthetic', opener=respond).pages(c.BASE + '/issues')))

    def test_preflight_rejects_wrong_installation_or_contents_access(self):
        for repositories, total in [([], 0), ([dict(full_name='someone/other', private=True)], 1)]:
            client = c.Client('synthetic', opener=lambda *a, **k: Response(
                {'repositories': repositories, 'total_count': total}))
            with self.assertRaises(c.CoordinationError):
                client.check()
        def overprivileged(request, **kwargs):
            if '/installation/' in request.full_url:
                return Response({'total_count': 1, 'repositories': [
                    dict(full_name=c.REPOSITORY, private=True)]})
            return Response({'files': []})
        with self.assertRaisesRegex(c.CoordinationError, 'forbidden access'):
            c.Client('synthetic', opener=overprivileged).check()


if __name__ == '__main__':
    main()
