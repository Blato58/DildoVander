"""Refresh presentation around trusted, unchanged manufactured CAD exports.

The caller must establish trusted artifact transport and an unchanged
manufacturing fingerprint first. This module also verifies the saved receipts
before transferring their evidence; it never imports or runs CAD tooling.
"""
from pathlib import Path
import hashlib
import json
import re
import tempfile
import zipfile

from artifacts import (
    GENERATION_SNAPSHOT, VERIFICATION_EXPORTS, assert_cad_hashes, assert_generation_snapshot,
    assert_verified_exports, export_names, generated_export_names, package_files,
)
from source_inputs import prepared_sources, presentation_only, source_hashes, validation_only
from viewer_document import render_document


ROOT = Path(__file__).resolve().parent
MODEL_MARKER = "<script>const model="


def _digest(content):
    return hashlib.sha256(content).hexdigest()


def _json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")


def model_payload(document):
    """Decode one complete embedded JSON value, independently of JS trailers."""
    if document.count(MODEL_MARKER) != 1:
        raise ValueError("Expected one saved viewer model")
    encoded = document.split(MODEL_MARKER, 1)[1]
    payload, end = json.JSONDecoder().raw_decode(encoded)
    if not isinstance(payload, dict) or not isinstance(payload.get("meshes"), list):
        raise ValueError("Saved viewer model has no mesh inventory")
    if not encoded[end:].lstrip().startswith(";"):
        raise ValueError("Saved viewer model has an unexpected JavaScript boundary")
    return payload


def _assert_sources(saved_sources, current_sources):
    if not isinstance(saved_sources, dict) or not saved_sources:
        raise ValueError("Missing saved source inventory")
    changed = {name for name in set(saved_sources) | set(current_sources)
               if saved_sources.get(name) != current_sources.get(name)}
    unsafe = sorted(name for name in changed if not presentation_only(name))
    if unsafe:
        raise ValueError("Manufacturing sources changed; regenerate: " + ", ".join(unsafe))
    return changed


def _saved_receipts(out, saved_sources):
    report_bytes = (out / "verification.json").read_bytes()
    report = json.loads(report_bytes)
    manifest = json.loads((out / "manifest.json").read_bytes())
    generation_bytes = (out / GENERATION_SNAPSHOT).read_bytes()
    generation = json.loads(generation_bytes)
    if report.get("result") != "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE":
        raise ValueError("Saved mechanical validation did not pass")
    if report.get("sources") != saved_sources or manifest.get("sources") != saved_sources:
        raise ValueError("Saved mechanical receipts have a different source identity")
    assert_cad_hashes(ROOT, manifest, report)
    assert_verified_exports(out, manifest, report)
    expected_index = {**report["export_sha256"], "verification.json": _digest(report_bytes)}
    if json.loads((out / "artifact-sha256.json").read_bytes()) != expected_index:
        raise ValueError("Saved artifact index differs from verified exports")
    slicing = json.loads((out / "slicer-validation.json").read_bytes())
    analytical = json.loads((out / "printability.json").read_bytes())
    manifest_digest = _digest((out / "manifest.json").read_bytes())
    for label, evidence in (("slicing", slicing), ("analytical printability", analytical)):
        if evidence.get("manifest_sha256") != manifest_digest:
            raise ValueError("Saved " + label + " evidence belongs to another manifest")
    return report_bytes, report, manifest, generation_bytes, generation, slicing, analytical


def _rebind_manifest(manifest, sources, slicing, analytical):
    """Update both manufacturing reports that bind the manifest's exact bytes."""
    manifest["sources"] = sources
    manifest_bytes = _json_bytes(manifest)
    manifest_digest = _digest(manifest_bytes)
    slicing["manifest_sha256"] = manifest_digest
    analytical["manifest_sha256"] = manifest_digest
    return {"manifest.json": manifest_bytes, "slicer-validation.json": _json_bytes(slicing),
            "printability.json": _json_bytes(analytical)}


