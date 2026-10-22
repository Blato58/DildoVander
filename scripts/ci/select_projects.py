"""Select existing CI projects from changed repository paths."""

import argparse
import hashlib
import json
import os
from pathlib import PurePosixPath
import re
import subprocess
import urllib.parse
import urllib.request

from cad_impact import classify_path


PROJECTS = ("repository", "backend", "firmware", "web", "ios", "ai-eval", "site")
BUILD_WORKFLOWS = {
    ".github/workflows/kufr-backend.yml": "backend",
    ".github/workflows/kufr-firmware.yml": "firmware",
    ".github/workflows/kufr-web.yml": "web",
    ".github/workflows/kufr-ios.yml": "ios",
    ".github/workflows/kufr-ios-sign.yml": "ios",
    ".github/workflows/kufr-ai-eval.yml": "ai-eval",
    ".github/workflows/kufr-site.yml": "site",
}
GITHUB_API_VERSION = "2026-03-10"


def stack_revision(repository, stack, position, base_ref, base_sha):
    selected = stack["pull_requests"][:position]
    candidate = {
        "repository": repository,
        "stack_id": stack["id"],
        "stack_number": stack["number"],
        "trunk": {"ref": base_ref, "sha": base_sha},
        "layers": [{"pr": entry["number"], "branch": entry["head"]["ref"],
                    "sha": entry["head"]["sha"]} for entry in selected],
    }
    encoded = json.dumps(candidate, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def is_ancestor(ancestor, descendant):
    result = subprocess.run(["git", "merge-base", "--is-ancestor", ancestor, descendant],
                            capture_output=True, check=False)
    if result.returncode == 0:
        return True
    if result.returncode == 1:
        return False
    raise ValueError("A native stack candidate commit is unavailable in the full-depth checkout")


def verify_native_stack(repository, pull_request, head_ref, head_sha, number, position, size,
                        base_ref, base_sha, token, ancestor_check=is_ancestor):
    if not token:
        raise ValueError("A GitHub token is required to verify native stack membership")
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": GITHUB_API_VERSION,
    }
    query = urllib.parse.urlencode({"pull_request": pull_request})
    request = urllib.request.Request(f"https://api.github.com/repos/{repository}/stacks?{query}", headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        stacks = json.load(response)
    if not isinstance(stacks, list) or len(stacks) != 1:
        raise ValueError("GitHub did not return one authoritative native stack")
    stack = stacks[0]
    entries = stack.get("pull_requests")
    if (stack.get("number") != number or stack.get("base", {}).get("ref") != base_ref
            or not isinstance(stack.get("id"), int) or not isinstance(entries, list)
            or len(entries) != size or not 1 <= position <= len(entries)):
        raise ValueError("Native stack identity, trunk or layer count changed")
    numbers = [entry.get("number") for entry in entries]
    if len(set(numbers)) != len(numbers) or numbers[position - 1] != pull_request:
        raise ValueError("PR is not at the expected position in the native stack")
    for entry in entries[:position]:
        if (not isinstance(entry.get("number"), int)
                or not isinstance(entry.get("head", {}).get("ref"), str)
                or not re.fullmatch(r"[0-9a-f]{40}", entry.get("head", {}).get("sha", ""))):
            raise ValueError("Native stack contains incomplete candidate metadata")
    current = entries[position - 1]
    if current.get("head", {}).get("ref") != head_ref or current["head"]["sha"] != head_sha:
        raise ValueError("Native stack candidate changed; fresh validation is required")
    if not re.fullmatch(r"[0-9a-f]{40}", base_sha or ""):
        raise ValueError("Native stack trunk SHA is invalid")
    branch = urllib.parse.quote(base_ref, safe="")
    trunk_request = urllib.request.Request(
        f"https://api.github.com/repos/{repository}/branches/{branch}", headers=headers)
    with urllib.request.urlopen(trunk_request, timeout=30) as response:
        trunk = json.load(response)
    if trunk.get("commit", {}).get("sha") != base_sha:
        raise ValueError("Native stack trunk changed; fresh validation is required")
    for entry in entries[:position - 1]:
        if not ancestor_check(entry["head"]["sha"], head_sha):
            raise ValueError("A lower native stack layer is not an ancestor of this candidate")
    stack["candidate_revision"] = stack_revision(repository, stack, position, base_ref, base_sha)
    return stack

def select_projects(paths):
    result = dict.fromkeys(PROJECTS, False)
    for path in paths:
        selected = dict.fromkeys(PROJECTS, False)
        parts = PurePosixPath(path).parts
        cad_impact = classify_path(path)
        if cad_impact == "none" and ("docs" in parts or PurePosixPath(path).suffix.lower() in {".md", ".mdx", ".rst"}):
            continue
        if path.startswith((".github/", "scripts/ci/")):
            selected["repository"] = True
        if path in {"scripts/cad.py", "scripts/cad.ps1"}:
            selected["repository"] = True
        if path in BUILD_WORKFLOWS:
            selected[BUILD_WORKFLOWS[path]] = True
        if path in {"scripts/ci/package.py", "scripts/ci/test_package.py"}:
            for name in ("backend", "firmware", "web", "site"):
                selected[name] = True
        if path.startswith(("backend/", "backend-tests/")) or path in {
            "global.json", "Directory.Build.props", "Directory.Build.targets",
            "Directory.Packages.props", "NuGet.Config"
        }:
            selected["backend"] = True
        if path.startswith("firmware/simulator/"):
            if PurePosixPath(path).name in {
                "dashboard.html", "dashboard.js", "dashboard-ui.js", "verify-dashboard.mjs",
            }:
                selected["site"] = True
            else:
                selected["firmware"] = selected["web"] = selected["ios"] = True
                if PurePosixPath(path).name.startswith("dashboard"):
                    selected["site"] = True
        elif path.startswith("firmware/diagnostics/servo-bench/web/") or path == "firmware/diagnostics/servo-bench/tools/serve_panel.py":
            selected["site"] = True
            if PurePosixPath(path).name in {"controller.mjs", "serial.mjs", "profiles.mjs", "auto-calibration.mjs", "mcp.mjs"}:
                selected["web"] = selected["ios"] = True
        elif path.startswith("firmware/"):
            selected["firmware"] = True
        if path in {"CMakeLists.txt", "platformio.ini", "firmware/CMakeLists.txt", "firmware/platformio.ini"}:
            selected["firmware"] = True
        if path.startswith("mobilni-aplikace/"):
            selected["web"] = True
            selected["ios"] = True
            selected["site"] = True
            if path.startswith("mobilni-aplikace/src/features/identity/") or PurePosixPath(path).name in {
                "accounts.browser.mjs", "accounts.fixture.browser.mjs"
            }:
                selected["backend"] = True
        if path.startswith(("web/", "design-system/")):
            selected["site"] = True
        if path.startswith("mechanika/"):
            # Geometry/model releases do not rebuild the shared application.
            selected["site" if cad_impact == "viewer" else "repository"] = True
        if path.startswith("design-system/"):
            selected["repository"] = True
            selected["web"] = True
            selected["ios"] = True
            # The self-contained archival card consumes generated C# tokens.
            if path.endswith("tokens.json"):
                selected["backend"] = True
        if path in {"scripts/generate_design_tokens.py", "scripts/check_design_system.py",
                    "scripts/test_design_system.py"}:
            selected["repository"] = True
        if path.startswith("ai-eval/"):
            selected["ai-eval"] = True
        if path.startswith("contracts/"):
            for name in ("backend", "firmware", "web", "ios", "site"):
                selected[name] = True
        # Unknown shared inputs conservatively run the local product checks.
        # Evaluate each path separately so an earlier recognized file cannot
        # hide an unclassified dependency. CAD retains its manual policy.
        if cad_impact == "none" and not any(selected.values()):
            for name in ("repository", "backend", "firmware", "web", "ios", "site"):
                selected[name] = True
        for name in PROJECTS:
            result[name] |= selected[name]
    return result


def changed_paths(base, head):
    if not all(re.fullmatch(r"[0-9a-f]{40}", sha or "") for sha in (base, head)):
        raise ValueError("Expected full base and head commit SHA")
    data = subprocess.check_output([
        "git", "diff", "--name-only", "--no-renames", "-z", f"{base}...{head}"
    ])
    return [path.decode("utf-8", errors="surrogateescape") for path in data.split(b"\0") if path]


def check_results(selected, jobs, native_stack_revision=None):
    if set(selected) != set(PROJECTS) or any(value not in ("true", "false") for value in selected.values()):
        raise ValueError("Missing or invalid project selection")
    if jobs.get("changes", {}).get("result") != "success":
        raise ValueError("Project selection failed")
    if native_stack_revision and jobs.get("stack-proof", {}).get("result") != "success":
        raise ValueError("Exact native stack proof job did not pass")
    failed = []
    for name in PROJECTS:
        expected = "success" if selected[name] == "true" else "skipped"
        if jobs.get(name, {}).get("result") != expected:
            failed.append(name)
    if failed:
        raise ValueError("Unexpected CI results: " + ", ".join(failed))


def select_recovery_projects(project):
    if project not in ("all",) + PROJECTS:
        raise ValueError("Unknown recovery project: " + project)
    return {name: project in ("all", name) for name in PROJECTS}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("select", "check"))
    parser.add_argument("--base")
    parser.add_argument("--head")
    parser.add_argument("--project", choices=("all",) + PROJECTS)
    parser.add_argument("--stack-pr", type=int)
    parser.add_argument("--stack-number", type=int)
    parser.add_argument("--stack-position", type=int)
    parser.add_argument("--stack-size", type=int)
    parser.add_argument("--stack-base-ref")
    parser.add_argument("--stack-base-sha")
    parser.add_argument("--stack-head-ref")
    args = parser.parse_args()
    if args.mode == "check":
        selected = json.loads(os.environ["SELECTED_PROJECTS"])
        results = json.loads(os.environ["RESULTS"])
        check_results({name: selected.get(name) for name in PROJECTS}, results,
                      os.environ.get("NATIVE_STACK_REVISION"))
        print("Every selected project passed; unrelated projects were skipped")
        return
    paths = []
    native_stack = None
    if args.project:
        selected = select_recovery_projects(args.project)
    else:
        if args.stack_pr is not None:
            native_stack = verify_native_stack(
                os.environ["GITHUB_REPOSITORY"], args.stack_pr, args.stack_head_ref, args.head,
                args.stack_number, args.stack_position, args.stack_size,
                args.stack_base_ref, args.stack_base_sha, os.environ.get("GITHUB_TOKEN"),
            )
        paths = changed_paths(args.base, args.head)
        selected = select_projects(paths)
    print(json.dumps(selected, sort_keys=True))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for name, enabled in selected.items():
                output.write(f"{name}={str(enabled).lower()}\n")
            if native_stack:
                output.write(f"native_stack_id={native_stack['id']}\n")
                output.write(f"native_stack_revision={native_stack['candidate_revision']}\n")
    if native_stack:
        print(f"Verified native stack id {native_stack['id']} and trunk-to-layer coverage")


if __name__ == "__main__":
    main()
