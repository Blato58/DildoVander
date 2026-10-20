"""Export ModelBundle data independently of HTML and application releases.

The geometry pipeline calls write_model_bundle with its actual tessellations.
This module uses only the Python standard library; it never generates geometry.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import re
import subprocess
from pathlib import Path

IDENTITY = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
IDENTIFIER = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_.:-]{0,159}$")


def model_data(payload: dict, model_id: str) -> dict:
    result = copy.deepcopy(payload)
    result["modelId"] = model_id
    result.setdefault("revision", "synthetic-ui-fixture")
    names = set()
    for mesh in result["meshes"]:
        # CAD semantic names are stable across tessellation and array reordering.
        name = mesh["name"]
        if not IDENTIFIER.fullmatch(name) or name in names:
            raise ValueError(f"Invalid or duplicate semantic CAD name: {name}")
        names.add(name)
        mesh["id"] = name
        mesh["parentId"] = None
        mesh["transform"] = list(IDENTITY)
    result.setdefault("connections", [])
    result.setdefault("bomReferences", ["elektronika.csv", "mechanicky-material.csv"])
    result.setdefault("twinManifest", None)
    result["presentation"] = {"target": [215, 134.5, 65], "camera": "iso"}
    # Runtime receipts and live telemetry never belong in an immutable model.
    for key in ("snapshot", "telemetry", "recording"):
        result.pop(key, None)
    return result


def write_model_bundle(payload: dict, output: Path, *, source_revision: str,
                       model_id="dildovander-f2", fixture=False) -> dict:
    if not IDENTIFIER.fullmatch(model_id):
        raise ValueError("Invalid model id")
    model = model_data(payload, model_id)
    if fixture:
        model["twinManifest"] = None
    encoded = (json.dumps(model, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    digest = hashlib.sha256(encoded).hexdigest()
    manifest = {
        "schemaVersion": 1, "modelId": model_id, "revision": model["revision"],
        "units": "mm", "coordinates": "right-handed-z-up",
        "compatibility": {"viewerSchema": 1},
        "provenance": {"kind": "synthetic-fixture" if fixture else "cad-export",
                       "sourceRevision": source_revision, "cadVerification": "not-verified"},
        "assets": [{"kind": "model-json", "path": "model.json", "sha256": digest, "bytes": len(encoded)}],
    }
    canonical = json.dumps(manifest, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    manifest["bundleHash"] = hashlib.sha256(canonical).hexdigest()
    output.mkdir(parents=True, exist_ok=True)
    (output / "model.json").write_bytes(encoded)
    (output / "model-bundle.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def source_revision() -> str:
    return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=Path(__file__).parent, text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--fixture", action="store_true", help="Synthetic UI data only; no CAD dependencies")
    group.add_argument("--payload", type=Path, help="Existing CAD-exported JSON payload; never HTML")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.fixture:
        from preview_ui_fixture import fixture_payload
        payload = fixture_payload()
    else:
        payload = json.loads(args.payload.read_text(encoding="utf-8"))
    manifest = write_model_bundle(payload, args.output, source_revision=source_revision(), fixture=args.fixture,
                                  model_id="synthetic-ui-fixture" if args.fixture else "dildovander-f2")
    print(json.dumps({"modelId": manifest["modelId"], "bundleHash": manifest["bundleHash"], "output": str(args.output.resolve())}))


if __name__ == "__main__":
    main()
