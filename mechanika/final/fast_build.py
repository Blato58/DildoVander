"""Generation-only ModelBundles. No manufacturing, slicer, tests or PNG renders."""
from __future__ import annotations

from contextlib import contextmanager, ExitStack
import hashlib
import importlib.metadata
import json
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import time

from source_inputs import file_hashes, presentation_only, validation_only
from part_cache import file_digest, shape_digest
from export_model_bundle import write_model_bundle, source_revision

ROOT = Path(__file__).resolve().parent


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def identity(root=ROOT):
    # Conservative shared inventory; verification and UI edits do not rebuild geometry.
    sources = {name: digest for name, digest in file_hashes(root).items()
               if not presentation_only(name) and not validation_only(name)}
    for name in ("preview.py", "export_model_bundle.py", "fast_build.py", "part_cache.py"):
        sources[name] = file_digest(root / name)
    runtime = {"python": platform.python_version(), "platform": platform.platform(),
               "packages": sorted((dist.metadata.get("Name", ""), dist.version)
                                  for dist in importlib.metadata.distributions())}
    return {"schema": 1, "sources": sources, "runtime": runtime}


@contextmanager
def cache_lock(cache):
    cache.mkdir(parents=True, exist_ok=True)
    lock = cache / ".build-lock"
    try:
        lock.mkdir()
    except FileExistsError as error:
        raise ValueError(f"CAD build already running; inspect stale lock explicitly: {lock}") from error
    try:
        yield
    finally:
        lock.rmdir()


class MeshCache:
    """Per-shape tessellation reuse shares the manufacturing BREP fingerprint."""
    def __init__(self, directory, runtime, force=False, mesher=None):
        self.directory, self.runtime, self.force = directory, runtime, force
        directory.mkdir(parents=True, exist_ok=True)
        self.mesher = mesher or file_digest(ROOT / "preview.py")
        self.staging = tempfile.TemporaryDirectory(prefix=".pending-", dir=directory)
        self.hits = self.misses = 0

    def mesh(self, shape):
        recipe = {"shape": shape_digest(shape), "runtime": self.runtime,
                  "tolerance": [0.15, 0.15], "schema": 1,
                  "mesher": self.mesher}
        key = hashlib.sha256(encode(recipe)).hexdigest()
        pending = Path(self.staging.name) / (key + ".json")
        path = pending if pending.exists() else self.directory / (key + ".json")
        if not self.force and path.is_file() and not path.is_symlink():
            try:
                record = json.loads(path.read_bytes())
                data = record["mesh"]
                if record["sha256"] == hashlib.sha256(encode(data)).hexdigest():
                    self.hits += 1
                    return data
            except (OSError, ValueError, KeyError, TypeError):
                pass
        from preview import mesh
        data = mesh(shape)
        record = {"mesh": data, "sha256": hashlib.sha256(encode(data)).hexdigest()}
        temporary = pending.with_suffix(".tmp")
        temporary.write_bytes(encode(record))
        temporary.replace(pending)
        self.misses += 1
        return data

    def publish(self):
        # Bind cached facets to a successfully completed, source-stable build.
        for path in Path(self.staging.name).glob("*.json"):
            path.replace(self.directory / path.name)

    def discard(self):
        self.staging.cleanup()


def validate(bundle):
    repo = ROOT.parents[1]
    subprocess.run(["node", str(repo / "mobilni-aplikace/scripts/validate-model-bundle.mjs"),
                    "--bundle", str(bundle)], check=True)


def unverified_bundle(bundle):
    manifest = json.loads((bundle / "model-bundle.json").read_bytes())
    if (manifest["provenance"]["kind"] != "cad-export"
            or manifest["provenance"]["cadVerification"] != "not-verified"):
        raise ValueError("Generation cache must contain an unverified CAD export")
    return manifest


def build(cache: Path, output: Path, force=False, *, payload_factory=None, validate_bundle=validate):
    started = time.perf_counter()
    for path in (cache, output):
        if any(parent.is_symlink() for parent in (path, *path.parents)):
            raise ValueError("CAD cache/output paths must not traverse symbolic links")
    cache, output = cache.resolve(), output.resolve()
    if ROOT.is_relative_to(output):
        raise ValueError("Build output must not contain CAD source")
    if cache == ROOT or ROOT.is_relative_to(cache) or cache.is_relative_to(ROOT):
        raise ValueError("Persistent cache must be outside the CAD source tree")
    if cache == output or cache.is_relative_to(output) or output.is_relative_to(cache):
        raise ValueError("Build output and persistent cache must be separate directories")
    with cache_lock(cache), ExitStack() as cleanup:
        # Snapshot external source documents before hashing, without importing CAD.
        if payload_factory is None:
            import inspect_data
            inspect_data.snapshot_sources()
        before = identity()
        key = hashlib.sha256(encode(before)).hexdigest()
        entry = cache / "builds" / key
        reused = False
        if not force and entry.is_dir() and not entry.is_symlink():
            try:
                validate_bundle(entry)
                unverified_bundle(entry)
                reused = True
            except (OSError, ValueError, subprocess.CalledProcessError):
                print("CAD cache CORRUPT: rebuilding", flush=True)
        meshes = MeshCache(cache / "meshes", before["runtime"], force,
                           mesher=before["sources"].get("preview.py"))
        cleanup.callback(meshes.discard)
        if not reused:
            print("CAD cache MISS: generating scene and model data only", flush=True)
            if payload_factory is None:
                from preview import model_payload
                payload_factory = model_payload
            payload = payload_factory(meshes.mesh)
            entry.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.TemporaryDirectory(prefix=".build-", dir=entry.parent) as name:
                staged = Path(name) / "bundle"
                write_model_bundle(payload, staged, source_revision=source_revision())
                validate_bundle(staged)
                if identity() != before:
                    raise ValueError("CAD sources changed during generation; no output published")
                # Local cache is disposable, never an immutable publication store.
                if entry.exists():
                    shutil.rmtree(entry)
                staged.rename(entry)
        else:
            print("CAD cache HIT: geometry construction and tessellation skipped", flush=True)
        if identity() != before:
            raise ValueError("CAD sources changed during cache lookup")
        output.parent.mkdir(parents=True, exist_ok=True)
        if output.is_symlink():
            raise ValueError("Build output must not be a symlink")
        # Publish a complete directory; no mixture with a previously certified bundle.
        with tempfile.TemporaryDirectory(prefix=".cad-output-", dir=output.parent) as name:
            staged = Path(name) / "bundle"
            shutil.copytree(entry, staged)
            validate_bundle(staged)
            if identity() != before:
                raise ValueError("CAD sources changed during output validation; previous output preserved")
            previous = Path(name) / "previous"
            if output.exists():
                output.rename(previous)
            try:
                staged.rename(output)
            except Exception:
                if previous.exists():
                    previous.rename(output)
                raise
        try:
            meshes.publish()
        except OSError as error:
            # A valid completed output must not fail because an optional local
            # cache write ran out of space. Any already-published entries are valid.
            print(f"CAD mesh cache write skipped: {error}", flush=True)
        manifest = unverified_bundle(output)
        result = {"output": str(output), "bundleHash": manifest["bundleHash"], "cacheHit": reused,
                  "meshHits": meshes.hits, "meshMisses": meshes.misses,
                  "seconds": round(time.perf_counter() - started, 3), "verification": "not-verified"}
        print(json.dumps(result), flush=True)
        return result
