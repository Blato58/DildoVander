"""Select existing CI projects from changed repository paths."""

import argparse
import json
import os
from pathlib import PurePosixPath
import re
import subprocess


PROJECTS = ("repository", "backend", "firmware", "web", "ios", "ai-eval", "site", "cad")
BUILD_WORKFLOWS = {
    ".github/workflows/kufr-backend.yml": "backend",
    ".github/workflows/kufr-firmware.yml": "firmware",
    ".github/workflows/kufr-web.yml": "web",
    ".github/workflows/kufr-ios.yml": "ios",
    ".github/workflows/kufr-ios-sign.yml": "ios",
    ".github/workflows/kufr-ai-eval.yml": "ai-eval",
    ".github/workflows/kufr-site.yml": "site",
    ".github/workflows/kufr-cad.yml": "cad",
}

CAD_INPUTS = {
    "kufr/docs/zapojeni-potvrzeneho-hardwaru.md",
    "kufr/mechanika/rozmery-nakoupenych-soucastek.md",
    "scripts/ci/install-cad-slicer.sh",
    "kufr/mobilni-aplikace/.nvmrc",
}


def select_projects(paths):
    selected = dict.fromkeys(PROJECTS, False)
    for path in paths:
        parts = PurePosixPath(path).parts
        cad_input = path.startswith((
            "kufr/mechanika/final/", "kufr/design-system/", ".github/actions/verified-cad/",
        )) or path in CAD_INPUTS
        if not cad_input and ("docs" in parts or PurePosixPath(path).suffix.lower() in {".md", ".mdx", ".rst"}):
            continue
        if cad_input:
            selected["cad"] = True
        if path.startswith((".github/", "scripts/ci/")):
            selected["repository"] = True
        if path in BUILD_WORKFLOWS:
            selected[BUILD_WORKFLOWS[path]] = True
        if path in {"scripts/ci/package.py", "scripts/ci/test_package.py"}:
            for name in ("backend", "firmware", "web", "site"):
                selected[name] = True
        if path.startswith(("kufr/backend/", "kufr/backend-tests/")) or path in {
            "kufr/global.json", "kufr/Directory.Build.props", "NuGet.Config"
        }:
            selected["backend"] = True
        if path.startswith("kufr/firmware/"):
            selected["firmware"] = True
        if path.startswith("kufr/mobilni-aplikace/"):
            selected["web"] = True
            selected["ios"] = True
            selected["site"] = True
        if path.startswith(("kufr/web/", "kufr/mechanika/", "kufr/design-system/")):
            selected["site"] = True
        if path.startswith("kufr/design-system/"):
            selected["web"] = True
            selected["ios"] = True
        if path.startswith("kufr/ai-eval/"):
            selected["ai-eval"] = True
        if path.startswith("kufr/contracts/"):
            for name in ("backend", "firmware", "web", "ios", "site"):
                selected[name] = True
        if path.startswith("kufr/simulator/"):
            selected["firmware"] = selected["web"] = selected["ios"] = True
            if PurePosixPath(path).name.startswith("dashboard"):
                selected["site"] = True
    return selected


def changed_paths(base, head):
    if not all(re.fullmatch(r"[0-9a-f]{40}", sha or "") for sha in (base, head)):
        raise ValueError("Expected full base and head commit SHA")
    data = subprocess.check_output([
        "git", "diff", "--name-only", "--no-renames", "-z", f"{base}...{head}"
    ])
    return [path.decode("utf-8", errors="surrogateescape") for path in data.split(b"\0") if path]


def check_results(selected, jobs):
    if set(selected) != set(PROJECTS) or any(value not in ("true", "false") for value in selected.values()):
        raise ValueError("Missing or invalid project selection")
    if jobs.get("changes", {}).get("result") != "success":
        raise ValueError("Project selection failed")
    failed = []
    for name in PROJECTS:
        expected = "success" if selected[name] == "true" else "skipped"
        if jobs.get(name, {}).get("result") != expected:
            failed.append(name)
    if failed:
        raise ValueError("Unexpected CI results: " + ", ".join(failed))


def select_recovery_projects(project):
    selected = {name: project in ("all", name) for name in PROJECTS}
    # Recovery site builds already run full CAD verification before packaging.
    if project == "all":
        selected["cad"] = False
    return selected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("select", "check"))
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--project", choices=("all",) + PROJECTS)
    args = parser.parse_args()
    if args.mode == "check":
        check_results(json.loads(os.environ["SELECTED_PROJECTS"]), json.loads(os.environ["RESULTS"]))
        print("Every selected project passed; unrelated projects were skipped")
        return
    if args.project:
        selected = select_recovery_projects(args.project)
    else:
        selected = select_projects(changed_paths(args.base, args.head))
    print(json.dumps(selected, sort_keys=True))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for name, enabled in selected.items():
                output.write(f"{name}={str(enabled).lower()}\n")


if __name__ == "__main__":
    main()
