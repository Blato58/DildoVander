"""Reuse verified F2 outputs and share CAD memoization within the test process."""
from __future__ import annotations

import argparse
import gc
from pathlib import Path
import subprocess
import sys
import time
import unittest


ROOT = Path(__file__).resolve().parent
TEST_MODULES = (
    "test_effects", "test_viewer_style", "test_packaging", "test_verification_gate",
    "test_inspect_data", "test_harness", "test_f2_cad", "test_sliding_follower",
    "test_floor_anchors", "test_follower_load", "test_strength", "test_assembly_access",
    "test_regeneration_cache", "test_regenerate",
)


def prepared_identity():
    import assembly_docs
    import inspect_data
    from build import source_hashes
    from regeneration_cache import runtime_environment

    assembly_docs.main()
    inspect_data.snapshot_sources()
    return {"sources": source_hashes(), "environment": runtime_environment()}


def stage(name, action):
    start = time.perf_counter()
    print(f"F2 start: {name}", flush=True)
    action()
    gc.collect()
    print(f"F2 complete: {name} ({time.perf_counter() - start:.2f}s)", flush=True)


def python_checks():
    # Keep the original module order and gates while sharing model lru_cache entries.
    for module in TEST_MODULES:
        def run_tests(name=module):
            suite = unittest.defaultTestLoader.loadTestsFromName(name)
            result = unittest.TextTestRunner(verbosity=1).run(suite)
            if not result.wasSuccessful():
                raise RuntimeError(f"F2 regression tests failed: {name}")
        stage(module, run_tests)
    import validate_assembly
    stage("validate_assembly", validate_assembly.validate)


def regenerate(force=False):
    from regeneration_cache import check_cache, write_cache

    out = ROOT / "exports"
    if not out.resolve().is_relative_to(ROOT.resolve()):
        raise RuntimeError("F2 exports directory escapes the source root")
    out.mkdir(exist_ok=True)
    for path in out.rglob("*"):
        if not path.resolve().is_relative_to(out.resolve()):
            raise RuntimeError(f"F2 output path escapes exports: {path}")
    lock = out / ".regeneration-lock"
    try:
        lock.mkdir()
    except FileExistsError as error:
        raise RuntimeError(
            f"F2 regeneration already running. Remove {lock} only after its process has stopped."
        ) from error
    try:
        identity = prepared_identity()
        # This header is outside the manufacturing package, so check it even on cache hits.
        subprocess.run([sys.executable, str(ROOT / "generate_effects.py"), "--check"], check=True)
        subprocess.run(["node", "--check", str(ROOT / "viewer.js")], check=True)
        subprocess.run(["node", "--test", str(ROOT / "test_viewer.cjs")], check=True)
        if not force and check_cache(ROOT, out, identity):
            if prepared_identity() != identity:
                raise RuntimeError("Sources changed while checking cached F2 outputs")
            print("F2 cache HIT: current sources, runtime and every verified output match.", flush=True)
            return

        print("F2 cache MISS: full tests, generation and verification required.", flush=True)
        stamp = out / "regeneration-cache.json"
        stamp.unlink(missing_ok=True)
        # Tessellation mutates native shape state. Keep tests and rendering away
        # from the verifier's pristine CAD objects, and release test caches first.
        commands = (
            ("python_checks", "regenerate.py", "--python-checks"),
            ("generate_exports", "artifacts.py", "--generate"),
            ("verify", "verify.py"),
            ("package", "package.py"),
        )
        for name, script, *arguments in commands:
            stage(name, lambda script=script, arguments=arguments: subprocess.run(
                [sys.executable, str(ROOT / script), *arguments], cwd=ROOT, check=True))
        if prepared_identity() != identity:
            raise RuntimeError("Sources or runtime changed during F2 regeneration")
        write_cache(ROOT, out, identity)
        if prepared_identity() != identity:
            stamp.unlink(missing_ok=True)
            raise RuntimeError("Sources changed while publishing the F2 cache receipt")
    finally:
        lock.rmdir()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--force", action="store_true", help="Run all gates even with valid cached outputs")
    parser.add_argument("--cache-key", action="store_true", help="Print the exact input/runtime cache key")
    parser.add_argument("--python-checks", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.python_checks:
        python_checks()
        return
    if args.cache_key:
        from regeneration_cache import cache_key
        print(cache_key(prepared_identity()))
        return
    start = time.perf_counter()
    regenerate(args.force)
    print(f"F2 regeneration total: {time.perf_counter() - start:.2f}s", flush=True)


if __name__ == "__main__":
    main()
