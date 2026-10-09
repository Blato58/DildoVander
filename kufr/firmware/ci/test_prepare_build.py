from pathlib import Path
import os
import tempfile
import unittest
from unittest.mock import Mock
import runpy

from prepare_build import cache_namespace, prune_cache, prune_caches


class CacheNamespaceTests(unittest.TestCase):
    def test_pin_package_version_and_compiler_replacement_invalidate_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            firmware = root / "firmware"
            firmware.mkdir()
            pin = firmware / "platformio.ini"
            pin.write_text("platform=espressif32@7.1.3")
            core = root / "core"
            package = core / "packages/toolchain-xtensa-esp-elf"
            (package / "bin").mkdir(parents=True)
            manifest = package / "package.json"
            manifest.write_text('{"version":"15.2.0"}')
            compiler = package / "bin/xtensa-esp32s3-elf-gcc"
            compiler.write_bytes(b"compiler version one")
            original = cache_namespace(firmware, core)
            self.assertEqual(original, cache_namespace(firmware, core))
            for path, replacement in (
                (pin, b"platform=espressif32@7.1.4"),
                (manifest, b'{"version":"15.2.1"}'),
                (compiler, b"compiler version two"),
            ):
                with self.subTest(path=path):
                    previous = path.read_bytes()
                    path.write_bytes(replacement)
                    self.assertNotEqual(original, cache_namespace(firmware, core))
                    path.write_bytes(previous)
            lock = firmware / "dependencies.lock"
            lock.write_text("esp-idf: 6.1.1")
            self.assertNotEqual(original, cache_namespace(firmware, core))

    def test_archive_tools_and_wrappers_invalidate_cache_when_replaced(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            firmware = root / "firmware"
            firmware.mkdir()
            core = root / "core"
            binary = core / "packages/toolchain-xtensa-esp-elf/bin"
            binary.mkdir(parents=True)
            for tool in ("gcc-ar", "gcc-ranlib", "ar", "ranlib"):
                with self.subTest(tool=tool):
                    path = binary / f"xtensa-esp32s3-elf-{tool}"
                    path.write_bytes(b"original")
                    original = cache_namespace(firmware, core)
                    path.write_bytes(b"replacement")
                    self.assertNotEqual(original, cache_namespace(firmware, core))


class TimestampCacheTests(unittest.TestCase):
    def test_relative_and_absolute_source_paths_keep_all_timestamp_objects_fresh(self):
        for path in (
            "build/src/features/backend_access/EspIdfBackendNetwork.cpp.o",
            "build/EspIdfBackendNetwork.cpp.o",
        ):
            with self.subTest(path=path):
                network = Path(path)
                env = Mock()
                env.get.return_value = [[Path("other.cpp.o")], [network]]
                env.Flatten.side_effect = lambda groups: [node for group in groups for node in group]
                env.subst.return_value = str(Path(__file__).parent / "missing-src")
                descriptors = [Mock(), Mock()]
                env.File.side_effect = descriptors
                runpy.run_path(str(Path(__file__).with_name("build_cache.py")), {
                    "env": env, "Import": Mock()
                })
                refreshed = [call.args[0] for call in env.AlwaysBuild.call_args_list]
                uncached = [call.args[0] for call in env.NoCache.call_args_list]
                self.assertEqual(refreshed, [network] + descriptors)
                self.assertEqual(uncached, refreshed)

    def test_missing_descriptor_builder_rejects_stale_cache_configuration(self):
        env = Mock()
        env.get.return_value = []
        env.Flatten.return_value = []
        env.subst.return_value = str(Path(__file__).parent / "missing-src")
        env.File.return_value.has_builder.return_value = False
        with self.assertRaisesRegex(ValueError, "Timestamp object is missing"):
            runpy.run_path(str(Path(__file__).with_name("build_cache.py")), {
                "env": env, "Import": Mock()
            })


class CachePruningTests(unittest.TestCase):
    def test_limit_is_shared_across_toolchain_namespaces(self):
        with tempfile.TemporaryDirectory() as directory:
            caches = [Path(directory) / "old", Path(directory) / "new"]
            objects = []
            for index, cache in enumerate(caches):
                bucket = cache / "AA"
                bucket.mkdir(parents=True)
                obj = bucket / ("a" * 32)
                obj.write_bytes(b"1234")
                os.utime(obj, (index + 1, index + 1))
                objects.append(obj)
            prune_caches(caches, limit=4)
            self.assertFalse(objects[0].exists())
            self.assertTrue(objects[1].exists())

    def test_stale_interrupted_writes_are_removed_in_both_scons_formats(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            bucket = cache / "AA"
            bucket.mkdir()
            old_file = bucket / ("a" * 32 + ".tmp12345678-1234-1234-1234-123456789012")
            old_file.write_bytes(b"old")
            old_dir = bucket / "tmp12345678"
            old_dir.mkdir()
            old_object = old_dir / ("b" * 32)
            old_object.write_bytes(b"old")
            for path in (old_file, old_object, old_dir):
                os.utime(path, (1, 1))
            prune_cache(cache, now=3602)
            self.assertFalse(old_file.exists())
            self.assertFalse(old_dir.exists())

    def test_recent_writes_count_toward_limit_and_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            bucket = cache / "AA"
            bucket.mkdir()
            obj = bucket / ("a" * 32)
            obj.write_bytes(b"1234")
            temporary = bucket / ("b" * 32 + ".tmp" + "c" * 32)
            temporary.write_bytes(b"5678")
            os.utime(temporary, (4000, 4000))
            prune_cache(cache, limit=4, now=4001)
            self.assertFalse(obj.exists())
            self.assertTrue(temporary.exists())
            with self.assertRaisesRegex(ValueError, "Recent cache writes exceed"):
                prune_cache(cache, limit=0, now=4001)
            self.assertTrue(temporary.exists())

    def test_oldest_objects_are_pruned_and_signature_database_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            bucket = cache / "AB"
            bucket.mkdir()
            oldest = bucket / ("a" * 32)
            newest = bucket / ("b" * 32)
            oldest.write_bytes(b"1234")
            newest.write_bytes(b"5678")
            os.utime(oldest, (1, 1))
            os.utime(newest, (2, 2))
            signatures = cache / ".sconsign312.dblite"
            signatures.write_bytes(b"keep")
            prune_cache(cache, limit=4)
            self.assertFalse(oldest.exists())
            self.assertEqual(newest.read_bytes(), b"5678")
            self.assertEqual(signatures.read_bytes(), b"keep")

    def test_other_files_and_nested_directories_are_not_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            cache = Path(directory)
            bucket = cache / "00"
            bucket.mkdir()
            unrelated = bucket / "notes.txt"
            unrelated.write_text("keep")
            (bucket / ("c" * 32)).mkdir()
            prune_cache(cache, limit=0)
            self.assertEqual(unrelated.read_text(), "keep")
            self.assertTrue((bucket / ("c" * 32)).is_dir())

    def test_symlinked_buckets_and_objects_are_not_followed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root / "cache"
            cache.mkdir()
            outside = root / "outside"
            outside.mkdir()
            source = outside / ("d" * 32)
            source.write_bytes(b"keep")
            try:
                (cache / "AA").symlink_to(outside, target_is_directory=True)
                bucket = cache / "BB"
                bucket.mkdir()
                (bucket / ("e" * 32)).symlink_to(source)
            except OSError:
                self.skipTest("Creating symlinks is not permitted")
            prune_cache(cache, limit=0)
            self.assertEqual(source.read_bytes(), b"keep")
            with self.assertRaises(ValueError):
                prune_cache(cache / "AA", limit=0)


if __name__ == "__main__":
    unittest.main()
