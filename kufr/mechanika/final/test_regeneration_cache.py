"""Cheap cache regressions use the real artifact boundaries without importing CAD."""
from contextlib import ExitStack
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
import warnings
from unittest.mock import patch
import zipfile

from artifacts import (ASSEMBLY_NAMES, export_names, file_sha256, generate_exports,
                       generated_export_names, VERIFICATION_EXPORTS)
import regeneration_cache as cache


def write_json(path, value):
    path.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")


class RegenerationCacheTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        self.out = self.root / "exports"
        (self.root / "input.py").write_bytes(b"current source")
        write_json(self.root / "parameters.json", {"revision": "F2"})
        self.sources = {name: file_sha256(self.root / name)
                        for name in ("input.py", "parameters.json")}
        self.identity = {"sources": self.sources, "environment": {"python": "test"}}
        self.manifest = {"revision": "F2", "assemblies": list(ASSEMBLY_NAMES),
                         "parts": [{"name": "body_1"}], "sources": self.sources}
        generator = SimpleNamespace(OUT=self.out)
        def generate():
            for name in generated_export_names(self.manifest):
                path = generator.OUT / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"generated artifact: " + name.encode("ascii"))
            write_json(generator.OUT / "manifest.json", self.manifest)
        generator.main = generate
        generate_exports(self.root, self.out, [generator], lambda: self.sources)
        for name in ("collision-details.json", "wire-collision-details.json"):
            write_json(self.out / name, {})
        names = export_names(self.manifest)
        self.report = {
            "result": cache.PASS_RESULT, "revision": "F2", "physical_test_performed": False,
            "sources": self.sources,
            "parts": [{"name": "body_1", "sha256": file_sha256(self.out / "stl/body_1.stl")}],
            "cad_sha256": {name: file_sha256(self.out / name)
                           for name in names if name.endswith((".stl", ".step"))},
            "export_sha256": {name: file_sha256(self.out / name)
                              for name in set(names) - VERIFICATION_EXPORTS},
        }
        write_json(self.out / "verification.json", self.report)
        write_json(self.out / "artifact-sha256.json",
                   {**self.report["export_sha256"],
                    "verification.json": file_sha256(self.out / "verification.json")})
        self.archive = self.out / "F2-kouzelny-kufr.zip"
        self.rebuild_archive()

    def rebuild_archive(self, extra=(), omitted=()):
        names = list(self.sources) + ["exports/" + name for name in export_names(self.manifest)]
        with zipfile.ZipFile(self.archive, "w", zipfile.ZIP_DEFLATED) as archive:
            for name in names:
                if name not in omitted:
                    archive.write(self.root / name, name)
            for name in extra:
                archive.writestr(name, b"unexpected member")
        (self.out / "package-sha256.txt").write_text(
            f"{file_sha256(self.archive)}  {self.archive.name}\n", encoding="ascii")

    def stamp(self):
        cache.write_cache(self.root, self.out, self.identity)
        self.assertTrue(cache.check_cache(self.root, self.out, self.identity))

    def read_stamp(self):
        return json.loads((self.out / cache.CACHE_NAME).read_text(encoding="utf-8"))

    def replace_stamp(self, stamp):
        write_json(self.out / cache.CACHE_NAME, stamp)

    def test_accepted_outputs_hit_and_cache_metadata_stays_outside_package(self):
        self.stamp()
        expected = set(export_names(self.manifest)) | {self.archive.name, "package-sha256.txt"}
        self.assertEqual(set(self.read_stamp()["sha256"]), expected)
        self.assertNotIn(cache.CACHE_NAME, export_names(self.manifest))
        self.assertNotIn(cache.CACHE_NAME,
                         json.loads((self.out / "artifact-sha256.json").read_text(encoding="utf-8")))
        with zipfile.ZipFile(self.archive) as archive:
            self.assertNotIn("exports/" + cache.CACHE_NAME, archive.namelist())
        (self.out / "unrelated-old-export.step").write_bytes(b"stale but excluded")
        self.assertTrue(cache.check_cache(self.root, self.out, self.identity))

    def test_source_identity_or_runtime_change_is_a_miss(self):
        self.stamp()
        changed_sources = {**self.sources, "input.py": "0" * 64}
        for identity in ({**self.identity, "sources": changed_sources},
                         {**self.identity, "sources": {"input.py": self.sources["input.py"]}},
                         {**self.identity, "environment": {"python": "different"}}):
            with self.subTest(identity=identity):
                self.assertFalse(cache.check_cache(self.root, self.out, identity))

    def test_every_required_output_change_or_removal_invalidates(self):
        self.stamp()
        for name in self.read_stamp()["sha256"]:
            path = self.out / name
            original = path.read_bytes()
            with self.subTest(name=name, change="content"):
                path.write_bytes(original + b"modified")
                self.assertFalse(cache.check_cache(self.root, self.out, self.identity))
            path.write_bytes(original)
            with self.subTest(name=name, change="removal"):
                path.unlink()
                self.assertFalse(cache.check_cache(self.root, self.out, self.identity))
            path.write_bytes(original)
        self.assertTrue(cache.check_cache(self.root, self.out, self.identity))

    def test_stamp_missing_extra_and_traversal_inventory_is_a_miss(self):
        self.stamp()
        original = self.read_stamp()
        for change in ("missing", "extra", "traversal"):
            stamp = json.loads(json.dumps(original))
            if change == "missing":
                stamp["sha256"].pop("layout.svg")
            else:
                stamp["sha256"]["../outside" if change == "traversal" else "old.step"] = "0" * 64
            self.replace_stamp(stamp)
            with self.subTest(change=change):
                self.assertFalse(cache.check_cache(self.root, self.out, self.identity))

    def test_missing_malformed_or_unknown_cache_record_is_a_miss(self):
        path = self.out / cache.CACHE_NAME
        self.assertFalse(cache.check_cache(self.root, self.out, self.identity))
        self.stamp()
        original = self.read_stamp()
        for record in (b"{", b"[]", b"null", b"\xff", b"{}", b'{"a":NaN}',
                       b'{"schema":1,"schema":1}'):
            path.write_bytes(record)
            with self.subTest(record=record):
                self.assertFalse(cache.check_cache(self.root, self.out, self.identity))
        for stamp in ({**original, "schema": 2}, {**original, "schema": True},
                      {**original, "key": "0" * 64}, {**original, "sha256": []}):
            self.replace_stamp(stamp)
            self.assertFalse(cache.check_cache(self.root, self.out, self.identity))

    def test_report_snapshot_and_index_inventories_are_fail_closed(self):
        cases = (("verification.json", "export_sha256"),
                 ("verification.json", "cad_sha256"),
                 ("generation-snapshot.json", "sha256"),
                 ("artifact-sha256.json", None))
        for name, field in cases:
            path = self.out / name
            original = path.read_bytes()
            for mutation in ("missing", "extra", "traversal", "malformed"):
                value = json.loads(original)
                inventory = value if field is None else value[field]
                if mutation == "missing":
                    inventory.pop(next(iter(inventory)))
                elif mutation == "malformed":
                    inventory[next(iter(inventory))] = True
                else:
                    inventory["../outside" if mutation == "traversal" else "old.step"] = "0" * 64
                write_json(path, value)
                with self.subTest(name=name, mutation=mutation):
                    with self.assertRaises(cache.CacheValidationError):
                        cache.write_cache(self.root, self.out, self.identity)
                    self.assertFalse((self.out / cache.CACHE_NAME).exists())
                path.write_bytes(original)

    def test_nonpass_stale_or_malformed_report_cannot_issue_stamp(self):
        path = self.out / "verification.json"
        original = path.read_bytes()
        cases = ({**self.report, "result": "FAIL"},
                 {**self.report, "physical_test_performed": True},
                 {**self.report, "sources": {}}, {**self.report, "parts": []},
                 {**self.report, "parts": None})
        for report in cases:
            self.stamp()
            write_json(path, report)
            with self.subTest(report=report):
                with self.assertRaises(cache.CacheValidationError):
                    cache.write_cache(self.root, self.out, self.identity)
                self.assertFalse((self.out / cache.CACHE_NAME).exists())
            path.write_bytes(original)

    def test_archive_members_and_checksum_are_validated_before_stamping(self):
        for extra, omitted in ((["../outside"], []), (["exports/old.step"], []),
                               ([], ["exports/layout.svg"])):
            self.rebuild_archive(extra, omitted)
            with self.subTest(extra=extra, omitted=omitted):
                with self.assertRaises(cache.CacheValidationError):
                    cache.write_cache(self.root, self.out, self.identity)
        self.rebuild_archive()
        (self.out / "package-sha256.txt").write_text("0" * 64 + "  wrong.zip\n", encoding="ascii")
        with self.assertRaises(cache.CacheValidationError):
            cache.write_cache(self.root, self.out, self.identity)
        self.rebuild_archive()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(self.archive, "a") as archive:
                archive.writestr("exports/layout.svg", b"wrong verified bytes")
        with self.assertRaises(cache.CacheValidationError):
            cache.write_cache(self.root, self.out, self.identity)
        self.rebuild_archive()
        (self.root / "input.py").write_bytes(b"changed source content")
        self.rebuild_archive()
        with self.assertRaises(cache.CacheValidationError):
            cache.write_cache(self.root, self.out, self.identity)

    def test_unsafe_revision_or_manifest_part_is_rejected(self):
        write_json(self.root / "parameters.json", {"revision": "../outside"})
        with self.assertRaises(cache.CacheValidationError):
            cache.write_cache(self.root, self.out, self.identity)
        write_json(self.root / "parameters.json", {"revision": "F2"})
        write_json(self.out / "manifest.json", {**self.manifest, "parts": [{"name": "../outside"}]})
        with self.assertRaises(cache.CacheValidationError):
            cache.write_cache(self.root, self.out, self.identity)

    def test_symlink_escape_is_a_miss(self):
        self.stamp()
        path = self.out / "layout.svg"
        with tempfile.TemporaryDirectory() as outside:
            target = Path(outside) / "layout.svg"
            target.write_bytes(path.read_bytes())
            path.unlink()
            try:
                path.symlink_to(target)
            except OSError as error:
                self.skipTest("Symlink creation unavailable: " + str(error))
            self.assertFalse(cache.check_cache(self.root, self.out, self.identity))
            with self.assertRaises(cache.CacheValidationError):
                cache.write_cache(self.root, self.out, self.identity)

    def test_programming_errors_propagate_and_failed_write_drops_stamp(self):
        self.stamp()
        with patch.object(cache, "assert_verified_exports", side_effect=RuntimeError("code defect")):
            with self.assertRaisesRegex(RuntimeError, "code defect"):
                cache.check_cache(self.root, self.out, self.identity)
            with self.assertRaisesRegex(RuntimeError, "code defect"):
                cache.write_cache(self.root, self.out, self.identity)
        self.assertFalse((self.out / cache.CACHE_NAME).exists())

    def test_output_or_report_changed_after_artifact_gates_cannot_be_stamped(self):
        original_gate = cache.assert_verified_exports
        for name in ("layout.svg", "verification.json", "artifact-sha256.json"):
            path = self.out / name
            original = path.read_bytes()
            def change_after_gate(*args):
                original_gate(*args)
                path.write_bytes(original + b"\n")
            with self.subTest(name=name), \
                 patch.object(cache, "assert_verified_exports", side_effect=change_after_gate):
                with self.assertRaises(cache.CacheValidationError):
                    cache.write_cache(self.root, self.out, self.identity)
                self.assertFalse((self.out / cache.CACHE_NAME).exists())
            path.write_bytes(original)

    def test_atomic_publish_failure_leaves_no_stamp_or_temporary_file(self):
        self.stamp()
        with patch.object(cache.os, "replace", side_effect=OSError("publish failure")):
            with self.assertRaisesRegex(OSError, "publish failure"):
                cache.write_cache(self.root, self.out, self.identity)
        self.assertFalse((self.out / cache.CACHE_NAME).exists())
        self.assertEqual(list(self.out.glob(".regeneration-cache-*.tmp")), [])

    def test_hashing_does_not_read_whole_files(self):
        original_read_bytes = Path.read_bytes
        def metadata_only(path):
            self.assertEqual(path.suffix, ".json", "Whole-file allocation of generated binary output")
            return original_read_bytes(path)
        with patch.object(Path, "read_bytes", new=metadata_only):
            self.stamp()


