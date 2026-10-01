import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest

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

    def bundle(self, entries):
        archive = self.package / 'payload.tar.gz'
        with tarfile.open(archive, 'w:gz') as stream:
            for name, kind in entries:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                if kind == tarfile.REGTYPE:
                    entry.size = 7
                    entry.mode = 0o755
                    stream.addfile(entry, io.BytesIO(b'backend'))
                else:
                    entry.linkname = '/etc/passwd'
                    stream.addfile(entry)
        with archive.open('rb') as stream:
            checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
        (self.package / 'build-manifest.json').write_text(json.dumps({'payload_sha256': checksum}))

    def test_extracts_executable_with_permissions(self):
        self.bundle([('backend', tarfile.REGTYPE)])
        module.unpack(self.package, self.destination)
        self.assertEqual((self.destination / 'backend').read_bytes(), b'backend')
        self.assertEqual((self.destination / 'backend').stat().st_mode & 0o777, 0o755)

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
