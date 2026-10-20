import importlib.util
import csv
import os
from pathlib import Path
import shutil
import struct
import tempfile
import unittest

import ota_release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='kufr-release-test-')
        self.root = Path(self.temporary.name)
        self.image = self.root / 'firmware.bin'
        data = bytearray(512)
        data[0], data[1], data[3] = 0xe9, 1, 0x40
        struct.pack_into('<H', data, 12, 9)
        struct.pack_into('<I', data, 32, 0xabcd5432)
        data[48:53] = b'1.2.3'
        self.image.write_bytes(data)
        self.partitions = Path(__file__).resolve().parents[1] / 'partitions_16mb.csv'

    def tearDown(self):
        self.temporary.cleanup()

    def test_preserved_partitions_and_image_contract(self):
        ota_release.check_partitions(self.partitions)
        ota_release.check_image(self.image, '1.2.3')
        with self.assertRaises(ValueError):
            ota_release.check_image(self.image, '1.2.4')
        changed = self.root / 'partitions.csv'
        changed.write_text(self.partitions.read_text().replace('0x5000', '0x6000'))
        with self.assertRaises(ValueError):
            ota_release.check_partitions(changed)

    def test_version_and_size_rejects(self):
        for version in ['01.2.3', '65536.0.0', '1.2', '1.2.3-rc', '-1.2.3']:
            with self.assertRaises(ValueError):
                ota_release.check_version(version)
        with self.image.open('wb') as stream:
            stream.truncate(ota_release.SLOT_SIZE + 1)
        with self.assertRaises(ValueError):
            ota_release.check_image(self.image)

    def partition_rows(self):
        return [row for row in csv.reader(self.partitions.read_text().splitlines())
                if row and not row[0].lstrip().startswith('#')]

    def write_partition_rows(self, rows):
        changed = self.root / 'partitions.csv'
        with changed.open('w', newline='') as stream:
            csv.writer(stream).writerows(rows)
        return changed

    def test_partition_encryption_flags_require_explicit_migration(self):
        for partition in ('nvs', 'otadata', 'app0', 'app1', 'spiffs', 'coredump'):
            for flag in ('encrypted', 'readonly', 'encrypted:readonly', 'readonly:encrypted'):
                with self.subTest(partition=partition, flag=flag):
                    rows = self.partition_rows()
                    next(row for row in rows if row[0].strip() == partition)[5] = flag
                    with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
                        ota_release.check_partitions(self.write_partition_rows(rows))

    def test_blank_or_omitted_flags_preserve_current_layout(self):
        rows = self.partition_rows()
        for row in rows:
            row[5] = '   '
        ota_release.check_partitions(self.write_partition_rows(rows))
        ota_release.check_partitions(self.write_partition_rows([row[:5] for row in rows]))

    def test_extra_partition_columns_and_rows_are_rejected(self):
        for extra in ('', 'encrypted', 'ignored'):
            with self.subTest(extra=extra):
                rows = self.partition_rows()
                rows[0].append(extra)
                with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
                    ota_release.check_partitions(self.write_partition_rows(rows))
        rows = self.partition_rows()
        rows.append(rows[0].copy())
        with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
            ota_release.check_partitions(self.write_partition_rows(rows))
        rows = self.partition_rows()
        rows.append(['unreviewed', 'data', '0x40', '0x8000', '0x1000', ''])
        with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
            ota_release.check_partitions(self.write_partition_rows(rows))

    def test_invalid_partition_flags_and_short_rows_are_rejected(self):
        for flag in ('unknown', 'encrypted:', ':readonly', 'encrypted::readonly', 'encrypted:encrypted'):
            with self.subTest(flag=flag):
                rows = self.partition_rows()
                rows[0][5] = flag
                with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
                    ota_release.check_partitions(self.write_partition_rows(rows))
        rows = self.partition_rows()
        rows[0] = rows[0][:4]
        with self.assertRaisesRegex(ValueError, 'migration must be reviewed'):
            ota_release.check_partitions(self.write_partition_rows(rows))

    def test_real_development_signing_and_corruption(self):
        executable = os.environ.get('OPENSSL') or shutil.which('openssl')
        if not executable:
            self.skipTest('OpenSSL unavailable')
        key = self.root / 'nonproduction-private.pem'
        ota_release.openssl(['genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', key], executable)
        output = self.root / 'release'
        metadata = ota_release.package(self.image, self.partitions, '1.2.3', 'a' * 40,
                                       'development', key, output, executable)
        self.assertEqual(metadata, ota_release.verify(output, executable))
        self.assertFalse(any('private' in path.name for path in output.rglob('*')))
        binary = output / 'artifacts' / (metadata['sha256'] + '.bin')
        damaged = bytearray(binary.read_bytes())
        damaged[-1] ^= 1
        binary.write_bytes(damaged)
        with self.assertRaises(ValueError):
            ota_release.verify(output, executable)

    def test_publisher_open_descriptor_is_bounded(self):
        path = Path(__file__).resolve().parents[2] / 'backend' / 'deploy' / 'firmware-publish.py'
        specification = importlib.util.spec_from_file_location('publisher', path)
        publisher = importlib.util.module_from_spec(specification)
        specification.loader.exec_module(publisher)
        with self.image.open('rb') as stream:
            self.assertEqual(publisher.snapshot_descriptor(stream.fileno()), self.image.read_bytes())
        with self.image.open('wb') as stream:
            stream.truncate(ota_release.SLOT_SIZE + 1)
        with self.image.open('rb') as stream, self.assertRaises(ValueError):
            publisher.snapshot_descriptor(stream.fileno())

    def test_publisher_accepts_only_approved_runner_roots(self):
        path = Path(__file__).resolve().parents[2] / 'backend' / 'deploy' / 'firmware-publish.py'
        specification = importlib.util.spec_from_file_location('publisher_roots', path)
        publisher = importlib.util.module_from_spec(specification)
        specification.loader.exec_module(publisher)
        roots = (self.root / 'runner-main', self.root / 'runner-second')
        publisher.RUNNER_TEMPS = roots
        for root in roots:
            root.mkdir()
            image = root / 'firmware.bin'
            image.write_bytes(self.image.read_bytes())
            self.assertEqual(publisher.source_path(image), image)
            if os.name == 'posix':
                self.assertEqual(publisher.read_source(image), self.image.read_bytes())
        with self.assertRaises(ValueError):
            publisher.source_path(self.image)
        if os.name == 'posix':
            linked = self.root / 'linked-root'
            linked.symlink_to(roots[1], target_is_directory=True)
            publisher.RUNNER_TEMPS = (linked,)
            with self.assertRaises(ValueError):
                publisher.source_path(linked / 'firmware.bin')
            with self.assertRaises(OSError):
                publisher.open_directory(linked)


if __name__ == '__main__':
    unittest.main()
