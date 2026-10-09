"""Reuse isolated self-hosted runner tools and content-addressed build outputs."""

from pathlib import Path
import os
import re
import subprocess
import sys


PLATFORMIO_VERSION = "6.2.0"
CACHE_LIMIT_BYTES = 1024 ** 3


def prune_cache(cache, limit=CACHE_LIMIT_BYTES):
    if cache.is_symlink():
        raise ValueError("Compiler cache must be a real directory")
    entries = []
    if cache.exists():
        for bucket in cache.iterdir():
            if bucket.is_symlink() or not re.fullmatch(r"[0-9A-Fa-f]{2}", bucket.name):
                continue
            if not bucket.is_dir():
                continue
            for entry in bucket.iterdir():
                if entry.is_symlink() or not re.fullmatch(r"[0-9A-Fa-f]{32,64}", entry.name):
                    continue
                if entry.is_file():
                    stat = entry.stat()
                    entries.append((stat.st_mtime, stat.st_size, entry))
    size = sum(entry[1] for entry in entries)
    removed = 0
    for _, entry_size, entry in sorted(entries):
        if size <= limit:
            break
        entry.unlink()
        size -= entry_size
        removed += 1
    print(f"Compiler cache: {size // (1024 ** 2)} MiB, pruned {removed} objects")


def main():
    # Each runner has a separate HOME; never share SCons signature databases
    # or mutable PlatformIO installations between concurrently executing jobs.
    root = Path.home() / ".cache" / "kufr-firmware-ci"
    cache = root / "objects-v1"
    for directory in (root.parent, root, cache):
        if directory.is_symlink():
            raise ValueError("Firmware cache directories must not be symlinks")
    prune_cache(cache)
    if sys.argv[1:] == ["--prune-only"]:
        return
    if sys.argv[1:]:
        raise ValueError("Unknown argument")

    cache.mkdir(parents=True, exist_ok=True)
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
    script = Path(__file__).with_name("build_cache.py").resolve()
    with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
        output.write(f"KUFR_PIO={pio}\n")
        output.write(f"PLATFORMIO_BUILD_CACHE_DIR={cache}\n")
        output.write(f"PLATFORMIO_EXTRA_SCRIPTS=post:{script}\n")


if __name__ == "__main__":
    main()
