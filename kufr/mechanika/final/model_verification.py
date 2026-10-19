"""Model-only verification provenance. Standard library; never runs CAD or UI builds."""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
from pathlib import Path
from source_inputs import file_hashes, presentation_only, validation_only

MODEL_GENERATION = "model-generation.json"
HASH = re.compile(r"^[a-f0-9]{64}$")


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def model_source_hashes(root: Path) -> dict:
    """Use the shared conservative inventory; only reviewed presentation is exempt.

    Verifier and validation inputs remain bound to the proof even when their
    separate cache policy permits reusing unchanged geometry for fresh checks.
    """
    root = root.resolve()
    result = {}
    for name, digest in file_hashes(root).items():
        if presentation_only(name):
            continue
        if (root / name).is_symlink():
            raise ValueError("Model source cannot be a symbolic link")
        result[name] = digest
    if not result:
        raise ValueError("Empty mechanical/model source inventory")
    return dict(sorted(result.items()))


def manifest_identity(manifest: dict) -> str:
    data = {key: value for key, value in manifest.items() if key != "bundleHash"}
    return hashlib.sha256(json.dumps(data, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")).hexdigest()


def generation_receipt(bundle: Path, source_root: Path, *, allow_certified=False) -> dict:
    manifest = json.loads((bundle / "model-bundle.json").read_text(encoding="utf-8"))
    if manifest["provenance"]["kind"] != "cad-export" or (not allow_certified and manifest["provenance"]["cadVerification"] != "not-verified"):
        raise ValueError("Only actual, unverified CAD exports may enter model verification")
    model = next(asset for asset in manifest["assets"] if asset["kind"] == "model-json")
    if model["path"] != "model.json" or not HASH.fullmatch(model["sha256"]):
        raise ValueError("CAD pipeline requires the confined model.json asset")
    if manifest_identity(manifest) != manifest["bundleHash"] or file_hash(bundle / model["path"]) != model["sha256"] or (bundle / model["path"]).stat().st_size != model["bytes"]:
        raise ValueError("Model export changed before generation snapshot")
    return {"schemaVersion": 1, "modelId": manifest["modelId"], "modelAssetSha256": model["sha256"],
            "sourceRevision": manifest["provenance"]["sourceRevision"], "sourceHashes": model_source_hashes(source_root)}


def write_generation_receipt(bundle: Path, source_root: Path, output: Path) -> dict:
    value = generation_receipt(bundle, source_root)
    output.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return value


def copy_generated_model(staging: Path, out: Path):
    source = staging / "model-release"
    receipt = staging / MODEL_GENERATION
    if not source.exists() and not receipt.exists():
        return  # Historical generators may produce only the immutable manufacturing inventory.
    if not source.is_dir() or not receipt.is_file():
        raise ValueError("CAD generation omitted the independent model artifact")
    destination = out / "model-release"
    if not destination.resolve().is_relative_to(out.resolve()) or destination.is_symlink():
        raise ValueError("Model output escapes generation directory")
    destination.mkdir(exist_ok=True)
    for name in ("model.json", "model-bundle.json"):
        shutil.copyfile(source / name, destination / name)
    # This named build artifact is retired when the new generation has no proof.
    (destination / "cad-verification.json").unlink(missing_ok=True)
    shutil.copyfile(receipt, out / MODEL_GENERATION)


def evidence_for_success(out: Path, source_root: Path, full_sources: dict) -> dict:
    receipt_path = out / MODEL_GENERATION
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    current = generation_receipt(out / "model-release", source_root, allow_certified=True)
    if receipt != current or any(full_sources.get(key) != digest for key, digest in receipt["sourceHashes"].items()):
        raise ValueError("Model inputs or data changed since CAD generation")
    return {**receipt, "generationSnapshotSha256": file_hash(receipt_path)}


def prepare_validation_refresh(out: Path, source_root: Path, saved_sources: dict, current_sources: dict) -> dict:
    """Prepare local generation metadata, removing acceptance before fresh checks.

    Only refresh_package's authenticated validation-only transfer may use this.
    Published immutable bundles are outside source_root/exports and forbidden.
    Returned bytes are staged with the legacy generation metadata by the caller.
    """
    if out.resolve() != source_root.resolve() / "exports":
        raise ValueError("Model validation refresh is confined to working exports")
    bundle, receipt_path = out / "model-release", out / MODEL_GENERATION
    if not bundle.exists() and not receipt_path.exists():
        return {}  # Historical generation had no independent ModelBundle.
    if not bundle.is_dir() or not receipt_path.is_file() or bundle.is_symlink() or receipt_path.is_symlink():
        raise ValueError("Missing or unsafe independent model generation receipt")
    changed = {name for name in set(saved_sources) | set(current_sources) if saved_sources.get(name) != current_sources.get(name)}
    if not changed or any(not validation_only(name) for name in changed):
        raise ValueError("Model generation transfer requires validation-only input changes")
    for name in ("model-bundle.json", "model.json", "cad-verification.json"):
        if (bundle / name).is_symlink():
            raise ValueError("Symbolic links are forbidden in model validation refresh")
    receipt = json.loads(receipt_path.read_bytes())
    current = generation_receipt(bundle, source_root, allow_certified=True)
    old_model_sources = {name: digest for name, digest in saved_sources.items() if not presentation_only(name)}
    new_model_sources = {name: digest for name, digest in current_sources.items() if not presentation_only(name)}
    if (receipt != {**current, "sourceHashes": old_model_sources}
            or current["sourceHashes"] != new_model_sources):
        raise ValueError("Model data or generation inputs changed before validation refresh")
    manifest = json.loads((bundle / "model-bundle.json").read_bytes())
    proofs = [asset for asset in manifest["assets"] if asset["kind"] == "verification-json"]
    if any(asset["path"] != "cad-verification.json" for asset in proofs):
        raise ValueError("Unexpected model verification asset path")
    manifest["assets"] = [asset for asset in manifest["assets"] if asset["kind"] != "verification-json"]
    manifest["provenance"]["cadVerification"] = "not-verified"
    manifest["bundleHash"] = manifest_identity(manifest)
    encode = lambda value: (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    return {"model-release/model-bundle.json": encode(manifest), MODEL_GENERATION: encode(current)}


def certify_bundle(bundle: Path, report_path: Path, source_root: Path) -> dict:
    """Bind a real successful report to the exact export; no geometry is recomputed."""
    if report_path.stat().st_size > 32 * 1024 * 1024 or (bundle / "model-bundle.json").stat().st_size > 128 * 1024:
        raise ValueError("CAD verification input exceeds technical size limit")
    for name in ("model-bundle.json", "model.json", "cad-verification.json", "model-bundle.json.tmp"):
        if (bundle / name).is_symlink():
            raise ValueError("Symbolic links are forbidden in model certification")
    report_bytes = report_path.read_bytes()
    if len(report_bytes) > 32 * 1024 * 1024:
        raise ValueError("CAD verification report exceeds technical size limit")
    report = json.loads(report_bytes)
    manifest = json.loads((bundle / "model-bundle.json").read_text(encoding="utf-8"))
    if report.get("result") != "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE" or report.get("physical_test_performed") is not False:
        raise ValueError("CAD report did not pass or claims physical acceptance")
    if manifest["provenance"]["kind"] != "cad-export" or manifest_identity(manifest) != manifest["bundleHash"]:
        raise ValueError("Synthetic or modified manifests cannot be certified")
    evidence = report.get("modelEvidence", {})
    model = next(asset for asset in manifest["assets"] if asset["kind"] == "model-json")
    if model["path"] != "model.json" or not HASH.fullmatch(model["sha256"]):
        raise ValueError("CAD certification requires the confined model.json asset")
    sources = evidence.get("sourceHashes", {})
    if (evidence.get("schemaVersion") != 1 or evidence.get("modelId") != manifest["modelId"]
            or evidence.get("modelAssetSha256") != model["sha256"] or file_hash(bundle / model["path"]) != model["sha256"] or (bundle / model["path"]).stat().st_size != model["bytes"]
            or evidence.get("sourceRevision") != manifest["provenance"]["sourceRevision"]
            or not sources or sources != model_source_hashes(source_root)
            or any(report.get("sources", {}).get(name) != digest for name, digest in sources.items())):
        raise ValueError("CAD evidence does not bind current model inputs and exact asset bytes")
    proof = {"path": "cad-verification.json", "kind": "verification-json", "bytes": len(report_bytes), "sha256": hashlib.sha256(report_bytes).hexdigest()}
    if manifest["provenance"]["cadVerification"] == "verified":
        previous = [asset for asset in manifest["assets"] if asset["kind"] == "verification-json"]
        if previous == [proof] and file_hash(bundle / proof["path"]) == proof["sha256"]:
            return manifest
        raise ValueError("Verified model releases are immutable; certify an unverified generation copy")
    assets = [asset for asset in manifest["assets"] if asset["kind"] != "verification-json"]
    if sum(asset["bytes"] for asset in assets) + len(report_bytes) > 96 * 1024 * 1024:
        raise ValueError("Certified model exceeds total size limit")
    manifest["assets"] = [*assets, proof]
    manifest["provenance"]["cadVerification"] = "verified"
    manifest["bundleHash"] = manifest_identity(manifest)
    # The bundle is a local generation artifact. Durable publication is a separate atomic operation.
    (bundle / proof["path"]).write_bytes(report_bytes)
    temporary = bundle / "model-bundle.json.tmp"
    with temporary.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
    os.replace(temporary, bundle / "model-bundle.json")
    return manifest
