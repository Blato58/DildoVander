#!/usr/bin/env python3
"""Combine already-built backend and guest web with provenance; never deploy."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil


def private_path(path):
    name = path.name.lower()
    private_suffixes = (".sqlite", ".sqlite-wal", ".sqlite-shm", ".db", ".env", ".pem", ".key", ".pfx")
    return (path.is_symlink() or name.startswith(".env")
            or (name.startswith("key-") and name.endswith(".xml"))
            or name.endswith(private_suffixes)
            or name in ("archive", "keepsake-data", "keyring", "keys"))


def package(backend, web, output, revision):
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Require exact source commit SHA")
    if backend.is_symlink() or web.is_symlink():
        raise ValueError("Source root must not be a symlink")
    if output.exists() or not (backend / "backend").is_file() or not (web / "index.html").is_file():
        raise ValueError("Require published Linux backend, built web, and new output directory")
    if any(output.resolve().is_relative_to(root.resolve()) for root in (backend, web)):
        raise ValueError("Release output must be separate from build inputs")
    site_manifest = web / "site-manifest.json"
    standalone_manifest = web / "keepsake-manifest.json"
    if site_manifest.exists() and standalone_manifest.exists():
        raise ValueError("Web build has ambiguous provenance manifests")
    web_manifest = site_manifest if site_manifest.exists() else standalone_manifest
    if web_manifest.is_symlink():
        raise ValueError("Web manifest must not be a symlink")
    if web_manifest.is_file():
        provenance = json.loads(web_manifest.read_text(encoding="utf-8"))
        if web_manifest == site_manifest:
            if provenance.get("schema") != 1 or provenance.get("source_sha") != revision:
                raise ValueError("Full site must come from the same committed source")
        elif (provenance.get("schemaVersion") != 1 or provenance.get("sourceRevision") != revision
              or provenance.get("sourceDirty") is not False):
            raise ValueError("Standalone web must come from the same clean committed source")
        actual = {}
        for path in sorted(web.rglob("*")):
            if private_path(path):
                raise ValueError("Persistent data/credentials must not be in a code release")
            if path.is_file() and path != web_manifest:
                with path.open("rb") as stream:
                    actual[path.relative_to(web).as_posix()] = hashlib.file_digest(stream, "sha256").hexdigest()
        if provenance.get("sha256") != actual:
            raise ValueError("Web inventory differs from its build manifest")
    sources = []
    # web is a public root, including /keepsake and /design-system. Preserve it
    # verbatim instead of nesting the root under the portal's URL directory.
    for component, root in (("backend", backend), ("web", web)):
        if root.is_symlink():
            raise ValueError("Source root must not be a symlink")
        for path in sorted(root.rglob("*")):
            if private_path(path):
                raise ValueError("Persistent data/credentials must not be in a code release")
            if path.is_file():
                sources.append((component, root, path))
            elif not path.is_dir():
                raise ValueError("Special files are forbidden in code releases")
    output.mkdir(parents=True)
    try:
        files = {}
        for component, root, path in sources:
            relative = Path(component) / path.relative_to(root)
            target = output / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
            with target.open("rb") as stream:
                files[relative.as_posix()] = hashlib.file_digest(stream, "sha256").hexdigest()
        (output / "release-manifest.json").write_text(json.dumps({"schemaVersion": 1, "sourceRevision": revision, "retentionMode": "manual_only", "activationOrder": ["compatible-backend", "keepsake-web"], "sha256": files}, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except Exception:
        shutil.rmtree(output)
        raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("backend", "web", "output"):
        parser.add_argument("--" + option, type=Path, required=True)
    parser.add_argument("--revision", required=True)
    args = parser.parse_args()
    package(args.backend, args.web, args.output, args.revision)
