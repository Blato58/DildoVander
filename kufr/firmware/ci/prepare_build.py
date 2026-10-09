"""Reuse isolated self-hosted runner tools and content-addressed build outputs."""

from pathlib import Path
import hashlib
import os
import re
import subprocess
import sys
import time


PLATFORMIO_VERSION = "6.2.0"
CACHE_LIMIT_BYTES = 1024 ** 3
TEMPORARY_MAX_AGE_SECONDS = 3600
SIGNATURE = r"[0-9A-Fa-f]{32,64}"
CACHE_NAMESPACE = r"objects-v1|objects-v2-[0-9a-f]{16}"


def cache_entries(cache, now):
    if cache.is_symlink():
        raise ValueError("Compiler cache must be a real directory")
    if cache.exists():
        for bucket in cache.iterdir():
            if bucket.is_symlink() or not re.fullmatch(r"[0-9A-Fa-f]{2}", bucket.name):
                continue
            if not bucket.is_dir():
                continue
            for entry in bucket.iterdir():
                if entry.is_symlink():
                    continue
                if entry.is_file():
                    temporary = re.fullmatch(SIGNATURE + r"\.tmp[0-9a-fA-F-]{32,36}", entry.name)
                    if not temporary and not re.fullmatch(SIGNATURE, entry.name):
                        continue
                    stat = entry.stat()
                    stale = now - stat.st_mtime >= TEMPORARY_MAX_AGE_SECONDS
                    yield stat.st_mtime, stat.st_size, entry, not temporary or stale, bool(temporary)
                elif entry.is_dir() and re.fullmatch(r"tmp[0-9A-Za-z_-]{8}", entry.name):
                    # SCons 4.11 writes into TemporaryDirectory, while older
                    # versions used <signature>.tmp<uuid> files.
                    modified = entry.stat().st_mtime
                    for temporary in entry.iterdir():
                        if temporary.is_symlink() or not temporary.is_file():
                            continue
                        if not re.fullmatch(SIGNATURE, temporary.name):
                            continue
                        stat = temporary.stat()
                        age = now - max(modified, stat.st_mtime)
                        yield stat.st_mtime, stat.st_size, temporary, age >= TEMPORARY_MAX_AGE_SECONDS, True


def prune_caches(caches, limit=CACHE_LIMIT_BYTES, now=None):
    now = time.time() if now is None else now
    entries = [entry for cache in caches for entry in cache_entries(cache, now)]
    size = sum(entry[1] for entry in entries)
    removed = 0
    for _, entry_size, entry, removable, temporary in sorted(entries):
        if not removable or (size <= limit and not temporary):
            continue
        entry.unlink()
        size -= entry_size
        removed += 1
        if temporary and re.fullmatch(r"tmp[0-9A-Za-z_-]{8}", entry.parent.name):
            try:
                entry.parent.rmdir()
            except OSError:
                pass  # Preserve directories containing other files.
    print(f"Compiler cache: {size // (1024 ** 2)} MiB, pruned {removed} objects")
    if size > limit:
        raise ValueError("Recent cache writes exceed the limit; allow stale-write cleanup before building")


def prune_cache(cache, limit=CACHE_LIMIT_BYTES, now=None):
    prune_caches([cache], limit, now)


def cache_namespace(firmware, core):
    digest = hashlib.sha256(f"v2:{PLATFORMIO_VERSION}:{sys.implementation.cache_tag}".encode())
    paths = set()
    for pattern in (
        "platformio.ini", "dependencies.lock", "sdkconfig.defaults", "src/idf_component.yml",
        "diagnostics/*/platformio.ini", "diagnostics/*/dependencies.lock",
        "diagnostics/*/sdkconfig.defaults", "diagnostics/*/src/idf_component.yml",
    ):
        paths.update(firmware.glob(pattern))
    manifests = list(core.glob("platforms/*/platform.json")) + list(core.glob("packages/*/package.json"))
    for path in sorted(paths):
        digest.update(path.relative_to(firmware).as_posix().encode() + b"\0")
        digest.update(path.read_bytes())
    for path in sorted(manifests):
        digest.update(path.relative_to(core).as_posix().encode() + b"\0")
        digest.update(path.read_bytes())
    # Package versions protect upgrades; executable hashes also protect a
    # repaired/replaced compiler that kept the same package version and path.
    compilers = set(core.glob("packages/toolchain*/libexec/gcc/*/*/cc1*"))
    compilers.update(core.glob("packages/toolchain*/libexec/gcc/*/*/lto1*"))
    for path in core.glob("packages/toolchain*/bin/*"):
        if re.search(r"(?:gcc-ar|gcc-ranlib|gcc|g\+\+|as|ld|ar|ranlib)(?:-[0-9.]+)?(?:\.exe)?$", path.name):
            compilers.add(path)
    seen = set()
    for path in sorted(compilers):
        if not path.is_file() or path.resolve() in seen:
            continue
        seen.add(path.resolve())
        digest.update(path.relative_to(core).as_posix().encode() + b"\0")
        with path.open("rb") as source:
            for chunk in iter(lambda: source.read(1024 ** 2), b""):
                digest.update(chunk)
    return "objects-v2-" + digest.hexdigest()[:16]


def main():
    # Each runner has a separate HOME; never share SCons signature databases
    # or mutable PlatformIO installations between concurrently executing jobs.
    root = Path.home() / ".cache" / "kufr-firmware-ci"
    for directory in (root.parent, root):
        if directory.is_symlink():
            raise ValueError("Firmware cache directories must not be symlinks")
    caches = [] if not root.exists() else [
        path for path in root.iterdir()
        if re.fullmatch(CACHE_NAMESPACE, path.name) and path.is_dir() and not path.is_symlink()
    ]
    prune_caches(caches)
    if sys.argv[1:] == ["--prune-only"]:
        return
    if sys.argv[1:]:
        raise ValueError("Unknown argument")

    root.mkdir(parents=True, exist_ok=True)
    venv = root / f"platformio-{PLATFORMIO_VERSION}-{sys.implementation.cache_tag}"
    python = venv / "bin" / "python"
    pio = venv / "bin" / "pio"
    if not pio.is_file():
        subprocess.run([sys.executable, "-m", "venv", str(venv)], check=True)
        subprocess.run([
            str(python), "-m", "pip", "install", f"platformio=={PLATFORMIO_VERSION}"
        ], check=True)
    version = subprocess.check_output([str(pio), "--version"], text=True).strip()
    if version != f"PlatformIO Core, version {PLATFORMIO_VERSION}":
        raise ValueError("Persistent PlatformIO version does not match the pinned version")
    print(version)
    firmware = Path(__file__).resolve().parent.parent
    subprocess.run([str(pio), "pkg", "install", "-d", str(firmware)], check=True)
    core = Path(os.environ.get("PLATFORMIO_CORE_DIR", str(Path.home() / ".platformio")))
    cache = root / cache_namespace(firmware, core)
    if cache.is_symlink():
        raise ValueError("Compiler cache must be a real directory")
    cache.mkdir(exist_ok=True)
    script = Path(__file__).with_name("build_cache.py").resolve()
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
        output.write(f"KUFR_PIO={pio}\n")
        output.write(f"PLATFORMIO_BUILD_CACHE_DIR={cache}\n")
        output.write(f"PLATFORMIO_EXTRA_SCRIPTS=post:{script}\n")


if __name__ == "__main__":
    main()
