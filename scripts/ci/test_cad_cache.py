"""Host rollout metadata must not discard a validated, tool-identical package."""
import copy
import json
import io
from pathlib import Path
import tempfile
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from contextlib import redirect_stdout, redirect_stderr

from cad_cache import main, normalize_identity, promote_legacy_cache


def identity():
    return {'sources': {'geometry.py': 'a' * 64}, 'environment': {
        'system': 'Linux', 'machine': 'x86_64',
        'platform': 'Linux-6.11.0-azure-x86_64-with-glibc2.39',
        'ci_image': {'ImageOS': 'ubuntu24', 'ImageVersion': '20261004.327.1'},
        'python_version': '3.11.17', 'python_implementation': 'CPython',
        'distributions': [{'name': 'cadquery', 'version': '2.8.0'}],
        'slicer': {'path': '/tmp/slicer', 'executable_sha256': 'b' * 64,
                   'flatpak': {'runtime': 'gnome/50@' + 'c' * 64}},
    }}


class CadCachePolicyTests(unittest.TestCase):
    def test_key_command_emits_only_hash_despite_source_generator_progress(self):
        cache = SimpleNamespace(runtime_environment=lambda: identity()['environment'],
                                cache_key=lambda value: 'f' * 64)
        def prepare():
            print('Generated assembly guides')
            return {'sources': identity()['sources'], 'environment': cache.runtime_environment()}
        out, errors = io.StringIO(), io.StringIO()
        with patch.dict(sys.modules, {'regenerate': SimpleNamespace(prepared_identity=prepare),
                                     'regeneration_cache': cache}), \
                patch.object(sys, 'argv', ['cad_cache.py', '--cache-key']), \
                patch.object(sys, 'path', list(sys.path)), redirect_stdout(out), redirect_stderr(errors):
            main()
        self.assertEqual(out.getvalue(), 'f' * 64 + '\n')
        self.assertIn('Generated assembly guides', errors.getvalue())
        self.assertIn('CAD host diagnostics', errors.getvalue())

    def test_runner_rollout_and_kernel_only_share_identity(self):
        before = identity()
        after = copy.deepcopy(before)
        after['environment']['platform'] = 'Linux-6.14.0-azure-x86_64-with-glibc2.39'
        after['environment']['ci_image']['ImageVersion'] = '20261012.334.1'
        self.assertEqual(normalize_identity(before), normalize_identity(after))
        self.assertIn('ImageVersion', before['environment']['ci_image'])

    def test_every_real_source_tool_or_abi_change_invalidates(self):
        original = identity()
        changes = [
            ('sources', 'geometry.py', 'd' * 64),
            ('environment', 'machine', 'arm64'),
            ('environment', 'platform', 'Linux-6.14-arm64-with-glibc2.40'),
            ('environment', 'python_version', '3.11.18'),
            ('environment', 'distributions', [{'name': 'cadquery', 'version': '2.9.0'}]),
            ('environment', 'slicer', {'executable_sha256': 'e' * 64}),
        ]
        for section, field, value in changes:
            changed = copy.deepcopy(original)
            changed[section][field] = value
            with self.subTest(field=field):
                self.assertNotEqual(normalize_identity(original), normalize_identity(changed))

    def test_unknown_or_non_hosted_platform_is_rejected(self):
        for field, value in [('system', 'Windows'), ('platform', 'Linux-unknown')]:
            changed = identity()
            changed['environment'][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                normalize_identity(changed)
        changed = identity()
        changed['environment']['ci_image']['ImageOS'] = 'ubuntu26'
        with self.assertRaises(ValueError):
            normalize_identity(changed)

    def test_promotes_only_after_original_complete_validator(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            out = root / 'exports'
            out.mkdir()
            saved = identity()
            (out / 'receipt.json').write_text(json.dumps({'identity': saved}), encoding='utf-8')
            calls = []
            cache = SimpleNamespace(CACHE_NAME='receipt.json',
                check_cache=Mock(side_effect=lambda *args: calls.append('validate') or True),
                write_cache=Mock(side_effect=lambda *args: calls.append('write')))
            current = normalize_identity(saved)
            self.assertTrue(promote_legacy_cache(root, out, current, cache))
            self.assertEqual(calls, ['validate', 'write'])
            cache.check_cache.assert_called_once_with(root, out, saved)
            cache.write_cache.assert_called_once_with(root, out, current)

    def test_changed_sources_or_damaged_exports_never_promote(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            out = root / 'exports'
            out.mkdir()
            saved = identity()
            (out / 'receipt.json').write_text(json.dumps({'identity': saved}), encoding='utf-8')
            cache = SimpleNamespace(CACHE_NAME='receipt.json', check_cache=Mock(return_value=False),
                                    write_cache=Mock())
            current = normalize_identity(saved)
            changed = copy.deepcopy(current)
            changed['sources']['geometry.py'] = 'd' * 64
            self.assertFalse(promote_legacy_cache(root, out, changed, cache))
            cache.check_cache.assert_not_called()
            self.assertFalse(promote_legacy_cache(root, out, current, cache))
            cache.write_cache.assert_not_called()

    def test_escaping_cache_path_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = SimpleNamespace(CACHE_NAME='receipt.json')
            with self.assertRaises(ValueError):
                promote_legacy_cache(root / 'source', root / 'outside', normalize_identity(identity()), cache)


if __name__ == '__main__':
    unittest.main()
