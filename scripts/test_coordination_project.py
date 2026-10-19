"""Project permission, matching and partial-write regression tests."""
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest import TestCase, main, mock

from coordination_project import ProjectBoard, ProjectError, configured_project
import coordination as c


class Transport:
    def __init__(self):
        self.response_headers = {'x-oauth-scopes': 'project'}
        self.public = False
        self.cards = [{'id': 'item', 'isArchived': False, 'fieldValueByName': {'text': 'Task'}}]
        self.values = {}
        self.writes = []
        self.bad_readback = False

    def request(self, path, method='GET', data=None):
        if path == 'user':
            return {'login': 'owner'}
        query, variables = data['query'], data['variables']
        if 'projectV2(number:' in query:
            fields = [{'name': name, 'id': name} for name in ['Status', 'Agent/session', 'Verification', 'Public PR']]
            fields[0]['options'] = [{'name': name, 'id': name} for name in ['In Progress', 'Blocked', 'Ready', 'In Review', 'Done']]
            return {'data': {'user': {'projectV2': {'id': 'project', 'public': self.public, 'fields': {'nodes': fields}}}}}
        if 'items(first:100' in query:
            return {'data': {'node': {'items': {'nodes': self.cards, 'pageInfo': {'hasNextPage': False}}}}}
        if 'updateProjectV2ItemFieldValue' in query:
            self.writes.append(variables)
            self.values[variables['field']] = next(iter(variables['value'].values()))
            return {'data': {'updateProjectV2ItemFieldValue': {'projectV2Item': {'id': 'item'}}}}
        if 'fieldValues(first:50)' in query:
            values = {} if self.bad_readback else self.values
            return {'data': {'node': {'fieldValues': {'nodes': [
                {'field': {'name': name}, 'name' if name == 'Status' else 'text': value}
                for name, value in values.items()]}}}}
        raise AssertionError('Unexpected request')


class ProjectTests(TestCase):
    def setUp(self):
        self.transport = Transport()
        self.project = ProjectBoard(self.transport, 'owner', 1)

    def test_rejects_broad_scope_or_public_project(self):
        self.transport.response_headers['x-oauth-scopes'] = 'repo, project'
        with self.assertRaises(ProjectError):
            self.project.check()
        self.transport.response_headers['x-oauth-scopes'] = 'project'
        self.transport.public = True
        with self.assertRaises(ProjectError):
            self.project.check()
        self.assertEqual([], self.transport.writes)

    def test_unique_title_and_archived_cards(self):
        self.transport.cards.append({'id': 'old', 'isArchived': True, 'fieldValueByName': {'text': 'Task'}})
        self.assertEqual('item', self.project.resolve({'title': 'Task'}))
        self.transport.cards[-1]['isArchived'] = False
        with self.assertRaises(ProjectError):
            self.project.resolve({'title': 'Task'})
        with self.assertRaises(ProjectError):
            self.project.resolve({'title': 'Different task'})
        self.assertEqual([], self.transport.writes)

    def test_status_and_evidence_are_read_back(self):
        self.project.check()
        result = self.project.update('item', 'Blocked', 'session', 'Needs hardware', 'https://example.test/pr')
        self.assertTrue(result['verified'])
        self.assertEqual('Blocked', self.transport.values['Status'])
        self.assertEqual('session', self.transport.values['Agent/session'])
        self.assertEqual(4, len(self.transport.writes))

    def test_missing_option_and_readback_failure_are_not_success(self):
        self.project.check()
        with self.assertRaises(ProjectError):
            self.project.update('item', 'Unknown')
        self.assertEqual([], self.transport.writes)
        self.transport.bad_readback = True
        with self.assertRaises(ProjectError):
            self.project.update('item', 'Ready')

    def test_explicit_configuration_has_no_owner_token_fallback(self):
        with mock.patch.dict(os.environ, {'GH_TOKEN': 'owner-synthetic'}, clear=True):
            self.assertIsNone(configured_project(mock.Mock()))
        with mock.patch.dict(os.environ, {'DILDOVANDER_COORDINATION_PROJECT_NUMBER': '1',
                                         'GH_TOKEN': 'owner-synthetic'}, clear=True):
            with self.assertRaises(ProjectError):
                configured_project(mock.Mock())

    def test_dedicated_token_passed_only_to_project_transport(self):
        values = {'DILDOVANDER_COORDINATION_PROJECT_NUMBER': '1',
                  'DILDOVANDER_COORDINATION_PROJECT_OWNER': 'owner',
                  'DILDOVANDER_COORDINATION_PROJECT_TOKEN': 'project-synthetic'}
        factory = mock.Mock(return_value=self.transport)
        with mock.patch.dict(os.environ, values, clear=True):
            self.assertIsInstance(configured_project(factory), ProjectBoard)
        factory.assert_called_once_with(token='project-synthetic')

    def test_dpapi_command_is_valid_and_uses_captured_output(self):
        factory = mock.Mock(return_value=self.transport)
        values = {'DILDOVANDER_COORDINATION_PROJECT_NUMBER': '1',
                  'DILDOVANDER_COORDINATION_PROJECT_TOKEN_FILE': 'encrypted.dpapi'}
        with mock.patch.dict(os.environ, values, clear=True), mock.patch(
                'coordination_project.subprocess.run', return_value=SimpleNamespace(
                    returncode=0, stdout='project-synthetic')) as run:
            configured_project(factory)
        command = run.call_args.args[0][-1]
        self.assertTrue(command.startswith('$ErrorActionPreference'))
        self.assertIn('.Trim()', command)
        self.assertTrue(run.call_args.kwargs['capture_output'])
        factory.assert_called_once_with(token='project-synthetic')


