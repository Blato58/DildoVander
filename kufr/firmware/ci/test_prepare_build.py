from pathlib import Path
import os
import tempfile
import unittest
from unittest.mock import Mock
import runpy

from prepare_build import prune_cache


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
