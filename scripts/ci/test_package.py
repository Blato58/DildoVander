import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tarfile
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('build_package', Path(__file__).with_name('package.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / 'package'
        self.package.mkdir()
        self.destination = self.root / 'output'

    def bundle(self, entries, archive_mode=0o755):
        archive = self.package / 'payload.tar.gz'
        with tarfile.open(archive, 'w:gz') as stream:
            for name, kind in entries:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                if kind == tarfile.REGTYPE:
                    entry.size = 7
                    entry.mode = archive_mode
                    stream.addfile(entry, io.BytesIO(b'backend'))
                else:
                    entry.linkname = '/etc/passwd'
                    stream.addfile(entry)
        with archive.open('rb') as stream:
            checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
        (self.package / 'build-manifest.json').write_text(json.dumps({'payload_sha256': checksum}))

    def pack(self, event, event_name='workflow_dispatch', source_sha='a' * 40):
        event_path = self.root / 'event.json'
        event_path.write_text(json.dumps(event))
        source = self.root / 'source'
        source.mkdir(exist_ok=True)
        (source / 'backend').write_text('backend')
        environment = {'GITHUB_EVENT_PATH': str(event_path), 'GITHUB_EVENT_NAME': event_name,
                       'GITHUB_REPOSITORY': 'owner/repo', 'GITHUB_RUN_ID': '123',
                       'GITHUB_RUN_ATTEMPT': '2'}
        with patch.dict(module.os.environ, environment), patch.object(
                module, 'git_revision', side_effect=lambda ref: source_sha if ref == 'HEAD' else 'c' * 40):
            module.pack(source, self.package)
        return json.loads((self.package / 'build-manifest.json').read_text())

    def test_pack_records_exact_pr_dispatch_provenance(self):
        manifest = self.pack({'inputs': {'pr_number': '42', 'expected_head_sha': 'a' * 40,
                                         'expected_base_sha': 'd' * 40}})
        self.assertEqual(manifest['schema'], 1)
        self.assertEqual(manifest['event'], 'workflow_dispatch')
        self.assertEqual(manifest['pr_number'], 42)
        self.assertEqual(manifest['head_sha'], manifest['source_sha'])
        self.assertEqual(manifest['head_sha'], 'a' * 40)
        self.assertEqual(manifest['base_sha'], 'd' * 40)

    def test_pack_preserves_main_recovery_provenance(self):
        for inputs in [{}, {'pr_number': '', 'expected_head_sha': '', 'expected_base_sha': ''}]:
            with self.subTest(inputs=inputs):
                manifest = self.pack({'inputs': inputs})
                self.assertIsNone(manifest['pr_number'])
                self.assertIsNone(manifest['base_sha'])
                self.assertEqual(manifest['head_sha'], manifest['source_sha'])

    def test_pack_preserves_pull_request_merge_provenance(self):
        manifest = self.pack({'pull_request': {'number': 42, 'head': {'sha': 'b' * 40},
                                              'base': {'sha': 'd' * 40}}}, 'pull_request')
        self.assertEqual(manifest['source_sha'], 'a' * 40)
        self.assertEqual(manifest['head_sha'], 'b' * 40)
        self.assertEqual(manifest['base_sha'], 'd' * 40)
        self.assertEqual(manifest['pr_number'], 42)

    def test_pack_rejects_invalid_pr_dispatch_inputs(self):
        valid = {'pr_number': '42', 'expected_head_sha': 'a' * 40, 'expected_base_sha': 'd' * 40}
        for key, value in [('pr_number', ''), ('pr_number', '0'), ('pr_number', '-1'),
                           ('pr_number', '1e3'), ('pr_number', '042'), ('pr_number', 42),
                           ('pr_number', '9007199254740992'),
                           ('expected_head_sha', ''), ('expected_head_sha', 'a' * 39),
                           ('expected_head_sha', 'A' * 40), ('expected_base_sha', None),
                           ('expected_base_sha', 'g' * 40)]:
            with self.subTest(key=key, value=value):
                with self.assertRaisesRegex(ValueError, 'Invalid PR dispatch'):
                    self.pack({'inputs': {**valid, key: value}})
                self.assertFalse((self.package / 'build-manifest.json').exists())
                self.assertFalse((self.package / 'payload.tar.gz').exists())

    def test_pack_rejects_dispatch_checkout_head_mismatch(self):
        with self.assertRaisesRegex(ValueError, 'checkout does not match'):
            self.pack({'inputs': {'pr_number': '42', 'expected_head_sha': 'b' * 40,
                                  'expected_base_sha': 'd' * 40}})
        self.assertFalse((self.package / 'build-manifest.json').exists())

    def test_extracts_executable_with_permissions(self):
        for archive_mode in [0o755, 0o4755, 0o2755, 0o1755]:
            with self.subTest(archive_mode=oct(archive_mode)):
                self.bundle([('backend', tarfile.REGTYPE)], archive_mode=archive_mode)
                destination = self.destination / f'{archive_mode:o}'
                with patch.object(module.Path, 'chmod', autospec=True,
                                  side_effect=module.Path.chmod) as chmod:
                    module.unpack(self.package, destination)
                executable = destination / 'backend'
                self.assertEqual(executable.read_bytes(), b'backend')
                chmod.assert_called_once_with(executable, 0o755)
                if os.name == 'posix':
                    self.assertEqual(stat.S_IMODE(executable.stat().st_mode), 0o755)

    def test_rejects_corrupted_payload(self):
        self.bundle([('backend', tarfile.REGTYPE)])
        with (self.package / 'payload.tar.gz').open('ab') as stream:
            stream.write(b'tampered')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            module.unpack(self.package, self.destination)
        self.assertFalse(self.destination.exists())

    def test_rejects_unsafe_entries_before_writing_any_file(self):
        for name, kind in [('..', tarfile.DIRTYPE), ('../escape', tarfile.REGTYPE),
                           ('/absolute', tarfile.REGTYPE), ('link', tarfile.SYMTYPE),
                           ('hardlink', tarfile.LNKTYPE), ('device', tarfile.CHRTYPE)]:
            with self.subTest(name=name):
                self.bundle([('backend', tarfile.REGTYPE), (name, kind)])
                with self.assertRaisesRegex(ValueError, 'Unsafe'):
                    module.unpack(self.package, self.destination)
                self.assertFalse(self.destination.exists())

    def test_rejects_duplicate_entries(self):
        self.bundle([('backend', tarfile.REGTYPE), ('backend', tarfile.REGTYPE)])
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            module.unpack(self.package, self.destination)

    def test_rejects_duplicate_normalized_paths(self):
        self.bundle([('folder/backend', tarfile.REGTYPE), ('folder/./backend', tarfile.REGTYPE)])
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            module.unpack(self.package, self.destination)

    def test_requires_a_fresh_destination(self):
        self.bundle([('backend', tarfile.REGTYPE)])
        self.destination.mkdir()
        with self.assertRaises(FileExistsError):
            module.unpack(self.package, self.destination)


if __name__ == '__main__':
    unittest.main()
