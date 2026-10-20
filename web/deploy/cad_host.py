"""Portable model-only SSH publisher. Standard library; no service changes."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import urllib.request

from release_store import inventory, publish_model, rollback_model

HASH = re.compile(r"[a-f0-9]{64}")
CONFIG = Path("/etc/dildovander/cad.json")
LIMIT = 96 * 1024 * 1024


def read_json(path, limit=128 * 1024):
    if path.is_symlink() or not path.is_file() or path.stat().st_size > limit:
        raise ValueError("Missing, unsafe or oversized JSON: " + str(path))
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError("JSON grew beyond size limit")
    return json.loads(data)


def validate_bundle(bundle, channel="preview"):
    """Check transport integrity/schema after full viewer validation at the sender.

    This is not manufacturing verification or an authenticity certificate.
    SSH publisher authority is the trust boundary, as in release_store.py.
    """
    if bundle.is_symlink() or not bundle.is_dir():
        raise ValueError("Bundle must be a real directory")
    count, total_bytes = 0, 0
    for path in bundle.rglob("*"):
        count += 1
        if count > 128 or path.is_symlink() or not path.resolve().is_relative_to(bundle.resolve()):
            raise ValueError("Unsafe or oversized model inventory")
        if path.is_file():
            total_bytes += path.stat().st_size
        elif not path.is_dir():
            raise ValueError("Special model files are forbidden")
        if total_bytes > LIMIT + 128 * 1024:
            raise ValueError("Model directory exceeds size limit")
    files = inventory(bundle)
    manifest = read_json(bundle / "model-bundle.json")
    if not isinstance(manifest, dict):
        raise ValueError("Invalid model manifest schema")
    canonical = {k: v for k, v in manifest.items() if k != "bundleHash"}
    actual = hashlib.sha256(json.dumps(canonical, ensure_ascii=False, sort_keys=True,
                                      separators=(",", ":"), allow_nan=False).encode()).hexdigest()
    if (manifest.get("schemaVersion") != 1 or manifest.get("bundleHash") != actual
            or manifest.get("compatibility") != {"viewerSchema": 1}
            or manifest.get("units") != "mm" or manifest.get("coordinates") != "right-handed-z-up"):
        raise ValueError("Incompatible model manifest or bundle hash")
    for field in ("modelId", "revision"):
        if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.:-]{0,159}", manifest.get(field, "")):
            raise ValueError("Invalid model identity")
    provenance = manifest.get("provenance", {})
    if (not isinstance(provenance, dict) or provenance.get("kind") != "cad-export"
            or provenance.get("cadVerification") not in ("verified", "not-verified")
            or not isinstance(provenance.get("sourceRevision"), str)
            or not provenance["sourceRevision"]):
        raise ValueError("Only actual CAD exports may be published")
    if channel == "production" and provenance["cadVerification"] != "verified":
        raise ValueError("Production requires explicit CAD verification; publish to preview")
    assets = manifest.get("assets", [])
    if not isinstance(assets, list) or not 1 <= len(assets) <= 32:
        raise ValueError("Invalid model asset inventory")
    names, total, model, proof, model_asset = {"model-bundle.json"}, 0, None, None, None
    for asset in assets:
        if not isinstance(asset, dict):
            raise ValueError("Invalid model asset schema")
        name = asset.get("path", "")
        if (not re.fullmatch(r"[a-zA-Z0-9_-]+(?:[.][a-zA-Z0-9_-]+)*(?:/[a-zA-Z0-9_-]+(?:[.][a-zA-Z0-9_-]+)*)*", name)
                or name.lower() in {n.lower() for n in names}
                or asset.get("kind") not in ("model-json", "bom-json", "verification-json")
                or not HASH.fullmatch(asset.get("sha256", ""))
                or type(asset.get("bytes")) is not int or asset["bytes"] <= 0):
            raise ValueError("Invalid model asset")
        names.add(name)
        total += asset["bytes"]
        if total > LIMIT or files.get(name) != asset["sha256"] or (bundle / name).stat().st_size != asset["bytes"]:
            raise ValueError("Transferred model asset integrity differs")
        value = read_json(bundle / name, LIMIT)
        if asset["kind"] == "model-json":
            if model is not None:
                raise ValueError("Duplicate model asset")
            model, model_asset = value, asset
        if asset["kind"] == "verification-json":
            if proof is not None:
                raise ValueError("Duplicate verification certificate")
            proof = value
    if set(files) != names or not isinstance(model, dict):
        raise ValueError("Missing model or undeclared release files")
    if (model.get("modelId") != manifest["modelId"] or model.get("revision") != manifest["revision"]
            or not isinstance(model.get("meshes"), list) or not 1 <= len(model["meshes"]) <= 10000):
        raise ValueError("Invalid model data schema")
    verified = provenance["cadVerification"] == "verified"
    if verified != (proof is not None):
        raise ValueError("Verification certificate count differs")
    if verified:
        if not isinstance(proof, dict):
            raise ValueError("Invalid CAD verification certificate")
        evidence = proof.get("modelEvidence", {})
        if not isinstance(evidence, dict):
            raise ValueError("Invalid CAD verification evidence")
        sources = evidence.get("sourceHashes", {})
        if (proof.get("result") != "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE"
                or proof.get("physical_test_performed") is not False
                or evidence.get("schemaVersion") != 1 or evidence.get("modelId") != manifest["modelId"]
                or evidence.get("modelAssetSha256") != model_asset["sha256"]
                or evidence.get("sourceRevision") != provenance["sourceRevision"]
                or not isinstance(sources, dict) or not 1 <= len(sources) <= 512
                or any(not HASH.fullmatch(h) or proof.get("sources", {}).get(n) != h for n, h in sources.items())):
            raise ValueError("Unbound CAD verification certificate")
    return manifest


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise ValueError("Model readback redirects are forbidden")


def http_bytes(url, maximum):
    opener = urllib.request.build_opener(NoRedirect)
    with opener.open(urllib.request.Request(url, headers={"Cache-Control": "no-cache"}), timeout=20) as response:
        data = response.read(maximum + 1)
        if len(data) > maximum:
            raise ValueError("Oversized deployment readback")
        return data


def check_release(root, base, pointer):
    validate_pointer(pointer)
    observed = json.loads(http_bytes(base + "/active.json", 128 * 1024))
    if observed != pointer:
        raise ValueError("Active model HTTP readback differs")
    bundle = root / "bundles" / pointer["bundleHash"]
    for name, expected in inventory(bundle).items():
        maximum = (bundle / name).stat().st_size
        data = http_bytes(base + "/bundles/" + pointer["bundleHash"] + "/" + name, maximum)
        if hashlib.sha256(data).hexdigest() != expected:
            raise ValueError("Published model readback differs: " + name)


def validate_pointer(pointer):
    if (not isinstance(pointer, dict) or pointer.get("schemaVersion") != 1
            or not HASH.fullmatch(pointer.get("bundleHash", ""))
            or pointer.get("manifestPath") != f'bundles/{pointer["bundleHash"]}/model-bundle.json'):
        raise ValueError("Invalid active model pointer")


def configuration(channel):
    config = read_json(CONFIG)
    info = CONFIG.stat()
    if info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o022:
        raise ValueError("CAD host configuration must be root-owned and not writable by others")
    selected = config[channel]
    root = Path(selected["root"])
    base = selected["baseUrl"]
    if (config.get("schemaVersion") != 1 or selected.get("viewerSchema") != 1 or selected.get("enabled") is not True
            or not root.is_absolute() or root.is_symlink() or not root.is_dir()
            or not re.fullmatch(r"https://[a-zA-Z0-9.-]+/" + ("models" if channel == "production" else "model-previews"), base)):
        raise ValueError("Model host migration/compatibility has not been provisioned")
    roots = [Path(config[name]["root"]).resolve() for name in ("preview", "production")]
    if (roots[0].is_relative_to(roots[1]) or roots[1].is_relative_to(roots[0])
            or config["preview"]["baseUrl"] == config["production"]["baseUrl"]):
        raise ValueError("Preview and production stores must be separate")
    if (str(root.resolve()) != selected["root"]
            or any(parent.is_symlink() for parent in (root, *root.parents))):
        raise ValueError("Model root must be canonical and must not traverse symlinks")
    return root, base


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("status", "deploy", "rollback"))
    parser.add_argument("--channel", choices=("preview", "production"), default="preview")
    parser.add_argument("--source", type=Path)
    args = parser.parse_args()
    root, base = configuration(args.channel)
    validator = lambda bundle: validate_bundle(bundle, args.channel)
    check = lambda pointer: check_release(root, base, pointer)
    if args.operation == "status":
        pointer = read_json(root / "active.json") if (root / "active.json").exists() else None
        if pointer:
            validate_pointer(pointer)
            manifest = validator(root / "bundles" / pointer["bundleHash"])
            if manifest["bundleHash"] != pointer["bundleHash"] or manifest["modelId"] != pointer.get("modelId"):
                raise ValueError("Active pointer identity differs")
            check(pointer)
    elif args.operation == "deploy":
        if args.source is None:
            parser.error("--source required")
        pointer = publish_model(args.source, root, validator=validator, check=check)
    else:
        pointer = rollback_model(root, validator=validator, check=check)
    print(json.dumps({"channel": args.channel, "active": pointer, "baseUrl": base}))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise SystemExit("CAD deployment failed: " + str(error))
