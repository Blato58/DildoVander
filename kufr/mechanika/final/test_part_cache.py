"""Exercise incremental CAD exports without loading CadQuery or constructing CAD."""
import json
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import part_cache


class ShapeStub:
    def __init__(self, content=b"BREP geometry"):
        self.content = content
        self.wrapped = SimpleNamespace(Checked=lambda value: None)

    def exportBrep(self, stream):
        stream.write(self.content)
        return bool(self.content)

    def copy(self, mesh=False):
        if mesh:
            raise ValueError("Cache identity must not include tessellation")
        return ShapeStub(self.content)

    def __iter__(self):
        return iter(())


class PartCacheTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.write("geometry.py", "PARAMS = {}\n")
        self.write("mechanism.py", "from geometry import PARAMS\nMOUNT = 1\n")
        self.write("chassis.py", "from mechanism import MOUNT\n")
        self.write("layout.py", "from geometry import PARAMS\nimport flex\n")
        self.write("flex.py", "def route(): return 1\n")
        self.write("build.py", "import chassis, mechanism, layout\nTOLERANCE = .03\n")
        self.write("part_cache.py", "CACHE_SCHEMA = 1\n")
        self.write("parameters.json", '{"length": 1}')
        self.write("requirements.txt", "cadquery==2.8.0\n")
        self.runtime = {"python": "3.test", "cadquery": "2.8.0"}
        self.part = SimpleNamespace(name="plate", rotation=None, quantity=1, reason="fixture", shape=ShapeStub())
        self.out = self.root / "staging"
        self.out.mkdir()
        for suffix in part_cache.FORMATS:
            (self.out / suffix).mkdir()
            (self.out / suffix / ("plate." + suffix)).write_bytes(("original " + suffix).encode())

    def write(self, name, value):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value, encoding="utf-8")

    def cache(self, force=False, runtime=None):
        cache = part_cache.PartExportCache(self.root, runtime or self.runtime,
                                          ("chassis", "mechanism", "layout"), force=force)
        self.addCleanup(self.cleanup_cache, cache)
        return cache

    @staticmethod
    def cleanup_cache(cache):
        if cache.staging is not None:
            cache.staging.cleanup()

    def keys(self):
        cache = self.cache()
        return {owner: cache.key(owner, self.part) for owner in cache.groups}

    def publish(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        cache.publish()
        return cache, key

    def erase_exports(self):
        for suffix in part_cache.FORMATS:
            (self.out / suffix / ("plate." + suffix)).unlink()

    def receipt(self, cache, key):
        return json.loads((cache.directory / key / "receipt.json").read_bytes())

    def test_exact_hit_restores_all_three_exports(self):
        cache, key = self.publish()
        self.erase_exports()
        self.assertTrue(cache.restore(key, "plate", self.out))
        for suffix in part_cache.FORMATS:
            self.assertEqual((self.out / suffix / ("plate." + suffix)).read_bytes(),
                             ("original " + suffix).encode())

    def test_part_change_invalidates_its_owner_and_transitive_users(self):
        before = self.keys()
        self.write("mechanism.py", "from geometry import PARAMS\nMOUNT = 2\n")
        after = self.keys()
        self.assertNotEqual(before["mechanism"], after["mechanism"])
        self.assertNotEqual(before["chassis"], after["chassis"])
        self.assertEqual(before["layout"], after["layout"])

    def test_local_helper_change_invalidates_only_its_users(self):
        before = self.keys()
        self.write("flex.py", "def route(): return 2\n")
        after = self.keys()
        self.assertNotEqual(before["layout"], after["layout"])
        self.assertEqual(before["chassis"], after["chassis"])
        self.assertEqual(before["mechanism"], after["mechanism"])

    def test_shared_sources_parameters_and_recipe_invalidate_every_owner(self):
        for name in ("geometry.py", "build.py", "part_cache.py", "parameters.json", "requirements.txt"):
            with self.subTest(name=name):
                before = self.keys()
                with (self.root / name).open("a", encoding="utf-8") as stream:
                    stream.write("\n" if name.endswith(".json") else "\n# change\n")
                after = self.keys()
                self.assertTrue(all(before[owner] != after[owner] for owner in before))

    def test_runtime_and_part_metadata_are_key_inputs(self):
        cache = self.cache()
        before = cache.key("layout", self.part)
        changed = self.cache(runtime={**self.runtime, "cadquery": "different"})
        self.assertNotEqual(before, changed.key("layout", self.part))
        for name, value in (("rotation", ((0, 0, 1), 90)), ("quantity", 2), ("reason", "new")):
            part = SimpleNamespace(**{**vars(self.part), name: value})
            self.assertNotEqual(before, cache.key("layout", part))

    def test_non_geometry_ui_changes_do_not_invalidate_exports(self):
        before = self.keys()
        self.write("viewer.js", "console.log('new presentation');")
        self.write("README.md", "new explanation")
        self.assertEqual(before, self.keys())

    def test_live_geometry_changes_invalidate_even_when_sources_are_unchanged(self):
        cache = self.cache()
        original = cache.key("layout", self.part, ShapeStub(b"first actual shape"))
        changed = cache.key("layout", self.part, ShapeStub(b"second actual shape"))
        self.assertNotEqual(original, changed)

    def test_failed_brep_serialization_cannot_create_a_reusable_key(self):
        with self.assertRaisesRegex(ValueError, "fingerprint"):
            self.cache().key("layout", self.part, ShapeStub(b""))

    def test_all_data_inputs_conservatively_invalidate_exports(self):
        before = self.keys()
        self.write("reference/component.json", '{"size": 2}')
        self.assertTrue(all(before[key] != value for key, value in self.keys().items()))

    def test_package_initializers_and_nested_relative_imports_are_included(self):
        self.write("layout.py", "from package.part import shape\n")
        self.write("package/__init__.py", "ENABLED = True\n")
        self.write("package/part.py", "from .helper import shape\n")
        self.write("package/helper.py", "shape = 1\n")
        closure = part_cache.source_closure(self.root, "layout")
        self.assertTrue({"package/__init__.py", "package/part.py", "package/helper.py"} <= set(closure))

    def test_import_cycles_and_imports_inside_functions_are_included(self):
        self.write("layout.py", "def parts():\n import helper\n return helper.value\n")
        self.write("helper.py", "import layout\nvalue = 1\n")
        self.assertIn("helper.py", part_cache.source_closure(self.root, "layout"))

    def test_missing_and_malformed_receipts_are_misses(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        self.assertFalse(cache.restore(key, "plate", self.out))
        entry = cache.directory / key
        entry.mkdir(parents=True)
        for content in (b"not json", b"[]", b"{}", b'{"schema": 1, "files": null}'):
            (entry / "receipt.json").write_bytes(content)
            self.assertFalse(cache.restore(key, "plate", self.out))

    def test_missing_or_corrupt_any_format_is_a_complete_miss(self):
        for suffix in part_cache.FORMATS:
            with self.subTest(suffix=suffix):
                cache, key = self.publish()
                digest = self.receipt(cache, key)["files"][suffix]["sha256"]
                source = cache.directory / key / (digest + "." + suffix)
                source.write_bytes(b"broken")
                self.assertFalse(cache.restore(key, "plate", self.out))
                source.unlink()
                self.assertFalse(cache.restore(key, "plate", self.out))
                for fmt in part_cache.FORMATS:
                    self.assertEqual((self.out / fmt / ("plate." + fmt)).read_bytes(),
                                     ("original " + fmt).encode())

    def test_force_bypasses_an_otherwise_valid_entry(self):
        _, key = self.publish()
        self.assertFalse(self.cache(force=True).restore(key, "plate", self.out))

    def test_same_size_corruption_is_a_miss(self):
        cache, key = self.publish()
        digest = self.receipt(cache, key)["files"]["stl"]["sha256"]
        source = cache.directory / key / (digest + ".stl")
        source.write_bytes(b"X" * source.stat().st_size)
        self.assertFalse(cache.restore(key, "plate", self.out))

    def test_copy_time_corruption_does_not_restore_any_outputs(self):
        cache, key = self.publish()
        self.erase_exports()
        original_copy = part_cache.shutil.copyfile

        def corrupt_copy(source, destination):
            original_copy(source, destination)
            Path(destination).write_bytes(b"changed during copy")

        with patch.object(part_cache.shutil, "copyfile", side_effect=corrupt_copy):
            self.assertFalse(cache.restore(key, "plate", self.out))
        self.assertFalse(any((self.out / suffix / ("plate." + suffix)).exists()
                             for suffix in part_cache.FORMATS))

    def test_export_snapshots_precede_slicer_overwrite_and_survive_output_deletion(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        (self.out / "3mf/plate.3mf").write_bytes(b"slicer printer project")
        self.erase_exports()
        cache.publish()
        self.assertTrue(cache.restore(key, "plate", self.out))
        self.assertEqual((self.out / "3mf/plate.3mf").read_bytes(), b"original 3mf")

    def test_unpublished_exports_never_create_a_hit(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        self.assertFalse(self.cache().restore(key, "plate", self.out))

    def test_source_change_during_build_prevents_cache_publication(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        self.write("flex.py", "value = 3\n")
        with self.assertRaisesRegex(ValueError, "inputs changed"):
            cache.publish()
        self.assertFalse((cache.directory / key / "receipt.json").exists())

    def test_incomplete_export_failure_does_not_publish_receipt(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        snapshot = cache.pending[0][2]
        (snapshot / "3mf/plate.3mf").unlink()
        with self.assertRaises(FileNotFoundError):
            cache.publish()
        self.assertFalse((cache.directory / key / "receipt.json").exists())

    def test_receipt_replace_failure_does_not_publish_partial_entry(self):
        cache = self.cache()
        key = cache.key("layout", self.part)
        cache.remember(key, "plate", self.out)
        original_replace = part_cache.os.replace

        def fail_receipt(source, destination):
            if Path(destination).name == "receipt.json":
                raise OSError("simulated crash")
            original_replace(source, destination)

        with patch.object(part_cache.os, "replace", side_effect=fail_receipt):
            with self.assertRaisesRegex(OSError, "simulated crash"):
                cache.publish()
        self.assertFalse(cache.restore(key, "plate", self.out))

    def test_invalid_names_and_receipt_paths_cannot_escape_cache(self):
        cache, key = self.publish()
        with self.assertRaises(ValueError):
            cache.restore(key, "../plate", self.out)
        with self.assertRaises(ValueError):
            cache.key("layout", SimpleNamespace(**{**vars(self.part), "name": "../plate"}))
        receipt = self.receipt(cache, key)
        receipt["files"]["stl"]["sha256"] = "../outside"
        (cache.directory / key / "receipt.json").write_text(json.dumps(receipt))
        self.assertFalse(cache.restore(key, "plate", self.out))

    def test_successful_publication_prunes_only_obsolete_cache_entry_directories(self):
        previous, old_key = self.publish()
        unrelated = previous.directory / "keep-not-a-cache-entry"
        unrelated.mkdir()
        (unrelated / "important.txt").write_bytes(b"unrelated")
        hexadecimal_file = previous.directory / ("a" * 64)
        hexadecimal_file.write_bytes(b"not an entry directory")
        cache = self.cache()
        current_key = cache.key("layout", self.part, ShapeStub(b"new shape"))
        cache.remember(current_key, "plate", self.out)
        self.assertTrue((cache.directory / old_key).exists())
        cache.publish()
        self.assertFalse((cache.directory / old_key).exists())
        self.assertTrue((cache.directory / current_key / "receipt.json").is_file())
        self.assertEqual((unrelated / "important.txt").read_bytes(), b"unrelated")
        self.assertTrue(hexadecimal_file.is_file())

    def test_successful_hit_is_retained_while_unused_entries_are_pruned(self):
        previous, key = self.publish()
        obsolete = previous.directory / ("b" * 64)
        obsolete.mkdir()
        (obsolete / "old.stl").write_bytes(b"old mesh")
        cache = self.cache()
        self.assertTrue(cache.restore(key, "plate", self.out))
        cache.publish()
        self.assertTrue((cache.directory / key / "receipt.json").is_file())
        self.assertFalse(obsolete.exists())

    def test_failed_publication_preserves_existing_cache_entries(self):
        previous, old_key = self.publish()
        cache = self.cache()
        current_key = cache.key("layout", self.part, ShapeStub(b"new shape"))
        cache.remember(current_key, "plate", self.out)
        (cache.pending[0][2] / "3mf/plate.3mf").unlink()
        with self.assertRaises(FileNotFoundError):
            cache.publish()
        self.assertTrue((previous.directory / old_key / "receipt.json").is_file())

    def test_pruning_skips_symlinked_entry_directories(self):
        cache, key = self.publish()
        outside = self.root / "outside-cache"
        outside.mkdir()
        (outside / "important.txt").write_bytes(b"preserve")
        link = cache.directory / ("c" * 64)
        try:
            link.symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest("Creating symlinks is unavailable in this environment")
        cache.publish()
        self.assertTrue(link.is_symlink())
        self.assertEqual((outside / "important.txt").read_bytes(), b"preserve")
        self.assertTrue((cache.directory / key / "receipt.json").is_file())

    @unittest.skipUnless(importlib.util.find_spec("cadquery"), "Requires the existing CAD environment")
    def test_real_brep_is_stable_across_processes_and_exports_round_trip(self):
        import cadquery as cq
        # Exercise the real native serialization boundary on a small fixture,
        # never the suitcase assembly or its expensive verification pipeline.
        script = ("import cadquery as cq,hashlib; from io import BytesIO; "
                  "s=BytesIO(); cq.Solid.makeBox(3,4,5).exportBrep(s); "
                  "print(hashlib.sha256(s.getvalue()).hexdigest())")
        fingerprints = [subprocess.check_output([sys.executable, "-I", "-c", script],
                        cwd=self.root, text=True, timeout=120).strip() for _ in range(2)]
        self.assertEqual(fingerprints[0], fingerprints[1])
        self.part.shape = cq.Solid.makeBox(3, 4, 5)
        checked = [face.wrapped.Checked() for face in self.part.shape.Faces()]
        unmeshed_key = self.cache().key("layout", self.part)
        self.assertEqual([face.wrapped.Checked() for face in self.part.shape.Faces()], checked)
        for suffix in part_cache.FORMATS:
            cq.exporters.export(self.part.shape, str(self.out / suffix / ("plate." + suffix)))
        cache, key = self.publish()
        self.assertEqual(key, unmeshed_key, "Native tessellation must not invalidate identical geometry")
        original = {suffix: (self.out / suffix / ("plate." + suffix)).read_bytes()
                    for suffix in part_cache.FORMATS}
        self.erase_exports()
        self.assertTrue(cache.restore(key, "plate", self.out))
        self.assertEqual(original, {suffix: (self.out / suffix / ("plate." + suffix)).read_bytes()
                                  for suffix in part_cache.FORMATS})
        restored = cq.importers.importStep(str(self.out / "step/plate.step")).val()
        self.assertTrue(restored.isValid())
        self.assertAlmostEqual(restored.Volume(), 60)


if __name__ == "__main__":
    unittest.main()
