"""Classify CAD changes without importing generators or installing CAD tools.

Only reviewed presentation leaves bypass manufacturing verification. New files
under final/ are manufacturing inputs until their dependency role is reviewed.
This policy selects checks; it never substitutes for artifact validation.
"""
import argparse
import importlib.util
import json
from pathlib import Path, PurePosixPath
import subprocess


ROOT = Path(__file__).resolve().parents[2]
FINAL = "mechanika/final/"
# Share the same small, standard-library-only dependency policy with the
# manufacturing fingerprint. Import by file so no CAD modules enter this job.
POLICY_PATH = ROOT / FINAL / "source_inputs.py"
POLICY_SPEC = importlib.util.spec_from_file_location("cad_source_inputs", POLICY_PATH)
SOURCE_POLICY = importlib.util.module_from_spec(POLICY_SPEC)
POLICY_SPEC.loader.exec_module(SOURCE_POLICY)
EXTERNAL_CAD_INPUTS = frozenset({
    # Snapshot and provenance documents are authoritative hardware evidence;
    # generic documentation filtering must never discard them.
    "docs/zapojeni-potvrzeneho-hardwaru.md",
    "mechanika/rozmery-nakoupenych-soucastek.md",
    "mechanika/rozmery-doruceneho-kufru.md",
    "mechanika/rozmery-hulek.md",
    "scripts/ci/install-cad-slicer.sh",
    "scripts/cad.py", "scripts/cad.ps1",
    "scripts/ci/cad_impact.py", "scripts/ci/test_cad_impact.py",
    ".github/workflows/kufr-cad-generate.yml",
})


def classify_path(path):
    """Return manufacturing, viewer, or none for a Git POSIX path."""
    if path in EXTERNAL_CAD_INPUTS or path.startswith(".github/actions/verified-cad/"):
        return "manufacturing"
    if path.startswith("design-system/"):
        return "viewer"
    if not path.startswith(FINAL):
        return "none"
    relative = str(PurePosixPath(path).relative_to(FINAL))
    if SOURCE_POLICY.presentation_only(relative):
        return "viewer"
    return "manufacturing"


def requires_manufacturing(paths):
    return any(classify_path(path) == "manufacturing" for path in paths)


def changed_paths(base="HEAD", head=None, root=ROOT):
    """Include deleted/renamed paths and every local snapshot without writes."""
    def git(*arguments):
        return subprocess.check_output(["git", "--no-optional-locks", "-C", str(root), *arguments])

    def commit(revision):
        return git("rev-parse", "--verify", "--end-of-options",
                   revision + "^{commit}").decode("ascii").strip()

    base_sha = commit(base)
    diff = ("diff", "--name-only", "--no-renames", "--no-ext-diff", "-z")
    if head is not None:
        inventories = [git(*diff, base_sha, commit(head), "--")]
    else:
        # Index and working tree can differ in opposite directions. Include
        # both snapshots, even when one restores a file to its base contents.
        inventories = [git(*diff, "--cached", base_sha, "--"),
                       git(*diff, base_sha, "--"),
                       git("ls-files", "--others", "--exclude-standard", "-z")]
    return sorted({path.decode("utf-8", errors="surrogateescape")
                   for inventory in inventories for path in inventory.split(b"\0") if path})


def owner_dependencies(root=ROOT):
    """Reuse the export cache's AST graph without importing any CAD module."""
    try:
        final = Path(root) / FINAL
        spec = importlib.util.spec_from_file_location("cad_part_cache", final / "part_cache.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return {owner: module.source_closure(final, owner)
                for owner in ("chassis", "mechanism", "layout")}
    except (OSError, SyntaxError, ValueError, AttributeError):
        # Missing/deleted inputs and in-progress syntax errors still need a
        # conservative plan; they must never become a no-impact result.
        return {}


def impact_plan(paths, root=ROOT):
    causes = {"manufacturing": [], "viewer": []}
    dependencies = None
    owners = set()
    unrelated = 0
    for path in sorted(set(paths)):
        kind = classify_path(path)
        if kind == "none":
            unrelated += 1
            continue
        if kind == "viewer":
            causes[kind].append(path)
            continue
        if dependencies is None:
            dependencies = owner_dependencies(root)
        relative = path[len(FINAL):] if path.startswith(FINAL) else None
        affected = sorted(owner for owner, sources in dependencies.items() if relative in sources)
        # Shared geometry/data, tooling and unknown dependencies require the
        # full manufacturing path. Owners only describe export reuse scope;
        # assembly/mechanics verification remains mandatory on a cache miss.
        if not affected or len(affected) == 3:
            affected = ["shared/full"]
        owners.update(affected)
        causes[kind].append({"path": path, "owners": affected})
    manufacturing = bool(causes["manufacturing"])
    checks = []
    if manufacturing:
        checks.append("manufacturing verification (trusted result or generation)")
    if causes["viewer"]:
        checks.append("viewer/style checks and presentation refresh when a trusted package exists")
    return {
        "manufacturing_required": manufacturing,
        "owners": ["shared/full"] if "shared/full" in owners else sorted(owners),
        "causes": causes,
        "unrelated_paths": unrelated,
        "checks": checks,
        "skipped_stages": [] if manufacturing else [
            "CAD tool setup", "geometry exports", "renders", "mechanical verification", "slicer",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="*", help="Repository-relative POSIX paths to simulate")
    parser.add_argument("--base", default="HEAD", help="Base commit/ref (default: HEAD)")
    parser.add_argument("--head", help="Compare commits instead of inspecting local changes")
    args = parser.parse_args()
    if args.paths and args.head:
        parser.error("Positional paths cannot be combined with --head")
    paths = args.paths or changed_paths(args.base, args.head)
    mode = "paths" if args.paths else "commits" if args.head else "worktree"
    print(json.dumps({"mode": mode, **impact_plan(paths)}, sort_keys=True, separators=(",", ":")))


if __name__ == "__main__":
    main()
