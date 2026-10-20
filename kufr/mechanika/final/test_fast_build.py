"""Generation cache acceptance with small data; never constructs full CAD."""
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import fast_build
from preview_ui_fixture import fixture_payload
from test_part_cache import ShapeStub


class FastBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cache, self.output = self.root / "cache", self.root / "output"
        self.identity = {"schema": 1, "sources": {"geometry": "one"}, "runtime": {"python": "test"}}
        self.factory = lambda mesh: {**fixture_payload(), "twinManifest": None}
        self.patch = patch.object(fast_build, "identity", return_value=self.identity)
        self.patch.start()
        self.addCleanup(self.patch.stop)

    def build(self, **kwargs):
        return fast_build.build(self.cache, self.output, payload_factory=self.factory, **kwargs)

    def test_second_build_skips_geometry_and_preserves_exact_bundle(self):
        first = self.build()
        self.factory = lambda mesh: self.fail("Cache hit constructed geometry")
        second = self.build()
        self.assertFalse(first["cacheHit"])
        self.assertTrue(second["cacheHit"])
        self.assertEqual(first["bundleHash"], second["bundleHash"])
        self.assertEqual(second["verification"], "not-verified")

    def test_force_and_changed_sources_rebuild(self):
        self.build()
        self.assertFalse(self.build(force=True)["cacheHit"])
        self.identity["sources"]["geometry"] = "two"
        self.assertFalse(self.build()["cacheHit"])

    def test_corrupt_cache_rebuilds(self):
        self.build()
        cached = next((self.cache / "builds").glob("*/model.json"))
        cached.write_text("{}")
        self.assertFalse(self.build()["cacheHit"])

    def test_sources_change_during_build_preserves_previous_output(self):
        self.build()
        original = (self.output / "model-bundle.json").read_bytes()
        with patch.object(fast_build, "identity", side_effect=[self.identity, {"changed": True}]):
            with self.assertRaisesRegex(ValueError, "sources changed"):
                self.build(force=True)
        self.assertEqual(original, (self.output / "model-bundle.json").read_bytes())

    def test_sources_change_during_output_validation_preserves_output_and_mesh_cache(self):
        self.build()
        original = (self.output / "model-bundle.json").read_bytes()
        with patch.dict(sys.modules, {"preview": SimpleNamespace(mesh=lambda shape: ([[1, 2, 3]], [[0, 0, 0]]))}):
            def payload(mesh):
                mesh(ShapeStub())
                return {**fixture_payload(), "twinManifest": None}
            self.factory = payload
            with patch.object(fast_build, "identity", side_effect=[self.identity] * 3 + [{"changed": True}]):
                with self.assertRaisesRegex(ValueError, "sources changed during output"):
                    self.build(force=True)
        self.assertEqual(original, (self.output / "model-bundle.json").read_bytes())
        self.assertEqual(list((self.cache / "meshes").iterdir()), [])

    def test_mesh_reuses_identical_shape_and_rebuilds_corruption(self):
        generator = lambda shape: ([[1, 2, 3]], [[0, 0, 0]])
        with patch.dict(sys.modules, {"preview": SimpleNamespace(mesh=generator)}):
            cache = fast_build.MeshCache(self.cache, {"cad": "test"})
            self.addCleanup(cache.discard)
            cache.mesh(ShapeStub())
            cache.publish()
            cache.mesh(ShapeStub())
            self.assertEqual((cache.hits, cache.misses), (1, 1))
            next(self.cache.glob("*.json")).write_text("{}")
            cache.mesh(ShapeStub())
            cache.mesh(ShapeStub(b"different geometry"))
            self.assertEqual(cache.misses, 3)

    def test_failed_build_does_not_publish_partial_mesh_cache(self):
        with patch.dict(sys.modules, {"preview": SimpleNamespace(mesh=lambda shape: ([[1, 2, 3]], [[0, 0, 0]]))}):
            def fail(mesh):
                mesh(ShapeStub())
                raise ValueError("generation failed")
            self.factory = fail
            with self.assertRaisesRegex(ValueError, "generation failed"):
                self.build()
        self.assertEqual(list((self.cache / "meshes").iterdir()), [])

    def test_existing_build_lock_is_not_removed(self):
        self.cache.mkdir()
        (self.cache / ".build-lock").mkdir()
        with self.assertRaisesRegex(ValueError, "already running"):
            self.build()
        self.assertTrue((self.cache / ".build-lock").exists())

    def test_symlink_output_cannot_replace_or_delete_target(self):
        target = self.root / "preserve"
        target.mkdir()
        (target / "important.txt").write_text("preserved")
        try:
            self.output.symlink_to(target, target_is_directory=True)
        except OSError as error:
            self.skipTest("Creating symlinks unavailable: " + str(error))
        with self.assertRaisesRegex(ValueError, "symbolic links"):
            self.build()
        self.assertEqual((target / "important.txt").read_text(), "preserved")

    def test_parent_symlink_is_rejected_before_writing(self):
        target = self.root / "preserve"
        target.mkdir()
        link = self.root / "link"
        try:
            link.symlink_to(target, target_is_directory=True)
        except OSError as error:
            self.skipTest("Creating symlinks unavailable: " + str(error))
        with self.assertRaisesRegex(ValueError, "symbolic links"):
            fast_build.build(self.cache, link / "bundle", payload_factory=self.factory)
        self.assertEqual(list(target.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
