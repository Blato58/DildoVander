"""Select existing CI projects from changed repository paths."""

import argparse
import json
import os
from pathlib import PurePosixPath
import re
import subprocess

from cad_impact import classify_path, impact_plan


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

def select_projects(paths):
    selected = dict.fromkeys(PROJECTS, False)
    for path in paths:
        parts = PurePosixPath(path).parts
        cad_impact = classify_path(path)
        if cad_impact == "none" and ("docs" in parts or PurePosixPath(path).suffix.lower() in {".md", ".mdx", ".rst"}):
            continue
        if cad_impact == "manufacturing":
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


def select_recovery_projects(project, force_cad=False):
    selected = {name: project in ("all", name) for name in PROJECTS}
    # Recovery site builds already run full CAD verification before packaging.
    if project == "all":
        selected["cad"] = False
    if force_cad and not selected["site"]:
        selected["cad"] = True
    return selected


def write_cad_summary(paths, selected, recovery=None):
    destination = os.environ.get("GITHUB_STEP_SUMMARY")
    if not destination:
        return
    plan = impact_plan(paths)
    causes = plan["causes"]["manufacturing"]
    with open(destination, "a", encoding="utf-8") as summary:
        summary.write("### CAD impact\n\n")
        summary.write(f"- Manufacturing job selected: {str(selected['cad']).lower()}\n")
        if recovery:
            summary.write(f"- Explicit recovery scope: {recovery}; site recovery verifies manufacturing once.\n")
        elif causes:
            summary.write("- Affected owners: " + ", ".join(plan["owners"]) + "\n")
            for cause in causes[:12]:
                summary.write("- Input: " + json.dumps(cause["path"], ensure_ascii=True) + "\n")
            if len(causes) > 12:
                summary.write(f"- {len(causes) - 12} additional manufacturing inputs.\n")
        else:
            summary.write("- No manufacturing dependency changed; CAD setup, geometry, renders, slicer and mechanical verification skipped.\n")
        if plan["causes"]["viewer"]:
            summary.write(f"- Viewer inputs: {len(plan['causes']['viewer'])}; focused viewer checks apply.\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("select", "check"))
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--project", choices=("all",) + PROJECTS)
    parser.add_argument("--force-cad", action="store_true")
    args = parser.parse_args()
    if args.mode == "check":
        check_results(json.loads(os.environ["SELECTED_PROJECTS"]), json.loads(os.environ["RESULTS"]))
        print("Every selected project passed; unrelated projects were skipped")
        return
    paths = []
    if args.project:
        selected = select_recovery_projects(args.project, args.force_cad)
    else:
        paths = changed_paths(args.base, args.head)
        selected = select_projects(paths)
    write_cad_summary(paths, selected, args.project)
    print(json.dumps(selected, sort_keys=True))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for name, enabled in selected.items():
                output.write(f"{name}={str(enabled).lower()}\n")


if __name__ == "__main__":
    main()
