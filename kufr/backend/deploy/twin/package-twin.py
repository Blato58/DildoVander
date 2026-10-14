#!/usr/bin/env python3
"""Package verified backend publish, Linux native host and generated CAD viewer."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("publish", "native", "viewer", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--build-id", required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit("Use a new output directory; existing releases are immutable")
    if not (args.publish / "backend").is_file() or not args.native.is_file() or not args.viewer.is_file():
        raise SystemExit("Actual Linux publish, native executable and generated viewer required")
    cad_path = args.viewer.with_name("twin.manifest.json")
    cad = json.loads(cad_path.read_text(encoding="utf-8"))
    html = args.viewer.read_text(encoding="utf-8")
    key = '"twinManifest":'
    embedded, _ = json.JSONDecoder().raw_decode(html[html.index(key) + len(key):])
    if embedded != cad:
        raise SystemExit("Generated viewer and CAD manifest differ")
    shutil.copytree(args.publish, args.output)
    shutil.copy2(args.native, args.output / "native-twin")
    shutil.copy2(args.viewer, args.output / "viewer.html")
    shutil.copy2(cad_path, args.output / "twin.manifest.json")
    root = Path(__file__).resolve().parent
    for name in ("kufr-twin.service", "twin-locations.conf", "install-twin.py"):
        shutil.copy2(root / name, args.output / name)
    files = {str(path.relative_to(args.output)).replace("\\", "/"): hashlib.sha256(path.read_bytes()).hexdigest()
             for path in sorted(args.output.rglob("*")) if path.is_file()}
    manifest = {"schemaVersion": 1, "buildId": args.build_id, "files": files,
                "physicalVerification": "unavailable"}
    (args.output / "twin-release.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(args.output)


if __name__ == "__main__":
    main()