class CommandTests(TestCase):
    def setUp(self):
        self.client = mock.Mock()
        self.client.check.return_value = {}
        self.client.request.return_value = {'title': 'Task', 'state': 'open'}
        self.client.comment.return_value = {'html_url': 'private/comment'}
        self.board = mock.Mock()
        self.board.check.return_value = {'configured': True}
        self.board.resolve.return_value = 'item'
        self.board.update.return_value = {'verified': True}
        self.stack = []
        for target, value in [('load_saved_configuration', None), ('Client', self.client),
                              ('configured_project', self.board)]:
            patcher = mock.patch.object(c, target, return_value=value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def invoke(self, arguments):
        with mock.patch.object(sys, 'argv', ['coordination.py', *arguments]), mock.patch('builtins.print'):
            c.main()

    def test_claim_sync_occurs_only_after_successful_claim(self):
        args = ['claim', '2', '--session', 'A', '--branch', 'branch', '--worktree', 'tree', '--scope', 'scripts']
        with mock.patch.object(c, 'claim', return_value={}):
            self.invoke(args)
        self.board.update.assert_called_once_with('item', 'In Progress', 'A')
        self.board.update.reset_mock()
        with mock.patch.object(c, 'claim', side_effect=c.CoordinationError('overlap')):
            with self.assertRaises(c.CoordinationError):
                self.invoke(args)
        self.board.update.assert_not_called()

    def test_release_requires_own_claim_and_done_means_review(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence = Path(directory)/'evidence.txt'
            evidence.write_text('Software ready; external acceptance remains.')
            args = ['release', '2', '--session', 'A', '--kind', 'DONE', '--body-file', str(evidence)]
            with mock.patch.object(c, 'discover', return_value={'activeClaims': [{'issue': 2, 'session': 'B'}]}):
                with self.assertRaises(c.CoordinationError):
                    self.invoke(args)
            self.client.comment.assert_not_called()
            with mock.patch.object(c, 'discover', return_value={'activeClaims': [{'issue': 2, 'session': 'A'}]}):
                self.invoke(args)
            self.assertEqual('In Review', self.board.update.call_args.args[1])


if __name__ == '__main__':
    main()
