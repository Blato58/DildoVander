"""Regression: an old export tree must not contaminate the delivered ZIP."""
from pathlib import Path
import tempfile
import unittest
import zipfile
import hashlib
import json
from unittest.mock import patch
from types import SimpleNamespace
from contextlib import ExitStack, redirect_stdout
from io import StringIO

from artifacts import (ASSEMBLY_NAMES, export_names, package_files, assert_cad_hashes,
                       generated_export_names, generate_exports, GENERATION_SNAPSHOT)


def generate_fixture(root, manifest, omitted=()):
    """Exercise real snapshot generation with cheap deterministic export producers."""
    source = root / 'input.py'
    if not source.exists():
        source.write_bytes(b'current source')
    def sources():
        return {'input.py': hashlib.sha256(source.read_bytes()).hexdigest()}
    generator = SimpleNamespace(OUT=root / 'exports')
    def generate():
        current_manifest = {**manifest, 'sources': sources()}
        for name in generated_export_names(current_manifest) - set(omitted):
            path = generator.OUT / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'generated from ' + sources()['input.py'].encode('ascii'))
        (generator.OUT / 'manifest.json').write_text(json.dumps(current_manifest), encoding='utf-8')
    generator.main = generate
    generate_exports(root, root / 'exports', [generator], sources)
    return sources


def verify_fixture(root, sources, during_audit=None):
    """Run the real verification/report boundary; only expensive CAD checks are replaced."""
    import verify
    from test_verification_gate import WireAcceptanceTests
    out = root / 'exports'
    part = {'name': 'body_1', 'quantity': 1,
            'sha256': hashlib.sha256((out / 'stl/body_1.stl').read_bytes()).hexdigest()}
    def mechanical_check(path):
        path.write_text('{}', encoding='utf-8')
        if during_audit:
            during_audit()
        return {}
    with ExitStack() as stack:
        stack.enter_context(patch.multiple(verify, OUT=out, PARAMS={'revision': 'F2'}))
        stack.enter_context(patch.object(verify, 'source_hashes', side_effect=sources))
        stack.enter_context(patch.object(verify, 'export_checks', return_value=[part]))
        stack.enter_context(patch.object(verify, 'mechanical_checks', side_effect=mechanical_check))
        stack.enter_context(patch.object(verify.harness, 'validate', return_value={}))
        stack.enter_context(patch.object(verify.validate_assembly, 'validate', return_value={}))
        stack.enter_context(patch.object(verify.strength, 'calculate',
                                       return_value={'normal_load_assumption_screen_passed': True}))
        stack.enter_context(patch.object(verify.verify_harness_geometry, 'verify_all_states',
                                       return_value=WireAcceptanceTests().complete()))
        stack.enter_context(redirect_stdout(StringIO()))
        verify.main()


class PackageInventoryTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"assemblies": list(ASSEMBLY_NAMES), "parts": [{"name": "body_1"}]}

    def test_stale_exports_and_unverified_sources_are_not_packaged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            current = {"build.py", "reference/evidence.json"}
            current.update(f"exports/{name}" for name in export_names(self.manifest))
            stale = {
                "scratch.txt", "exports/assembly/old.step", "exports/images/old.png",
                "exports/old.svg", "exports/old-report.json", "exports/stl/old.stl",
                "exports/step/old.step", "exports/delivery-checks.json",
                "exports/F1-kouzelny-kufr.zip",
            }
            for name in current | stale:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("test", encoding="utf-8")
            files = package_files(root, ["build.py", "reference/evidence.json"], self.manifest)
            with zipfile.ZipFile(root / "result.zip", "w") as archive:
                for path in files:
                    archive.write(path, path.relative_to(root).as_posix())
            with zipfile.ZipFile(root / "result.zip") as archive:
                self.assertEqual(set(archive.namelist()), current)
                self.assertIsNone(archive.testzip())
            (root / "exports/images/open.png").unlink()
            with self.assertRaises(FileNotFoundError):
                package_files(root, ["build.py", "reference/evidence.json"], self.manifest)

    def test_changed_cad_bytes_are_rejected_despite_unchanged_sources(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            names=[n for n in export_names(self.manifest) if n.endswith(('.stl','.step'))]
            digest=hashlib.sha256(b'verified').hexdigest()
            for name in names:
                p=root/'exports'/name; p.parent.mkdir(parents=True,exist_ok=True); p.write_bytes(b'verified')
            report={'cad_sha256':dict.fromkeys(names,digest),'parts':[{'name':'body_1','sha256':digest}]}
            assert_cad_hashes(root,self.manifest,report)
            (root/'exports/stl/body_1.stl').write_bytes(b'corrupt')
            with self.assertRaises(ValueError):
                assert_cad_hashes(root,self.manifest,report)

    def test_wrong_or_duplicate_assembly_inventory_is_rejected(self):
        for names in (list(ASSEMBLY_NAMES[:-1]), [*ASSEMBLY_NAMES, ASSEMBLY_NAMES[0]],
                      [*ASSEMBLY_NAMES[:-1], "old_assembly"]):
            with self.subTest(names=names), self.assertRaises(ValueError):
                export_names({**self.manifest, "assemblies": names})

    def test_unsafe_or_duplicate_part_names_are_rejected(self):
        for names in (("../old",), ("body_1", "body_1"), ()):
            with self.subTest(names=names), self.assertRaises(ValueError):
                export_names({**self.manifest, "parts": [{"name": name} for name in names]})

    def test_source_path_cannot_escape_package_root(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            package_files(directory, ["../outside.py"], self.manifest)

    def test_export_or_hash_index_replaced_during_packaging_is_rejected(self):
        import package
        for changed in ('layout.svg','artifact-sha256.json'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                root=Path(directory).resolve()
                out=root/'exports'
                sources=generate_fixture(root,self.manifest)
                verify_fixture(root,sources)
                def replace_after_hash_snapshot(*args):
                    (out/changed).write_bytes(b'replacement after the accepted snapshot')
                    return package_files(*args)
                with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                     patch.object(package,'source_hashes',side_effect=sources), \
                     patch.object(package,'package_files',side_effect=replace_after_hash_snapshot):
                    with self.assertRaisesRegex(AssertionError,'ZIP readback mismatch'):
                        package.main()

    def test_non_cad_tampering_after_generation_and_verification_is_rejected(self):
        import package
        for changed in ('viewer.html','print-bom.csv','installed-hardware.csv',
                        'cable-cut-list.csv','layout.svg','images/open.png',GENERATION_SNAPSHOT):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                root=Path(directory).resolve()
                out=root/'exports'
                sources=generate_fixture(root,self.manifest)
                verify_fixture(root,sources)
                (out/changed).write_bytes(b'changed after verified snapshot')
                with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                     patch.object(package,'source_hashes',side_effect=sources):
                    with self.assertRaisesRegex(ValueError,'changed after snapshot'):
                        package.main()
                self.assertFalse((out/'F2-kouzelny-kufr.zip').exists())
                self.assertFalse((out/'artifact-sha256.json').exists())

    def test_missing_verified_export_is_rejected_before_zip_creation(self):
        import package
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            out=root/'exports'
            sources=generate_fixture(root,self.manifest)
            verify_fixture(root,sources)
            (out/'images/open.png').unlink()
            with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                 patch.object(package,'source_hashes',side_effect=sources):
                with self.assertRaises(FileNotFoundError):
                    package.main()
            self.assertFalse((out/'F2-kouzelny-kufr.zip').exists())

    def test_prior_revision_export_cannot_replace_current_verified_output(self):
        import package
        for changed in ('viewer.html','print-bom.csv','images/open.png'):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as directory:
                root=Path(directory).resolve()
                out=root/'exports'
                sources=generate_fixture(root,self.manifest)
                old_bytes=(out/changed).read_bytes()
                (root/'input.py').write_bytes(b'next revision')
                sources=generate_fixture(root,self.manifest)
                verify_fixture(root,sources)
                self.assertNotEqual((out/changed).read_bytes(),old_bytes)
                (out/changed).write_bytes(old_bytes)
                with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                     patch.object(package,'source_hashes',side_effect=sources):
                    with self.assertRaisesRegex(ValueError,'changed after snapshot'):
                        package.main()
                self.assertFalse((out/'F2-kouzelny-kufr.zip').exists())

    def test_source_change_after_verification_rejects_package(self):
        import package
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            out=root/'exports'
            sources=generate_fixture(root,self.manifest)
            verify_fixture(root,sources)
            (root/'input.py').write_bytes(b'next revision')
            with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                 patch.object(package,'source_hashes',side_effect=sources):
                with self.assertRaisesRegex(AssertionError,'Sources changed since verification'):
                    package.main()
            self.assertFalse((out/'F2-kouzelny-kufr.zip').exists())

    def test_complete_snapshot_packages_and_can_be_checked_after_unpacking(self):
        import package
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory).resolve()
            out=root/'exports'
            sources=generate_fixture(root,self.manifest)
            verify_fixture(root,sources)
            with patch.multiple(package,ROOT=root,OUT=out,PARAMS={'revision':'F2'}), \
                 patch.object(package,'source_hashes',side_effect=sources), redirect_stdout(StringIO()):
                package.main()
            unpacked=root/'unpacked'
            with zipfile.ZipFile(out/'F2-kouzelny-kufr.zip') as archive:
                archive.extractall(unpacked)
            from artifacts import assert_verified_exports
            manifest=json.loads((unpacked/'exports/manifest.json').read_bytes())
            report=json.loads((unpacked/'exports/verification.json').read_bytes())
            assert_verified_exports(unpacked/'exports',manifest,report)
            index=json.loads((unpacked/'exports/artifact-sha256.json').read_bytes())
            self.assertNotIn('artifact-sha256.json',index)
            self.assertNotIn('verification.json',report['export_sha256'])
            for name,digest in index.items():
                self.assertEqual(hashlib.sha256((unpacked/'exports'/name).read_bytes()).hexdigest(),digest)


if __name__ == "__main__":
    unittest.main()
