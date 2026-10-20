#!/usr/bin/env python3
"""Fingerprint product sources without Git state, secrets or generated exports."""
import argparse
import hashlib
import json
from pathlib import Path


def identity(root):
    files = set()
    for directory, suffixes in (
        ("firmware/src", {".cpp", ".h", ".txt"}),
        ("backend/Features", {".cs"}),
        ("firmware/simulator/native", {".cpp", ".h", ".py", ".cjs", ".txt"}),
        ("backend/deploy/twin", {".py", ".service", ".conf"}),
    ):
        files.update(path for path in (root / directory).rglob("*")
                     if path.is_file() and path.suffix in suffixes and "__pycache__" not in path.parts)
    files.update(root / name for name in ("backend/Program.cs", "backend/backend.csproj",
        "backend/packages.lock.json", "mechanika/final/effects.json"))
    files.update(path for path in (root / "mechanika/final").iterdir()
                 if path.is_file() and path.suffix in {".py", ".js", ".css", ".json"}
                 and not path.name.startswith("test_") and path.name != "fit-debug.json")
    digest = hashlib.sha256()
    inventory = []
    for path in sorted(files, key=lambda item: item.relative_to(root).as_posix()):
        name = path.relative_to(root).as_posix()
        data = path.read_bytes().replace(b"\r\n", b"\n")
        digest.update(name.encode("utf-8") + b"\0" + data + b"\0")
        inventory.append({"path": name, "sha256": hashlib.sha256(data).hexdigest()})
    return {"schemaVersion": 1, "algorithm": "sha256(path+NUL+LF-normalized-bytes+NUL), ordinal POSIX paths",
            "buildId": digest.hexdigest(), "files": inventory, "physicalVerification": "unavailable"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = identity(args.root.resolve())
    if args.output:
        args.output.write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))
    print(result["buildId"])


if __name__ == "__main__":
    main()