def _write_archive(target, files, expected, replacements):
    """Stable ZIP metadata makes repeated refreshes produce the same package."""
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(files):
            name = path.relative_to(ROOT).as_posix()
            content = replacements[name] if name in replacements else path.read_bytes()
            if _digest(content) != expected[name]:
                raise ValueError("Package member changed during refresh: " + name)
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, content, compresslevel=6)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(expected):
            raise ValueError("Refreshed archive inventory mismatch")
        for name, digest in expected.items():
            if _digest(archive.read(name)) != digest:
                raise ValueError("Refreshed archive readback mismatch: " + name)


def refresh_generation(saved_sources):
    """Rebind trusted geometry after verifier-only edits; invalidate acceptance.

    The caller authenticates transport and unchanged generation inputs/tools.
    A successful return permits generation reuse only: all verification and
    packaging must run again before the result is accepted or transported.
    """
    current_sources = prepared_sources()
    if not isinstance(saved_sources, dict) or not saved_sources:
        raise ValueError("Missing saved source inventory")
    changed = {name for name in set(saved_sources) | set(current_sources)
               if saved_sources.get(name) != current_sources.get(name)}
    if not changed:
        return False
    unsafe = sorted(name for name in changed if not validation_only(name))
    if unsafe:
        raise ValueError("Generation sources changed; regenerate: " + ", ".join(unsafe))
    out = ROOT / "exports"
    _, _, manifest, generation_bytes, generation, slicing, analytical = _saved_receipts(out, saved_sources)
    previous_transfer = generation.get("validation_refresh", {})
    transfer = {
        "schema": 1, "verification_required": True,
        "previous_generation_snapshot_sha256": _digest(generation_bytes),
        "original_generation_snapshot_sha256": previous_transfer.get(
            "original_generation_snapshot_sha256", _digest(generation_bytes)),
        "original_sources": previous_transfer.get("original_sources", saved_sources),
        "changed_validation_sources": sorted(changed),
        "evidence": "Reused unchanged generated geometry; old acceptance removed; fresh verification required",
    }
    replacements = _rebind_manifest(manifest, current_sources, slicing, analytical)
    generation["sources"] = current_sources
    generation["validation_refresh"] = transfer
    generation["sha256"] = {name: _digest(replacements[name]) if name in replacements
                            else generation["sha256"][name]
                            for name in sorted(generated_export_names(manifest))}
    replacements[GENERATION_SNAPSHOT] = _json_bytes(generation)
    revision = manifest["revision"]
    if not isinstance(revision, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", revision):
        raise ValueError("Unsafe CAD package revision")
    with tempfile.TemporaryDirectory(prefix="f2-validation-refresh-", dir=out) as directory:
        staging = Path(directory)
        for name, content in replacements.items():
            (staging / name).write_bytes(content)
        if source_hashes() != current_sources:
            raise ValueError("Sources changed during generation refresh")
        _saved_receipts(out, saved_sources)
        # Invalidate every accepted-result transport before publishing metadata
        # for the new verifier. An interrupted refresh cannot retain a PASS.
        for name in ("verification.json", "artifact-sha256.json", "regeneration-cache.json",
                     "trusted-result.json", revision + "-kouzelny-kufr.zip", "package-sha256.txt"):
            (out / name).unlink(missing_ok=True)
        for name in replacements:
            (staging / name).replace(out / name)
    assert_generation_snapshot(out, manifest, current_sources)
    return True


def refresh(saved_sources, *, manufacturing_key):
    """Return True after rebinding presentation; False for an exact source hit.

    manufacturing_key is the caller's already verified fingerprint, including
    external mechanical inputs. It is recorded, never inferred from UI files.
    """
    current_sources = prepared_sources()
    changed = _assert_sources(saved_sources, current_sources)
    if not changed:
        return False
    if not isinstance(manufacturing_key, str) or not re.fullmatch(r"[a-f0-9]{64}", manufacturing_key):
        raise ValueError("Expected the verified manufacturing fingerprint")
    out = ROOT / "exports"
    report_bytes, report, manifest, generation_bytes, generation, slicing, analytical = _saved_receipts(out, saved_sources)
    previous_transfer = report.get("presentation_refresh", {})
    if previous_transfer and previous_transfer.get("manufacturing_key") != manufacturing_key:
        raise ValueError("Previous transfer has a different manufacturing fingerprint")
    transfer = {
        "schema": 1, "manufacturing_key": manufacturing_key,
        "mechanical_validation_performed": False,
        "previous_report_sha256": _digest(report_bytes),
        "original_report_sha256": previous_transfer.get("original_report_sha256", _digest(report_bytes)),
        "original_generation_snapshot_sha256": previous_transfer.get(
            "original_generation_snapshot_sha256", _digest(generation_bytes)),
        "original_sources": previous_transfer.get("original_sources", saved_sources),
        "changed_presentation_sources": sorted(changed),
        "evidence": "Transferred existing mechanical evidence; manufacturing inputs and CAD bytes unchanged",
    }
    payload = model_payload((out / "viewer.html").read_text(encoding="utf-8"))
    viewer_bytes = render_document(payload).encode("utf-8")
    rendered_payload = model_payload(viewer_bytes.decode("utf-8"))
    geometry_payload = {key: value for key, value in payload.items() if key not in {"showPlan", "showManifest"}}
    refreshed_geometry = {key: value for key, value in rendered_payload.items() if key not in {"showPlan", "showManifest"}}
    if geometry_payload != refreshed_geometry:
        raise ValueError("Presentation refresh changed saved model geometry or metadata")
    replacements = _rebind_manifest(manifest, current_sources, slicing, analytical)
    replacements["viewer.html"] = viewer_bytes
    generation["sources"] = current_sources
    generation["presentation_refresh"] = transfer
    generation["sha256"] = {name: _digest(replacements[name]) if name in replacements
                            else generation["sha256"][name]
                            for name in sorted(generated_export_names(manifest))}
    replacements[GENERATION_SNAPSHOT] = _json_bytes(generation)
    report["sources"] = current_sources
    report["presentation_refresh"] = transfer
    report["export_sha256"] = {name: _digest(replacements[name]) if name in replacements
                               else report["export_sha256"][name]
                               for name in export_names(manifest) if name not in VERIFICATION_EXPORTS}
    replacements["verification.json"] = _json_bytes(report)
    index = {**report["export_sha256"], "verification.json": _digest(replacements["verification.json"])}
    replacements["artifact-sha256.json"] = _json_bytes(index)
    expected = {**current_sources, **{"exports/" + name: digest for name, digest in index.items()},
                "exports/artifact-sha256.json": _digest(replacements["artifact-sha256.json"])}
    archive_replacements = {"exports/" + name: content for name, content in replacements.items()}
    files = package_files(ROOT, current_sources, manifest)
    revision = manifest["revision"]
    if not isinstance(revision, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", revision):
        raise ValueError("Unsafe CAD package revision")
    archive_name = revision + "-kouzelny-kufr.zip"
    with tempfile.TemporaryDirectory(prefix="f2-refresh-", dir=out) as directory:
        staging = Path(directory)
        archive_path = staging / archive_name
        _write_archive(archive_path, files, expected, archive_replacements)
        if source_hashes() != current_sources:
            raise ValueError("Sources changed during presentation refresh")
        # Recheck immutable saved exports before publishing a transferred receipt.
        _saved_receipts(out, saved_sources)
        for name, content in replacements.items():
            (staging / name).write_bytes(content)
        checksum = f"{_digest(archive_path.read_bytes())}  {archive_name}\n".encode("ascii")
        (staging / "package-sha256.txt").write_bytes(checksum)
        (out / "regeneration-cache.json").unlink(missing_ok=True)
        for name in [*replacements, archive_name, "package-sha256.txt"]:
            (staging / name).replace(out / name)
    assert_cad_hashes(ROOT, manifest, report)
    assert_verified_exports(out, manifest, report)
    return True
