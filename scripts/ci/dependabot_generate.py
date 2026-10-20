"""Regenerate dependency artifacts in a disposable, read-only-token checkout."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


OUTPUTS = {
    "backend": (
        "backend/packages.lock.json",
        "backend-tests/packages.lock.json",
    ),
    "native": (
        "mobilni-aplikace/ios/App/CapApp-SPM/Package.swift",
        "mobilni-aplikace/ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved",
    ),
}
MAX_CONTENT_BYTES = 512 * 1024


def command(arguments, cwd):
    executable = shutil.which(arguments[0])
    if executable is None:
        raise ValueError(f"Required generator is unavailable: {arguments[0]}")
    subprocess.run([executable, *arguments[1:]], cwd=cwd, check=True, timeout=600)


def regenerate(root, kind):
    if kind == "backend":
        arguments = [
            "dotnet", "restore", "backend-tests/BackendTests.csproj",
            "-p:SelfContained=true",
        ]
        # Preserve the projects' complete Linux/Windows RuntimeIdentifiers.
        command([*arguments, "--force-evaluate"], root)
        command([*arguments, "--locked-mode"], root)
    else:
        mobile = root / "mobilni-aplikace"
        command(["npm", "ci", "--ignore-scripts"], mobile)
        command(["npm", "ci", "--ignore-scripts", "--prefix", "../contracts"], mobile)
        command(["npm", "ci", "--ignore-scripts", "--prefix", "../firmware/simulator"], mobile)
        # Capacitor's SPM update can copy web assets too. Use the complete CI path.
        command(["npm", "run", "build"], mobile)
        command(["npx", "--no-install", "cap", "sync", "ios"], mobile)
        command([
            "xcodebuild", "-resolvePackageDependencies",
            "-project", "ios/App/App.xcodeproj", "-scheme", "App",
        ], mobile)


def payload(root, kind, plan):
    if (not isinstance(plan.get("number"), int) or isinstance(plan["number"], bool)
            or plan["number"] <= 0
            or not all(re.fullmatch(r"[a-f0-9]{40}", str(plan.get(key, "")))
                       for key in ("head", "base"))):
        raise ValueError("Expected a PR number and immutable head/base SHA")
    if not plan.get(kind):
        raise ValueError("Generator kind is not selected by the trusted plan")
    changed = subprocess.run(
        ["git", "diff", "--name-only", "HEAD", "--"], cwd=root,
        check=True, capture_output=True, text=True,
    ).stdout.splitlines()
    unexpected = set(changed) - set(OUTPUTS[kind])
    if unexpected:
        raise ValueError(f"Generator changed unexpected tracked files: {sorted(unexpected)}")
    files = []
    for name in OUTPUTS[kind]:
        path = root / name
        if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_CONTENT_BYTES:
            raise ValueError(f"Expected a bounded regular generated file: {name}")
        content = path.read_text(encoding="utf-8")
        files.append({"path": name, "content": content})
    return {"version": 1, "number": plan["number"], "head": plan["head"],
            "base": plan["base"], "kind": kind, "files": files}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=OUTPUTS)
    parser.add_argument("--checkout", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.checkout.resolve(strict=True)
    plan = json.loads(os.environ["DEPENDABOT_PLAN"])
    actual_head = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=root, check=True,
        capture_output=True, text=True,
    ).stdout.strip()
    if actual_head != plan["head"]:
        raise ValueError("Generator checkout differs from the planned PR head")
    regenerate(root, args.kind)
    result = payload(root, args.kind, plan)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(result, indent=2) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
