"""Reuse only a complete, source-bound CAD package from the same runtime."""
from __future__ import annotations

import hashlib
import importlib.metadata
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import tempfile
import zipfile
import zlib

from artifacts import (assert_cad_hashes, assert_verified_exports, export_names,
                       file_sha256, generated_export_names, GENERATION_SNAPSHOT,
                       VERIFICATION_EXPORTS)
from slicer_validation import assert_saved_evidence


CACHE_NAME = "regeneration-cache.json"
PASS_RESULT = "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE"
SHA256_PATTERN = re.compile(r"[a-f0-9]{64}")


class CacheValidationError(ValueError):
    """An incomplete or altered saved result cannot be reused."""


def _tool_header(text):
    versions = [line for line in text.splitlines() if line.startswith("PrusaSlicer-")]
    _require(len(versions) == 1, "Missing or ambiguous PrusaSlicer version")
    provenance = {}
    prefixes = {
        "app_commit": "Kufr Flatpak app commit: ",
        "runtime": "Kufr Flatpak runtime: ",
        "native_sha256": "Kufr native executable SHA-256: ",
    }
    for key, prefix in prefixes.items():
        values = [line[len(prefix):] for line in text.splitlines() if line.startswith(prefix)]
        _require(len(values) <= 1, "Ambiguous Flatpak tool provenance")
        if values:
            provenance[key] = values[0]
    if provenance:
        _require(set(provenance) == set(prefixes), "Incomplete Flatpak tool provenance")
        _require(SHA256_PATTERN.fullmatch(provenance["app_commit"])
                 and SHA256_PATTERN.fullmatch(provenance["native_sha256"])
                 and re.fullmatch(r"[A-Za-z0-9._-]+/x86_64/[A-Za-z0-9._-]+@[a-f0-9]{64}",
                                  provenance["runtime"]), "Invalid Flatpak tool provenance")
    return versions[0], provenance


