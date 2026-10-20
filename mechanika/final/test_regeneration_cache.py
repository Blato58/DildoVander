"""Cheap cache regressions use the real artifact boundaries without importing CAD."""
from contextlib import ExitStack
import hashlib
import json
import os
import subprocess
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
import warnings
from unittest.mock import patch
import zipfile

from artifacts import (ASSEMBLY_NAMES, export_names, file_sha256, generate_exports,
                       generated_export_names, VERIFICATION_EXPORTS)
from slicer_validation import audit_profile, parse_gcode, resolve_warnings
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
        self.help_text = "PrusaSlicer-2.9.4\n\nSTDERR:\n"
        self.tool_identity = {
            "status": "AVAILABLE", "path": "test-slicer", "version": "PrusaSlicer-2.9.4",
            "executable_sha256": "a" * 64,
            "help_sha256": hashlib.sha256(self.help_text.encode("utf-8")).hexdigest(), "flatpak": {},
        }
        self.identity = {"sources": self.sources,
                         "environment": {"python": "test", "slicer": self.tool_identity}}
        self.tool_probe = patch.object(cache, "_slicer_identity", return_value=self.tool_identity)
        self.tool_probe.start()
        self.addCleanup(self.tool_probe.stop)
        self.manifest = {"revision": "F2", "assemblies": list(ASSEMBLY_NAMES),
                         "parts": [{"name": "body_1"}], "sources": self.sources}
        generator = SimpleNamespace(OUT=self.out)
        def generate():
            for name in generated_export_names(self.manifest):
                path = generator.OUT / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"generated artifact: " + name.encode("ascii"))
            self.write_slicing_fixture(generator.OUT)
        generator.main = generate
        generate_exports(self.root, self.out, [generator], lambda: self.sources)
        for name in ("collision-details.json", "wire-collision-details.json"):
            write_json(self.out / name, {})
        names = export_names(self.manifest)
        self.report = {
            "result": cache.PASS_RESULT, "revision": "F2", "physical_test_performed": False,
            "sources": self.sources,
            "printing": {"slicing": "PASS_SLICER"},
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

    def write_slicing_fixture(self, out):
        audit = out / "slicing"
        audit.mkdir()
        profile = "support_material = 0\n"
        (out / "profiles/body_1.ini").write_text(profile, encoding="utf-8")
        profile_digest = file_sha256(out / "profiles/body_1.ini")
        self.manifest["parts"][0]["manufacturing"] = {
            "slicer_profile": "profiles/body_1.ini", "slicer_profile_sha256": profile_digest,
            "project_3mf": "3mf/body_1.3mf",
        }
        write_json(out / "manifest.json", self.manifest)
        text = "M82\nG92 E0\n;LAYER_CHANGE\nG1 Z0.2\n;TYPE:External perimeter\nG0 X10 Y10\nG1 X20 E1\n"
        run = {"exit_code": 0, "warnings": [], "stability_types": [], "warnings_truncated": False}
        part = {"name": "body_1", "status": "PASS_SLICER", "profile": "profiles/body_1.ini",
                "profile_sha256": profile_digest, "stl": "stl/body_1.stl",
                "stl_sha256": file_sha256(out / "stl/body_1.stl"),
                "project_3mf": "3mf/body_1.3mf", "project_sha256": file_sha256(out / "3mf/body_1.3mf"),
                "slice": run, "project": run.copy(),
                "toolpath": parse_gcode(text, bed=(220, 220, 250)),
                "effective_profile": audit_profile(text, profile, profile),
                "warning_resolution": resolve_warnings(run, text)}
        for key, name, content in (
            ("gcode_sha256", "body_1.gcode", text),
            ("effective_profile_sha256", "body_1-effective.ini", profile),
            ("slice_log_sha256", "body_1-slice.log", "Exported\nSTDERR:\n"),
            ("project_log_sha256", "body_1-project.log", "Exported\nSTDERR:\n"),
        ):
            path = audit / name
            path.write_text(content, encoding="utf-8")
            part[key] = file_sha256(path)
        (audit / "tool.log").write_text(self.help_text, encoding="utf-8")
        self.slicing = {"status": "PASS_SLICER", "manifest_sha256": file_sha256(out / "manifest.json"),
                        "parts": [part], "tool": {
                            "name": "PrusaSlicer", "probe": {"exit_code": 0},
                            "version": self.tool_identity["version"],
                            "executable_sha256": self.tool_identity["executable_sha256"],
                            "log_sha256": file_sha256(audit / "tool.log"),
                        }}
        write_json(out / "slicer-validation.json", self.slicing)

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
        expected.update("slicing/" + path.name for path in (self.out / "slicing").iterdir())
        self.assertEqual(set(self.read_stamp()["sha256"]), expected)
        self.assertNotIn(cache.CACHE_NAME, export_names(self.manifest))
        self.assertNotIn(cache.CACHE_NAME,
                         json.loads((self.out / "artifact-sha256.json").read_text(encoding="utf-8")))
        with zipfile.ZipFile(self.archive) as archive:
            self.assertNotIn("exports/" + cache.CACHE_NAME, archive.namelist())
            self.assertFalse(any(name.startswith("exports/slicing/") for name in archive.namelist()))
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

    def check_slicing(self):
        return cache._slicer_evidence(self.out, self.manifest, self.report, self.identity)

    def test_fresh_gcode_hash_cannot_bless_stale_or_failed_audit(self):
        part = self.slicing["parts"][0]
        path = self.out / "slicing/body_1.gcode"
        path.write_text(path.read_text(encoding="utf-8") + "G1 X30 E2\n", encoding="utf-8")
        part["gcode_sha256"] = file_sha256(path)
        write_json(self.out / "slicer-validation.json", self.slicing)
        with self.assertRaisesRegex(cache.CacheValidationError, "audit differs"):
            self.check_slicing()
        part["toolpath"] = parse_gcode(path.read_text(encoding="utf-8"), bed=(220, 220, 250))
        part["toolpath"]["status"] = "FAIL_SLICER"
        write_json(self.out / "slicer-validation.json", self.slicing)
        with self.assertRaisesRegex(cache.CacheValidationError, "failed slicing audit"):
            self.check_slicing()

    def test_slicer_inventory_and_status_are_fail_closed(self):
        original = json.loads(json.dumps(self.slicing))
        for parts in ([], original["parts"] * 2,
                      [{**original["parts"][0], "name": "other"}]):
            write_json(self.out / "slicer-validation.json", {**original, "parts": parts})
            with self.subTest(parts=parts), self.assertRaises(cache.CacheValidationError):
                self.check_slicing()
        for status in ("SKIP_SLICER_UNAVAILABLE", "FAIL_SLICER"):
            write_json(self.out / "slicer-validation.json", {**original, "status": status})
            with self.subTest(status=status), self.assertRaises(cache.CacheValidationError):
                self.check_slicing()

    def test_saved_and_current_slicer_identity_must_match(self):
        for field, value in (("status", "UNAVAILABLE"), ("version", "PrusaSlicer-2.9.6"),
                             ("path", "replacement"), ("executable_sha256", "b" * 64),
                             ("help_sha256", "b" * 64), ("flatpak", {"app_commit": "b" * 64})):
            with self.subTest(field=field), \
                    patch.object(cache, "_slicer_identity", return_value={**self.tool_identity, field: value}), \
                    self.assertRaises(cache.CacheValidationError):
                self.check_slicing()
        original = json.loads(json.dumps(self.slicing))
        for field in ("version", "executable_sha256"):
            changed = json.loads(json.dumps(original))
            changed["tool"][field] = "replacement"
            write_json(self.out / "slicer-validation.json", changed)
            with self.subTest(saved=field), self.assertRaises(cache.CacheValidationError):
                self.check_slicing()

    def test_saved_help_fresh_hash_cannot_hide_provenance_change(self):
        path = self.out / "slicing/tool.log"
        path.write_text(self.help_text + "changed help\n", encoding="utf-8")
        self.slicing["tool"]["log_sha256"] = file_sha256(path)
        write_json(self.out / "slicer-validation.json", self.slicing)
        with self.assertRaisesRegex(cache.CacheValidationError, "help/provenance"):
            self.check_slicing()

    def test_slicing_symlink_and_profile_escape_are_rejected(self):
        part = self.slicing["parts"][0]
        original = part["profile"]
        part["profile"] = "../outside.ini"
        write_json(self.out / "slicer-validation.json", self.slicing)
        with self.assertRaises(cache.CacheValidationError):
            self.check_slicing()
        part["profile"] = original
        write_json(self.out / "slicer-validation.json", self.slicing)
        path = self.out / "slicing/body_1.gcode"
        target = self.root / "outside.gcode"
        target.write_bytes(path.read_bytes())
        path.unlink()
        try:
            path.symlink_to(target)
        except OSError as error:
            self.skipTest("Symlink creation unavailable: " + str(error))
        with self.assertRaises(cache.CacheValidationError):
            self.check_slicing()

    def test_slicing_mutation_during_reaudit_is_rejected(self):
        original_gate = cache.assert_saved_evidence
        def mutate_after_gate(*args):
            original_gate(*args)
            with (self.out / "slicing/body_1.gcode").open("a", encoding="utf-8") as stream:
                stream.write("; concurrent edit\n")
        with patch.object(cache, "assert_saved_evidence", side_effect=mutate_after_gate), \
                self.assertRaisesRegex(cache.CacheValidationError, "changed during re-audit"):
            self.check_slicing()

    def test_malformed_nested_slicer_metadata_is_rejected(self):
        original = json.loads(json.dumps(self.slicing))
        for key in ("toolpath", "effective_profile", "warning_resolution", "slice", "project"):
            changed = json.loads(json.dumps(original))
            changed["parts"][0][key] = []
            write_json(self.out / "slicer-validation.json", changed)
            with self.subTest(key=key), self.assertRaises(cache.CacheValidationError):
                self.check_slicing()
        write_json(self.out / "slicer-validation.json", original)
        self.report["printing"] = []
        with self.assertRaises(cache.CacheValidationError):
            self.check_slicing()

    def test_slicer_metadata_changed_after_parsing_is_rejected(self):
        original_gate = cache.assert_saved_evidence
        def mutate_after_gate(*args):
            original_gate(*args)
            with (self.out / "slicer-validation.json").open("a", encoding="utf-8") as stream:
                stream.write("\n")
        with patch.object(cache, "assert_saved_evidence", side_effect=mutate_after_gate), \
                self.assertRaisesRegex(cache.CacheValidationError, "changed during re-audit"):
            self.check_slicing()

    def test_identical_unrelated_profile_and_wrong_model_bindings_are_rejected(self):
        original = json.loads(json.dumps(self.slicing))
        profile = self.out / "profiles/body_1.ini"
        (self.out / "unrelated.ini").write_bytes(profile.read_bytes())
        for key, value in (("profile", "unrelated.ini"), ("profile_sha256", "b" * 64),
                           ("stl", "stl/other.stl"), ("stl_sha256", "b" * 64),
                           ("project_3mf", "3mf/other.3mf"), ("project_sha256", "b" * 64)):
            changed = json.loads(json.dumps(original))
            changed["parts"][0][key] = value
            write_json(self.out / "slicer-validation.json", changed)
            with self.subTest(key=key), self.assertRaises(cache.CacheValidationError):
                self.check_slicing()

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
            self.assertTrue(path.suffix == ".json" or path.parent.name == "slicing",
                            "Whole-file allocation of generated binary output")
            return original_read_bytes(path)
        with patch.object(Path, "read_bytes", new=metadata_only):
            self.stamp()


class RuntimeIdentityTests(unittest.TestCase):
    def test_probe_binds_launcher_help_and_complete_flatpak_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "slicer"
            executable.write_bytes(b"launcher one")
            header = ("Kufr Flatpak app commit: " + "a" * 64 + "\n"
                      "Kufr Flatpak runtime: org.freedesktop.Platform/x86_64/24.08@" + "b" * 64 + "\n"
                      "Kufr native executable SHA-256: " + "c" * 64 + "\nPrusaSlicer-2.9.6\n")
            result = SimpleNamespace(returncode=0, stdout=header, stderr="")
            with patch.dict(os.environ, {"KUFR_SLICER": str(executable)}), \
                    patch.object(cache.shutil, "which", return_value=str(executable)), \
                    patch.object(cache.subprocess, "run", return_value=result) as probe:
                first = cache._slicer_identity()
                self.assertEqual(first["flatpak"]["native_sha256"], "c" * 64)
                self.assertEqual(probe.call_args.args[0], [str(executable.resolve()), "--help"])
                self.assertEqual(probe.call_args.kwargs["timeout"], 30)
                self.assertFalse(probe.call_args.kwargs["check"])
                for field in ("app commit", "native executable SHA-256"):
                    result.stdout = header.replace("a" * 64 if field == "app commit" else "c" * 64, "d" * 64)
                    self.assertNotEqual(cache._slicer_identity(), first)
                result.stdout = header
                executable.write_bytes(b"launcher two")
                self.assertNotEqual(cache._slicer_identity(), first)
                result.stdout += "new help setting\n"
                self.assertNotEqual(cache._slicer_identity()["help_sha256"], first["help_sha256"])

    def test_missing_failed_or_unclassified_probe_never_qualifies(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(cache.subprocess, "run") as run:
            self.assertEqual(cache._slicer_identity()["status"], "UNAVAILABLE")
            run.assert_not_called()
        with tempfile.TemporaryDirectory() as directory:
            executable = Path(directory) / "slicer"
            executable.write_bytes(b"launcher")
            with patch.dict(os.environ, {"KUFR_SLICER": str(executable)}), \
                    patch.object(cache.shutil, "which", return_value=str(executable)):
                for result in (SimpleNamespace(returncode=1, stdout="PrusaSlicer-2.9.4\n", stderr="failed"),
                               SimpleNamespace(returncode=0, stdout="Other tool\n", stderr=""),
                               SimpleNamespace(returncode=0, stdout="PrusaSlicer-2.9.6\nKufr Flatpak app commit: " + "a" * 64, stderr="")):
                    with patch.object(cache.subprocess, "run", return_value=result), \
                            self.assertRaises(cache.CacheValidationError):
                        cache._slicer_identity()
                with patch.object(cache.subprocess, "run", side_effect=subprocess.TimeoutExpired("slicer", 30)), \
                        self.assertRaises(cache.CacheValidationError):
                    cache._slicer_identity()

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
