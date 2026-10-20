"""CAD command orchestration checks without CAD, SSH or production changes."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import MagicMock, patch

REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('cad_commands', REPO / 'scripts/cad.py')
CAD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CAD)
from export_model_bundle import write_model_bundle
from preview_ui_fixture import fixture_payload


class CadCommandTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.bundle = Path(self.temp.name) / 'bundle'
        payload = {**fixture_payload(), 'twinManifest': None}
        self.manifest = write_model_bundle(payload, self.bundle, source_revision='test')

    def test_unchanged_active_hash_never_uploads_bundle(self):
        remote = MagicMock()
        remote.command.return_value = {'active': {'bundleHash': self.manifest['bundleHash']}}
        with patch.object(CAD, 'Remote') as factory:
            factory.return_value.__enter__.return_value = remote
            CAD.deploy(self.bundle, 'vps-dildovander', 'preview')
        remote.copy.assert_not_called()
        remote.command.assert_called_once_with('status', 'preview')

    def test_changed_bundle_uploads_snapshot_and_deploys_same_channel(self):
        remote = MagicMock()
        remote.command.side_effect = [{'active': None}, {'active': {'bundleHash': self.manifest['bundleHash']}}]
        remote.directory = '/tmp/cad-test'
        with patch.object(CAD, 'Remote') as factory:
            factory.return_value.__enter__.return_value = remote
            CAD.deploy(self.bundle, 'vps-dildovander', 'preview')
        remote.copy.assert_called_once()
        self.assertEqual(remote.command.call_args.args, ('deploy', 'preview', '/tmp/cad-test/bundle'))

    def test_production_rejected_before_ssh(self):
        with patch.object(CAD, 'Remote') as factory:
            with self.assertRaisesRegex(ValueError, 'Production requires'):
                CAD.deploy(self.bundle, 'vps-dildovander', 'production')
            factory.assert_not_called()

    def test_publish_builds_then_deploys_without_manufacturing(self):
        with patch('fast_build.build') as build, patch.object(CAD, 'deploy') as deploy, patch.object(CAD.subprocess, 'run') as run:
            CAD.main(['publish', '--force'])
        self.assertTrue(build.call_args.args[2])
        self.assertEqual(deploy.call_args.args[2], 'preview')
        run.assert_not_called()

    def test_lightweight_verify_never_runs_manufacturing(self):
        with patch('fast_build.validate') as validate, patch.object(CAD.subprocess, 'run') as run:
            CAD.main(['verify', '--bundle', str(self.bundle)])
        validate.assert_called_once_with(self.bundle.resolve())
        run.assert_not_called()

    def test_manufacturing_is_explicit_and_runs_original_engine(self):
        with patch.object(CAD.subprocess, 'run') as run:
            CAD.main(['verify', '--manufacturing', '--force'])
        self.assertEqual(run.call_args.args[0][-2:], ['regenerate.py', '--force'])
        self.assertEqual(run.call_args.kwargs['cwd'], CAD.CAD)

    def test_remote_alias_cannot_be_an_option_or_shell_command(self):
        for alias in ('-oProxyCommand=bad', 'host;echo bad', 'host name'):
            with self.assertRaises(ValueError):
                CAD.Remote(alias)

    def test_only_dedicated_manual_workflow_generates_models(self):
        folder = REPO / '.github/workflows'
        source = (folder / 'kufr-cad-generate.yml').read_text()
        self.assertIn('  workflow_dispatch:', source)
        for forbidden in ('pull_request:', 'push:', 'workflow_call:', 'schedule:', 'secrets.',
                          'regenerate.py', '--manufacturing', 'slicer', 'scripts/cad.py deploy', 'npm test'):
            self.assertNotIn(forbidden, source)
        self.assertIn('python scripts/cad.py build', source)
        for path in folder.glob('*.yml'):
            if path.name != 'kufr-cad-generate.yml':
                self.assertNotIn('scripts/cad.py', path.read_text(), path.name)


if __name__ == '__main__':
    unittest.main()
