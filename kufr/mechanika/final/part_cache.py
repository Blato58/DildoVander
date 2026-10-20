"""Reuse CAD exports, never the assembly checks or their acceptance evidence.

This is a local optimization, not an authenticity boundary. Restored CI caches
must come from a trusted producer; checksums detect accidental damage only.
"""
from __future__ import annotations

import ast
import hashlib
from io import BytesIO
import json
import os
from pathlib import Path
import re
import shutil
import tempfile


FORMATS = ("stl", "step", "3mf")
DATA_SUFFIXES = {".json", ".csv", ".txt", ".ini", ".step", ".stp", ".stl", ".brep"}
SAFE_NAME = re.compile(r"[a-z][a-z0-9_]*")
SHA256 = re.compile(r"[a-f0-9]{64}")


def file_digest(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _json_bytes(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"),
                      ensure_ascii=True, allow_nan=False).encode("ascii")


def source_closure(root, module):
    """Hash complete local modules, including imports inside function bodies.

    Module-level constants and helper functions must invalidate their users too;
    a hash of the parts() function alone would miss those dependencies.
    """
    root = Path(root).resolve()
    pending = [module, "geometry"]
    sources = {}
    while pending:
        name = pending.pop()
        candidate = root.joinpath(*name.split(".")).with_suffix(".py")
        if not candidate.is_file():
            candidate = root.joinpath(*name.split("."), "__init__.py")
        if not candidate.is_file():
            continue  # External imports are covered by the runtime identity.
        if candidate.is_symlink() or not candidate.resolve().is_relative_to(root):
            raise ValueError("CAD source dependency escapes its source root")
        relative = candidate.relative_to(root).as_posix()
        if relative in sources:
            continue
        content = candidate.read_bytes()
        sources[relative] = hashlib.sha256(content).hexdigest()
        tree = ast.parse(content, filename=str(candidate))
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    segments = alias.name.split(".")
                    pending.extend(".".join(segments[:index])
                                   for index in range(1, len(segments) + 1))
            elif isinstance(node, ast.ImportFrom):
                prefix = node.module or ""
                if node.level:
                    package = name.split(".") if candidate.name == "__init__.py" else name.split(".")[:-1]
                    if node.level > len(package):
                        raise ValueError("Relative CAD import escapes its source root")
                    package = package[:len(package) - node.level + 1]
                    prefix = ".".join([*package, *filter(None, [prefix])])
                if prefix:
                    segments = prefix.split(".")
                    pending.extend(".".join(segments[:index])
                                   for index in range(1, len(segments) + 1))
                    pending.extend(prefix + "." + alias.name for alias in node.names
                                   if alias.name != "*")
    if module.replace(".", "/") + ".py" not in sources and module.replace(".", "/") + "/__init__.py" not in sources:
        raise ValueError("Missing CAD part owner: " + module)
    return sources


def group_identity(root, module, runtime):
    root = Path(root).resolve()
    sources = source_closure(root, module)
    # The exporter recipe is shared, but its unrelated imports must not join the
    # geometry closure (build.py imports all three independent part producers).
    for name in ("build.py", "part_cache.py", "requirements.txt", "parameters.json"):
        sources[name] = file_digest(root / name)
    for directory in (root, root / "reference"):
        for path in directory.glob("*"):
            if path.is_file() and path.suffix.lower() in DATA_SUFFIXES and path.name != "fit-debug.json":
                if path.is_symlink() or not path.resolve().is_relative_to(root):
                    raise ValueError("CAD data dependency escapes its source root")
                sources[path.relative_to(root).as_posix()] = file_digest(path)
    return {"schema": 1, "owner": module, "sources": sources, "runtime": runtime}


def shape_digest(shape):
    """Fingerprint geometry without mutable tessellation and OCC check flags."""
    stream = BytesIO()
    clean = shape.copy(mesh=False)
    pending = [clean]
    while pending:
        component = pending.pop()
        component.wrapped.Checked(False)
        pending.extend(component)
    if not clean.exportBrep(stream) or not stream.getbuffer().nbytes:
        raise ValueError("Cannot fingerprint CAD geometry")
    return hashlib.sha256(stream.getbuffer()).hexdigest()


class PartExportCache:
    """Stage verified export copies, then publish receipts after a successful build.

    Constructors still run, because manufacturing and assembly checks need live
    shapes. Only CAD export/tessellation work is reused. Force bypasses reads and
    refreshes entries after the same successful-build boundary.
    """

    def __init__(self, root, runtime, owners, force=False):
        self.root = Path(root).resolve()
        self.directory = self.root / "exports" / ".part-cache"
        self.runtime = runtime
        self.force = force
        self.groups = {owner: group_identity(self.root, owner, runtime) for owner in owners}
        self.pending = []
        self.staging = None
        self.used_keys = set()

    def key(self, owner, part, printed=None):
        if not SAFE_NAME.fullmatch(part.name):
            raise ValueError("Unsafe CAD part cache name")
        # Bind the actual live geometry as well as its source recipe. This
        # catches dynamic imports, mutable globals and code loaded just before
        # an on-disk source edit. BREP serialization does not tessellate a shape.
        shape = part.shape if printed is None else printed
        # CAD exporters attach tessellation to native shared shapes. Exclude
        # that mutable mesh state, or a previous cache miss changes later keys.
        shape_sha256 = shape_digest(shape)
        recipe = {"group": self.groups[owner], "part": part.name,
                  "rotation": part.rotation, "quantity": part.quantity, "reason": part.reason,
                  "shape_sha256": shape_sha256}
        key = hashlib.sha256(_json_bytes(recipe)).hexdigest()
        self.used_keys.add(key)
        return key

    def _entry(self, key):
        if not SHA256.fullmatch(key):
            raise ValueError("Invalid CAD part cache key")
        entry = self.directory / key
        if self.directory.is_symlink() or entry.is_symlink() or not entry.resolve().is_relative_to(self.root):
            raise ValueError("CAD part cache escapes its source root")
        return entry

    def restore(self, key, name, out):
        if self.force:
            return False
        if not SAFE_NAME.fullmatch(name):
            raise ValueError("Unsafe CAD part cache name")
        try:
            entry = self._entry(key)
            receipt_path = entry / "receipt.json"
            if receipt_path.is_symlink():
                return False
            receipt = json.loads(receipt_path.read_bytes())
            if receipt.get("schema") != 1 or receipt.get("key") != key or receipt.get("name") != name:
                return False
            files = receipt["files"]
            if set(files) != set(FORMATS):
                return False
            sources = {}
            for suffix in FORMATS:
                record = files[suffix]
                digest = record["sha256"]
                if not isinstance(digest, str) or not SHA256.fullmatch(digest):
                    return False
                source = entry / (digest + "." + suffix)
                if source.is_symlink() or source.stat().st_size != record["size"] or record["size"] <= 0:
                    return False
                if file_digest(source) != digest:
                    return False
                sources[suffix] = source
            # Validate and stage every file before publishing any restored file.
            with tempfile.TemporaryDirectory(prefix=".part-restore-", dir=out) as staging_name:
                staging = Path(staging_name)
                for suffix, source in sources.items():
                    staged = staging / suffix
                    shutil.copyfile(source, staged)
                    if file_digest(staged) != files[suffix]["sha256"]:
                        return False
                for suffix in FORMATS:
                    os.replace(staging / suffix, Path(out) / suffix / (name + "." + suffix))
            self.used_keys.add(key)
            return True
        except (OSError, ValueError, KeyError, TypeError, AttributeError):
            return False

    def remember(self, key, name, out):
        """Defer cache writes; an exporter or later build failure saves nothing."""
        self._entry(key)
        if not SAFE_NAME.fullmatch(name):
            raise ValueError("Unsafe CAD part cache name")
        self.used_keys.add(key)
        if self.staging is None:
            # Keep the original CQ 3MF: slicing later replaces the output with a
            # printer project. Never reuse that project as the raw CAD export.
            self.directory.parent.mkdir(parents=True, exist_ok=True)
            self.staging = tempfile.TemporaryDirectory(prefix=".part-cache-pending-", dir=self.directory.parent)
        snapshot = Path(self.staging.name) / key
        for suffix in FORMATS:
            destination = snapshot / suffix / (name + "." + suffix)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(Path(out) / suffix / (name + "." + suffix), destination)
        self.pending.append((key, name, snapshot))

    def publish(self):
        if any(group_identity(self.root, owner, self.runtime) != identity
               for owner, identity in self.groups.items()):
            raise ValueError("CAD inputs changed during part export; cache not published")
        for key, name, out in self.pending:
            entry = self._entry(key)
            entry.mkdir(parents=True, exist_ok=True)
            files = {}
            with tempfile.TemporaryDirectory(prefix=".part-write-", dir=entry) as staging_name:
                staging = Path(staging_name)
                for suffix in FORMATS:
                    source = out / suffix / (name + "." + suffix)
                    target = staging / suffix
                    shutil.copyfile(source, target)
                    digest = file_digest(target)
                    size = target.stat().st_size
                    if not size:
                        raise ValueError("Empty CAD export cannot enter the part cache")
                    files[suffix] = {"sha256": digest, "size": size}
                    os.replace(target, entry / (digest + "." + suffix))
                receipt = {"schema": 1, "key": key, "name": name, "files": files}
                temporary_receipt = staging / "receipt.json"
                temporary_receipt.write_bytes(_json_bytes(receipt))
                # Content-addressed immutable files are complete before the only
                # mutable pointer is atomically replaced, including on Windows.
                os.replace(temporary_receipt, entry / "receipt.json")
        self._prune_unused()
        self.pending.clear()
        if self.staging is not None:
            self.staging.cleanup()
            self.staging = None

    def _prune_unused(self):
        """Keep this build's export keys; old full packages live in CI artifacts."""
        if not self.directory.exists():
            return
        # Validate the cache root before inspecting or removing any descendant.
        self._entry("0" * 64)
        cache_root = self.directory.resolve()
        for candidate in self.directory.iterdir():
            if (not SHA256.fullmatch(candidate.name) or candidate.name in self.used_keys
                    or candidate.is_symlink() or not candidate.is_dir()):
                continue
            resolved = candidate.resolve()
            if resolved.parent != cache_root or not resolved.is_relative_to(cache_root):
                raise ValueError("Obsolete CAD part cache entry escapes its cache root")
            shutil.rmtree(candidate)
