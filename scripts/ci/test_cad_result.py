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
        self.runtime = {'slicer': {'executable_sha256': 'f' * 64}, 'python_version': '3.11.test'}
        self.write('verification.json', {'result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'sources': self.sources})
        self.write('slicer-validation.json', {'status': 'PASS_SLICER'})
        (self.out / 'part.stl').write_bytes(b'verified part')
        cad_result.publish(self.out, self.identity, self.sources, self.runtime)

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
            cad_result.publish(self.out, self.identity, self.sources, self.runtime)

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
            self.assertTrue(cad_result.reuse_generation(self.out, self.runtime))
            refresh.assert_called_once_with(self.sources)
            refresh.reset_mock()
            changed['sources']['geometry.py'] = 'e' * 64
            self.assertFalse(cad_result.reuse_generation(self.out, self.runtime))
            refresh.assert_not_called()
            changed['sources']['geometry.py'] = self.identity['sources']['geometry.py']
            changed['tools']['requirements.txt'] = 'f' * 64
            self.assertFalse(cad_result.reuse_generation(self.out, self.runtime))
            refresh.assert_not_called()

    def test_different_installed_slicer_runtime_regenerates_instead_of_failing_late(self):
        import types
        from unittest.mock import Mock
        changed = copy.deepcopy(self.identity)
        changed['sources']['verify.py'] = 'd' * 64
        refresh = Mock(return_value=True)
        policy = types.SimpleNamespace(validation_only=lambda name: name == 'verify.py')
        runtime = copy.deepcopy(self.runtime)
        runtime['slicer']['executable_sha256'] = 'e' * 64
        with patch.dict('sys.modules', {'source_inputs': policy,
                                      'refresh_package': types.SimpleNamespace(refresh_generation=refresh)}), \
                patch.object(cad_result, 'inputs', return_value=(changed, {**self.sources, 'verify.py': 'd' * 64})):
            self.assertFalse(cad_result.reuse_generation(self.out, runtime))
            refresh.assert_not_called()
        self.assertTrue((self.out / 'verification.json').exists())

    def test_promotion_preserves_original_validation_and_records_server_provenance(self):
        environment = {'CAD_SOURCE_RUN': '123', 'CAD_SOURCE_ARTIFACT': '456', 'CAD_SOURCE_PR': '42',
                       'CAD_SOURCE_MERGE': 'a' * 40, 'CAD_SOURCE_BUILT': 'b' * 40, 'CAD_MAIN_SHA': 'c' * 40,
                       'CAD_SOURCE_BASIS': 'exact'}
        before = cad_result.digest(self.out / cad_result.RECEIPT)
        report = (self.out / 'verification.json').read_bytes()
        cad_result.promote(self.out, self.identity, self.sources, environment)
        saved = cad_result.read_receipt(self.out)
        self.assertEqual(saved['runtime'], self.runtime)
        self.assertEqual(saved['promotion']['original_receipt_sha256'], before)
        self.assertFalse(saved['promotion']['mechanical_validation_performed'])
        self.assertEqual(saved['promotion']['main_sha'], 'c' * 40)
        self.assertEqual(saved['promotion']['source_proof'], 'exact')
        self.assertEqual((self.out / 'verification.json').read_bytes(), report)

    def test_promotion_records_authenticated_main_inheritance_without_claiming_exact_sources(self):
        environment = {'CAD_SOURCE_RUN': '123', 'CAD_SOURCE_ARTIFACT': '456', 'CAD_SOURCE_PR': '42',
                       'CAD_SOURCE_MERGE': 'a' * 40, 'CAD_SOURCE_BUILT': 'b' * 40, 'CAD_MAIN_SHA': 'c' * 40,
                       'CAD_SOURCE_BASIS': 'inherited-main', 'CAD_SOURCE_BASE': 'd' * 40,
                       'CAD_SOURCE_AUTH_MAIN': 'e' * 40}
        for missing in ('CAD_SOURCE_BASIS', 'CAD_SOURCE_BASE', 'CAD_SOURCE_AUTH_MAIN'):
            with self.subTest(missing=missing), self.assertRaises(ValueError):
                cad_result.promote(self.out, self.identity, self.sources,
                                  {name: value for name, value in environment.items() if name != missing})
        cad_result.promote(self.out, self.identity, self.sources, environment)
        proof = cad_result.read_receipt(self.out)['promotion']
        self.assertEqual(proof['source_proof'], 'inherited-main')
        self.assertEqual(proof['original_base_sha'], 'd' * 40)
        self.assertEqual(proof['authenticated_main_sha'], 'e' * 40)
        self.assertFalse(proof['mechanical_validation_performed'])

    def test_promotion_rejects_missing_metadata_or_nonexact_identity(self):
        before = (self.out / cad_result.RECEIPT).read_bytes()
        for identity, sources in [(self.identity, self.sources), ({'other': True}, self.sources),
                                  (self.identity, {'different.py': 'a' * 64})]:
            with self.subTest(identity=identity), self.assertRaises(ValueError):
                cad_result.promote(self.out, identity, sources, {})
        self.assertEqual((self.out / cad_result.RECEIPT).read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