def _slicer_identity():
    """Probe the configured entry point without writing or slicing anything."""
    configured = os.environ.get("KUFR_SLICER")
    executable = shutil.which(configured) if configured else None
    if not executable:
        return {"status": "UNAVAILABLE", "configured": configured}
    path = Path(executable).resolve()
    digest = file_sha256(path)
    try:
        probe = subprocess.run([str(path), "--help"], capture_output=True, text=True,
                               encoding="utf-8", errors="replace", timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise CacheValidationError("Cannot probe current PrusaSlicer") from error
    _require(probe.returncode == 0, "Current PrusaSlicer probe failed")
    header, provenance = _tool_header(probe.stdout)
    _require(file_sha256(path) == digest, "Slicer executable changed during probe")
    # Same normalized text as saved tool.log; raw file hashes remain separate.
    help_text = probe.stdout + "\nSTDERR:\n" + probe.stderr
    return {"status": "AVAILABLE", "path": str(path), "version": header,
            "executable_sha256": digest,
            "help_sha256": hashlib.sha256(help_text.encode("utf-8")).hexdigest(),
            "flatpak": provenance}


def runtime_environment():
    """Include all installed distributions, not just directly imported CAD tools."""
    distributions = []
    for distribution in importlib.metadata.distributions():
        name = distribution.metadata.get("Name", "")
        distributions.append({"name": name, "version": distribution.version})
    distributions.sort(key=lambda item: (item["name"].casefold(), item["name"], item["version"]))
    return {
        "python_version": platform.python_version(),
        "python_implementation": platform.python_implementation(),
        "platform": platform.platform(),
        "system": platform.system(),
        "machine": platform.machine(),
        "distributions": distributions,
        "slicer": _slicer_identity(),
        "ci_image": {name: os.environ[name] for name in ("ImageOS", "ImageVersion")
                     if name in os.environ},
    }


def cache_key(identity):
    serialized = json.dumps(identity, sort_keys=True, separators=(",", ":"),
                            ensure_ascii=True, allow_nan=False).encode("utf-8")
    return hashlib.sha256(serialized).hexdigest()


def _require(condition, message):
    if not condition:
        raise CacheValidationError(message)


def _safe_path(directory, name):
    _require(isinstance(name, str) and bool(name) and "\\" not in name,
             "Invalid inventory path")
    relative = PurePosixPath(name)
    _require(not relative.is_absolute() and ":" not in name
             and all(part not in ("", ".", "..") for part in name.split("/")),
             "Inventory path escapes its directory")
    path = directory / name
    _require(path.resolve().is_relative_to(directory), "Inventory symlink escapes its directory")
    return path


def _read_json(path, read_hashes=None):
    # JSON must be parsed in memory; retain its exact byte digest so a later edit
    # cannot replace the metadata that supplied the acceptance decision.
    content = path.read_bytes()
    value = json.loads(content.decode("utf-8"), object_pairs_hook=_unique_json_pairs,
                       parse_constant=_invalid_json_constant)
    _require(isinstance(value, dict), "Expected a JSON object: " + path.name)
    if read_hashes is not None:
        read_hashes[path.name] = hashlib.sha256(content).hexdigest()
    return value


def _unique_json_pairs(pairs):
    value = {}
    for key, item in pairs:
        _require(key not in value, "Duplicate JSON member: " + key)
        value[key] = item
    return value


def _invalid_json_constant(value):
    raise CacheValidationError("Nonfinite JSON constant: " + value)


def _hash_inventory(value, expected=None):
    _require(isinstance(value, dict) and bool(value), "Expected a nonempty hash inventory")
    if expected is not None:
        _require(set(value) == set(expected), "Hash inventory differs from the required files")
    for name, digest in value.items():
        _require(isinstance(name, str) and isinstance(digest, str)
                 and SHA256_PATTERN.fullmatch(digest) is not None,
                 "Invalid SHA256 inventory entry")
    return value


def _identity_sources(identity, root):
    _require(isinstance(identity, dict) and set(identity) == {"sources", "environment"},
             "Invalid cache identity")
    _require(isinstance(identity["environment"], dict), "Invalid runtime environment")
    sources = _hash_inventory(identity["sources"])
    for name in sources:
        _safe_path(root, name)
    return sources


def _directories(root, out):
    root = Path(root).resolve()
    out = Path(out).resolve()
    _require(out.is_relative_to(root) and out == (root / "exports").resolve(),
             "Cache outputs must be the source root's exports directory")
    return root, out


def _artifact_gates(root, out, manifest, report):
    # Existing gates raise ValueError for stale or altered exports. Convert only
    # this documented data-validation failure; programming errors still surface.
    try:
        assert_cad_hashes(root, manifest, report)
        assert_verified_exports(out, manifest, report)
    except ValueError as error:
        raise CacheValidationError(str(error)) from error


def _slicer_evidence(out, manifest, report, identity):
    """Keep local audit bytes in the cache receipt, outside the shipping ZIP."""
    current = identity["environment"].get("slicer")
    _require(isinstance(current, dict) and current.get("status") == "AVAILABLE",
             "A cache hit requires an available slicer")
    _require(_slicer_identity() == current, "Current slicer runtime identity changed")
    metadata = {}
    slicing = _read_json(_safe_path(out, "slicer-validation.json"), metadata)
    printing = report.get("printing")
    _require(slicing.get("status") == "PASS_SLICER"
             and isinstance(printing, dict) and printing.get("slicing") == "PASS_SLICER",
             "Cached package lacks actual slicer acceptance")
    _require(slicing.get("manifest_sha256") == file_sha256(_safe_path(out, "manifest.json")),
             "Slicer evidence belongs to a different manifest")
    tool = slicing.get("tool")
    _require(isinstance(tool, dict) and tool.get("name") == "PrusaSlicer"
             and isinstance(tool.get("probe"), dict) and tool["probe"].get("exit_code") == 0
             and tool.get("version") == current.get("version")
             and tool.get("executable_sha256") == current.get("executable_sha256"),
             "Saved slicer differs from current executable")
    parts = slicing.get("parts")
    expected_parts = {part["name"]: part for part in manifest["parts"]}
    expected = set(expected_parts)
    _require(isinstance(parts, list) and len(parts) == len(expected)
             and all(isinstance(part, dict) and isinstance(part.get("name"), str) for part in parts)
             and {part["name"] for part in parts} == expected,
             "Slicer evidence inventory differs from manifest")
    evidence = {**metadata, "slicing/tool.log": tool.get("log_sha256")}
    for part in parts:
        _require(part.get("status") == "PASS_SLICER", "Nonpassing saved slicer part")
        _require(all(isinstance(part.get(key), dict) for key in
                     ("toolpath", "effective_profile", "warning_resolution", "slice", "project")),
                 "Invalid saved slicing audit metadata")
        _require(part.get("toolpath", {}).get("status") == "PASS_TOOLPATH_AUDIT"
                 and part.get("effective_profile", {}).get("status") == "PASS_EFFECTIVE_PROFILE"
                 and part.get("warning_resolution", {}).get("status")
                 in ("PASS_NO_WARNINGS", "PASS_LONG_WARNING_REVIEW")
                 and part.get("slice", {}).get("exit_code") == 0
                 and part.get("project", {}).get("exit_code") == 0,
                 "Saved part has a failed slicing audit")
        contract = expected_parts[part["name"]].get("manufacturing")
        _require(isinstance(contract, dict)
                 and part.get("profile") == contract.get("slicer_profile")
                 and part.get("profile_sha256") == contract.get("slicer_profile_sha256"),
                 "Slicer profile differs from its manufacturing contract")
        profile_path = _safe_path(out, part.get("profile"))
        _require(file_sha256(profile_path) == part.get("profile_sha256"),
                 "Saved slicer profile changed")
        stl_name = "stl/" + part["name"] + ".stl"
        project_name = "3mf/" + part["name"] + ".3mf"
        _require(part.get("stl") == stl_name
                 and part.get("stl_sha256") == file_sha256(_safe_path(out, stl_name))
                 and part.get("project_3mf") == contract.get("project_3mf") == project_name
                 and part.get("project_sha256") == file_sha256(_safe_path(out, project_name)),
                 "Saved slicer model differs from manufacturing inventory")
        evidence[part["profile"]] = part["profile_sha256"]
        for key, suffix in (("gcode_sha256", ".gcode"),
                            ("effective_profile_sha256", "-effective.ini"),
                            ("slice_log_sha256", "-slice.log"),
                            ("project_log_sha256", "-project.log")):
            evidence["slicing/" + part["name"] + suffix] = part.get(key)
    _hash_inventory(evidence)
    before = {name: file_sha256(_safe_path(out, name)) for name in evidence}
    _require(before == evidence, "Missing or changed saved slicer bytes")
    saved_help = _safe_path(out, "slicing/tool.log").read_text(encoding="utf-8")
    version, provenance = _tool_header(saved_help)
    _require(version == current["version"] and provenance == current["flatpak"]
             and hashlib.sha256(saved_help.encode("utf-8")).hexdigest() == current["help_sha256"],
             "Saved slicer help/provenance differs from current tool")
    try:
        for part in parts:
            assert_saved_evidence(out, part)
    except (ValueError, KeyError, TypeError) as error:
        raise CacheValidationError("Invalid saved slicer audit: " + str(error)) from error
    _require(all(file_sha256(_safe_path(out, name)) == digest for name, digest in before.items()),
             "Saved slicer evidence changed during re-audit")
    return before


def _accepted_inventory(root, out, identity):
    sources = _identity_sources(identity, root)
    parameters = _read_json(_safe_path(root, "parameters.json"))
    revision = parameters.get("revision")
    _require(isinstance(revision, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", revision),
             "Unsafe package revision")
    metadata_hashes = {}
    manifest = _read_json(_safe_path(out, "manifest.json"), metadata_hashes)
    report = _read_json(_safe_path(out, "verification.json"), metadata_hashes)
    _require(manifest.get("sources") == sources and report.get("sources") == sources,
             "Export and verification source identities differ")
    _require(manifest.get("revision") == revision and report.get("revision") == revision,
             "Package revision differs from parameters")
    _require(report.get("result") == PASS_RESULT and report.get("physical_test_performed") is False,
             "No current CAD-only acceptance report")
    assemblies = manifest.get("assemblies")
    parts = manifest.get("parts")
    _require(isinstance(assemblies, list) and all(isinstance(name, str) for name in assemblies)
             and isinstance(parts, list)
             and all(isinstance(part, dict) and isinstance(part.get("name"), str) for part in parts),
             "Invalid manifest inventory")
    try:
        names = export_names(manifest)
    except ValueError as error:
        raise CacheValidationError(str(error)) from error
    for name in names:
        _safe_path(out, name)
    export_hashes = _hash_inventory(report.get("export_sha256"), set(names) - VERIFICATION_EXPORTS)
    cad_names = {name for name in names if name.endswith((".stl", ".step"))}
    _hash_inventory(report.get("cad_sha256"), cad_names)
    checked_parts = report.get("parts")
    _require(isinstance(checked_parts, list)
             and all(isinstance(part, dict) and isinstance(part.get("name"), str)
                     and isinstance(part.get("sha256"), str) for part in checked_parts),
             "Invalid checked part inventory")
    _require(len(checked_parts) == len(parts)
             and {part["name"] for part in checked_parts} == {part["name"] for part in parts},
             "Checked parts differ from manifest")
    snapshot = _read_json(_safe_path(out, GENERATION_SNAPSHOT), metadata_hashes)
    _require(type(snapshot.get("schema")) is int and snapshot["schema"] == 1
             and snapshot.get("sources") == sources,
             "Invalid generated snapshot")
    _hash_inventory(snapshot.get("sha256"), generated_export_names(manifest))
    _artifact_gates(root, out, manifest, report)
    audit_hashes = _slicer_evidence(out, manifest, report, identity)
    hashes = {name: file_sha256(_safe_path(out, name)) for name in names}
    _require({name: hashes[name] for name in export_hashes} == export_hashes,
             "Verified output changed while reading cache inventory")
    index = _read_json(_safe_path(out, "artifact-sha256.json"), metadata_hashes)
    expected_index = {**export_hashes, "verification.json": hashes["verification.json"]}
    _hash_inventory(index, expected_index)
    _require(index == expected_index, "Artifact index differs from verified bytes")
    _require(all(hashes[name] == digest for name, digest in metadata_hashes.items()),
             "Acceptance metadata changed while reading cache inventory")
    archive_name = revision + "-kouzelny-kufr.zip"
    archive_path = _safe_path(out, archive_name)
    _verify_archive(archive_path, {**sources, **{"exports/" + name: digest
                                               for name, digest in hashes.items()}})
    hashes[archive_name] = file_sha256(archive_path)
    checksum_path = _safe_path(out, "package-sha256.txt")
    _require(checksum_path.read_text(encoding="ascii")
             == f"{hashes[archive_name]}  {archive_name}\n", "Package checksum differs from ZIP")
    hashes["package-sha256.txt"] = file_sha256(checksum_path)
    _require(all(file_sha256(_safe_path(out, name)) == digest for name, digest in audit_hashes.items()),
             "Slicer evidence changed while validating package")
    hashes.update(audit_hashes)
    return hashes


def _verify_archive(path, expected):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        _require(len(names) == len(set(names)) and set(names) == set(expected),
                 "ZIP inventory differs from verified sources and exports")
        for name, digest in expected.items():
            hasher = hashlib.sha256()
            with archive.open(name) as member:
                for chunk in iter(lambda: member.read(1024 * 1024), b""):
                    hasher.update(chunk)
            _require(hasher.hexdigest() == digest, "ZIP member differs from verified bytes: " + name)


def check_cache(root, out, identity):
    """Data corruption is a miss; unrelated code and runtime failures propagate."""
    try:
        root, out = _directories(root, out)
        stamp = _read_json(_safe_path(out, CACHE_NAME))
        _require(set(stamp) == {"schema", "key", "identity", "sha256"}
                 and type(stamp["schema"]) is int and stamp["schema"] == 1,
                 "Unknown cache record schema")
        _require(stamp["identity"] == identity and stamp["key"] == cache_key(identity),
                 "Cache identity changed")
        hashes = _accepted_inventory(root, out, identity)
        recorded = _hash_inventory(stamp["sha256"], hashes)
        _require(recorded == hashes, "Cached output bytes changed")
        return True
    except (CacheValidationError, FileNotFoundError, NotADirectoryError, IsADirectoryError,
            PermissionError, json.JSONDecodeError, UnicodeDecodeError,
            zipfile.BadZipFile, zlib.error, EOFError):
        return False


def write_cache(root, out, identity):
    """Publish the cache receipt atomically only after every acceptance gate passes."""
    root, out = _directories(root, out)
    stamp_path = _safe_path(out, CACHE_NAME)
    stamp_path.unlink(missing_ok=True)
    hashes = _accepted_inventory(root, out, identity)
    stamp = {"schema": 1, "key": cache_key(identity), "identity": identity, "sha256": hashes}
    temporary_path = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=out,
                                         prefix=".regeneration-cache-", suffix=".tmp",
                                         delete=False) as temporary:
            temporary_path = Path(temporary.name)
            json.dump(stamp, temporary, sort_keys=True, indent=2, allow_nan=False)
            temporary.write("\n")
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_path, stamp_path)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
