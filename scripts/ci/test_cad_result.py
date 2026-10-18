"""Fast acceptance transport tests: no CAD imports or toolchain required."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import cad_result


class TrustedResultTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.out = Path(self.directory.name)
        self.identity = {'sources': {'geometry.py': 'a' * 64}, 'tools': {'requirements.txt': 'b' * 64}}
        self.sources = {'geometry.py': 'a' * 64}
        self.write('verification.json', {'result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'sources': self.sources})
        self.write('slicer-validation.json', {'status': 'PASS_SLICER'})
        (self.out / 'part.stl').write_bytes(b'verified part')
        cad_result.publish(self.out, self.identity, self.sources)

    def write(self, name, value):
        (self.out / name).write_text(json.dumps(value), encoding='utf-8')

    def test_identical_inputs_across_commits_reuse_without_generation(self):
        self.assertTrue(cad_result.restore(self.out, copy.deepcopy(self.identity), self.sources))
        self.assertEqual(cad_result.key(self.identity), cad_result.key(copy.deepcopy(self.identity)))

    def test_changed_dimensions_validation_or_toolchain_is_a_miss(self):
        for section in ('sources', 'tools'):
            identity = copy.deepcopy(self.identity)
            identity[section]['change'] = 'c' * 64
            with self.subTest(section=section):
                self.assertFalse(cad_result.restore(self.out, identity, self.sources))

    def test_corrupt_missing_extra_and_failing_acceptance_rejected(self):
        (self.out / 'part.stl').write_bytes(b'corrupt')
        with self.assertRaises(ValueError):
            cad_result.read_receipt(self.out)
        (self.out / 'part.stl').unlink()
        with self.assertRaises(ValueError):
            cad_result.read_receipt(self.out)
        (self.out / 'part.stl').write_bytes(b'verified part')
        (self.out / 'unexpected').write_bytes(b'extra')
        with self.assertRaises(ValueError):
            cad_result.read_receipt(self.out)
        (self.out / 'unexpected').unlink()
        self.write('verification.json', {'result': 'FAIL', 'sources': self.sources})
        with self.assertRaises(ValueError):
            cad_result.publish(self.out, self.identity, self.sources)

    def test_escaping_and_incomplete_inventory_rejected(self):
        for name in ('../escape', '/absolute', 'C:/absolute', 'nested/../escape', 'a\\b'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                cad_result.safe_file(self.out, name)
        receipt = json.loads((self.out / cad_result.RECEIPT).read_bytes())
        receipt['sha256'].pop('part.stl')
        self.write(cad_result.RECEIPT, receipt)
        with self.assertRaises(ValueError):
            cad_result.read_receipt(self.out)

    def test_partial_transport_is_discarded_without_touching_source_files(self):
        source = self.out / 'source'
        exports = source / 'exports'
        exports.mkdir(parents=True)
        (source / 'geometry.py').write_bytes(b'keep')
        (exports / 'partial.step').write_bytes(b'incomplete')
        with patch.object(cad_result, 'FINAL', source), \
                patch('sys.argv', ['cad_result.py', 'discard']):
            cad_result.main()
        self.assertFalse(exports.exists())
        self.assertEqual((source / 'geometry.py').read_bytes(), b'keep')

    def test_only_presentation_transfer_runs_on_equal_manufacturing(self):
        updated = {**self.sources, 'viewer.js': 'd' * 64}
        # Real refresh is tested separately against generated package fixtures.
        import types
        def refresh(saved_sources, manufacturing_key):
            self.assertEqual(saved_sources, self.sources)
            self.assertEqual(manufacturing_key, cad_result.key(self.identity))
            self.write('verification.json', {'result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'sources': updated})
        with patch.dict('sys.modules', {'refresh_package': types.SimpleNamespace(refresh=refresh)}):
            self.assertTrue(cad_result.restore(self.out, self.identity, updated))
        self.assertEqual(cad_result.read_receipt(self.out)['sources'], updated)

    def test_validation_change_reuses_generation_but_geometry_or_tools_never_do(self):
        import types
        from unittest.mock import Mock
        changed = copy.deepcopy(self.identity)
        changed['sources']['verify.py'] = 'd' * 64
        updated = {**self.sources, 'verify.py': 'd' * 64}
        refresh = Mock(return_value=True)
        policy = types.SimpleNamespace(validation_only=lambda name: name == 'verify.py')
        with patch.dict('sys.modules', {'source_inputs': policy,
                                      'refresh_package': types.SimpleNamespace(refresh_generation=refresh)}), \
                patch.object(cad_result, 'inputs', return_value=(changed, updated)):
            self.assertTrue(cad_result.reuse_generation(self.out))
            refresh.assert_called_once_with(self.sources)
            refresh.reset_mock()
            changed['sources']['geometry.py'] = 'e' * 64
            self.assertFalse(cad_result.reuse_generation(self.out))
            refresh.assert_not_called()
            changed['sources']['geometry.py'] = self.identity['sources']['geometry.py']
            changed['tools']['requirements.txt'] = 'f' * 64
            self.assertFalse(cad_result.reuse_generation(self.out))
            refresh.assert_not_called()


if __name__ == '__main__':
    unittest.main()
