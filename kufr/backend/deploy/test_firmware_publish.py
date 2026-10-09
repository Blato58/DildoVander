"""Regression coverage for the privileged signer's provenance boundary."""
import hashlib
import importlib.util
import os
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, Mock, patch


SPEC = importlib.util.spec_from_file_location('firmware_publish', Path(__file__).with_name('firmware-publish.py'))
PUBLISHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLISHER)


class PublisherBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.data = b'approved firmware'
        self.expected = SimpleNamespace(size_bytes=len(self.data), sha256=hashlib.sha256(self.data).hexdigest(),
                                        version='0.1.0', build_id='a' * 40, main_sha='b' * 40)

    def test_exact_verified_snapshot_is_accepted(self):
        with patch.object(PUBLISHER, 'read_source', return_value=self.data):
            self.assertEqual(PUBLISHER.verified_snapshot(Path('unused'), self.expected), self.data)

    def test_runner_cannot_substitute_same_length_firmware(self):
        with patch.object(PUBLISHER, 'read_source', return_value=b'X' * len(self.data)):
            with self.assertRaisesRegex(ValueError, 'independently verified'):
                PUBLISHER.verified_snapshot(Path('unused'), self.expected)

    def test_runner_cannot_substitute_truncated_firmware(self):
        with patch.object(PUBLISHER, 'read_source', return_value=self.data[:-1]):
            with self.assertRaises(ValueError):
                PUBLISHER.verified_snapshot(Path('unused'), self.expected)

    def test_token_only_accepts_bounded_ascii_without_whitespace(self):
        self.assertEqual(PUBLISHER.parse_token(b'ghs_ephemeral'), 'ghs_ephemeral')
        for value in (b'', b'token\n', b'token\x00', b'\xff', b'A' * 1025):
            with self.subTest(length=len(value)), self.assertRaises(ValueError):
                PUBLISHER.parse_token(value)

    def test_input_closes_and_returns_without_persisting_token(self):
        with patch.object(PUBLISHER.select, 'select', return_value=([3], [], [])), \
                patch.object(PUBLISHER.os, 'read', side_effect=[b'ghs_ephemeral', b'']):
            self.assertEqual(PUBLISHER.read_token(3), 'ghs_ephemeral')

    def test_input_without_eof_has_a_total_deadline(self):
        with patch.object(PUBLISHER.select, 'select', side_effect=[([3], [], []), ([], [], [])]), \
                patch.object(PUBLISHER.os, 'read', return_value=b'ghs_ephemeral') as read:
            with self.assertRaisesRegex(ValueError, 'timed out'):
                PUBLISHER.read_token(3)
            read.assert_called_once()

    @unittest.skipUnless(os.name == 'posix', 'Privileged helper uses Linux stdin descriptors')
    def test_real_unclosed_pipe_times_out(self):
        reader, writer = os.pipe()
        try:
            os.write(writer, b'ghs_ephemeral')
            with self.assertRaisesRegex(ValueError, 'timed out'):
                PUBLISHER.read_token(reader, timeout_seconds=0.02)
        finally:
            os.close(reader)
            os.close(writer)

    def test_bad_run_or_channel_is_rejected_before_privileged_reads(self):
        for run_id, channel in (('0', 'development'), ('1;command', 'stable'), ('12', 'other')):
            with patch.object(PUBLISHER, 'source_path') as source:
                with self.assertRaises(ValueError):
                    PUBLISHER.publish('unused', run_id, channel, 'ephemeral')
                source.assert_not_called()

    def test_unverified_artifact_never_reaches_signer(self):
        verifier = Mock(spec=['verify_artifact', 'assert_main_unchanged'])
        verifier.verify_artifact.side_effect = ValueError('rejected provenance')
        signer = Mock()
        self.invoke_rejected_publication(verifier, signer)
        signer.package.assert_not_called()
        verifier.verify_artifact.assert_called_once_with('123', 'ephemeral')

    def test_forged_runner_snapshot_never_reaches_signer(self):
        verifier = Mock(spec=['verify_artifact', 'assert_main_unchanged'])
        verifier.verify_artifact.return_value = self.expected
        signer = Mock()
        with patch.object(PUBLISHER, 'read_source', return_value=b'X' * len(self.data)):
            self.invoke_rejected_publication(verifier, signer)
        signer.package.assert_not_called()
        verifier.assert_main_unchanged.assert_not_called()

    def test_changed_main_never_promotes_a_prepared_package(self):
        self.exercise_prepared_package(main_changed=True)

    def test_publication_uses_only_independently_derived_identity(self):
        self.exercise_prepared_package(main_changed=False)

    def exercise_prepared_package(self, main_changed):
        verifier = Mock(spec=['verify_artifact', 'assert_main_unchanged'])
        verifier.verify_artifact.return_value = self.expected
        if main_changed:
            verifier.assert_main_unchanged.side_effect = ValueError('main changed')
        signer = Mock()

        def package(snapshot, partitions, version, build_id, channel, key, release):
            self.assertEqual(snapshot.read_bytes(), self.data)
            self.assertEqual((version, build_id, channel), ('0.1.0', 'a' * 40, 'development'))
            (release / 'artifacts').mkdir(parents=True)
            (release / 'manifests').mkdir()
            (release / 'artifacts' / (self.expected.sha256 + '.bin')).write_bytes(self.data)
            (release / 'manifests' / 'development.json').write_bytes(b'signed manifest')
            (release / 'trust.pem').write_bytes(b'PUBLIC')
            return {'sha256': self.expected.sha256}

        signer.package.side_effect = package
        owned = MagicMock()
        owned.parents = []
        owned.is_file.return_value = True
        owned.is_symlink.return_value = False
        owned.lstat.return_value = SimpleNamespace(st_uid=0, st_mode=0o600)
        owned.stat.return_value = SimpleNamespace(st_mode=0o600)
        owned.read_bytes.return_value = b'PUBLIC'
        keys = MagicMock()
        keys.__truediv__.return_value = owned
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / 'public'
            with patch.object(PUBLISHER, 'KEYS', keys), patch.object(PUBLISHER, 'PARTITIONS', owned), \
                    patch.object(PUBLISHER, 'DESTINATION', destination), \
                    patch.object(PUBLISHER, 'source_path', return_value=Path('unused')), \
                    patch.object(PUBLISHER, 'read_source', return_value=self.data), \
                    patch.object(PUBLISHER, 'load_owned_module', side_effect=[verifier, signer]):
                if main_changed:
                    with self.assertRaisesRegex(ValueError, 'main changed'):
                        PUBLISHER.publish('unused', '123', 'development', 'ephemeral')
                    self.assertFalse(destination.exists())
                else:
                    PUBLISHER.publish('unused', '123', 'development', 'ephemeral')
                    self.assertEqual((destination / 'artifacts' / (self.expected.sha256 + '.bin')).read_bytes(), self.data)
                    self.assertEqual((destination / 'manifests' / 'development.json').read_bytes(), b'signed manifest')
            verifier.assert_main_unchanged.assert_called_once_with('b' * 40, 'ephemeral')
            signer.package.assert_called_once()
            signer.verify.assert_called_once()

    def invoke_rejected_publication(self, verifier, signer):
        # Root configuration is outside the caller's control; emulate its stat
        # values so this boundary test runs on both Windows and Linux CI.
        owned = MagicMock()
        owned.parents = []
        owned.is_file.return_value = True
        owned.is_symlink.return_value = False
        owned.lstat.return_value = SimpleNamespace(st_uid=0, st_mode=0o600)
        owned.stat.return_value = SimpleNamespace(st_mode=0o600)
        keys = MagicMock()
        keys.__truediv__.return_value = owned
        with patch.object(PUBLISHER, 'KEYS', keys), patch.object(PUBLISHER, 'PARTITIONS', owned), \
                patch.object(PUBLISHER, 'source_path', return_value=Path('unused')), \
                patch.object(PUBLISHER, 'load_owned_module', side_effect=[verifier, signer]), \
                patch.object(PUBLISHER, 'atomic_write') as promote:
            with self.assertRaises(ValueError):
                PUBLISHER.publish('unused', '123', 'development', 'ephemeral')
            promote.assert_not_called()


if __name__ == '__main__':
    unittest.main()