class RuntimeIdentityTests(unittest.TestCase):
    def test_key_is_canonical_and_changes_with_identity(self):
        first = {"sources": {"b": "2", "a": "1"}, "environment": {"version": "x"}}
        reordered = {"environment": {"version": "x"}, "sources": {"a": "1", "b": "2"}}
        self.assertEqual(cache.cache_key(first), cache.cache_key(reordered))
        self.assertNotEqual(cache.cache_key(first), cache.cache_key({**first, "environment": {}}))

    def test_runtime_tracks_python_platform_all_distribution_versions_and_ci(self):
        distributions = [SimpleNamespace(metadata={"Name": "Zed"}, version="2"),
                         SimpleNamespace(metadata={"Name": "Alpha"}, version="1")]
        with ExitStack() as stack:
            stack.enter_context(patch.object(cache.importlib.metadata, "distributions", return_value=distributions))
            stack.enter_context(patch.dict(os.environ, {"ImageOS": "ubuntu24", "ImageVersion": "20261014.1"}, clear=True))
            for name in ("python_version", "python_implementation", "platform", "system", "machine"):
                stack.enter_context(patch.object(cache.platform, name, return_value=name))
            environment = cache.runtime_environment()
            distributions.reverse()
            self.assertEqual(cache.runtime_environment(), environment)
            distributions[0].version = "changed"
            self.assertNotEqual(cache.runtime_environment(), environment)
        self.assertEqual(environment["ci_image"], {"ImageOS": "ubuntu24", "ImageVersion": "20261014.1"})
        self.assertEqual(environment["distributions"][0], {"name": "Alpha", "version": "1"})


if __name__ == "__main__":
    unittest.main()
