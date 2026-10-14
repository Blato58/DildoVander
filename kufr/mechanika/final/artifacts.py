"""Explicit inventory for the current F2 manufacturing package."""
from pathlib import Path
import re
import hashlib
import json
import shutil
import tempfile


ASSEMBLY_NAMES = (
    "open_rest", "open_presenting", "service", "closed_transport", "one_module",
)
FIXED_EXPORTS = (
    "manifest.json", "verification.json", "collision-details.json", "artifact-sha256.json",
    "generation-snapshot.json", "wire-collision-details.json",
    "print-bom.csv", "printing-manifest.csv", "printability.json", "slicer-validation.json",
    "support-free-petg.ini", "installed-hardware.csv", "viewer.html", "reference-sources.html",
    "layout.svg", "layout.png", "kinematika.svg",
    "cable-plan.json", "cable-cut-list.csv", "cable-layout.svg", "cable-layout.png",
    "images/open.png", "images/service.png", "images/transport.png",
    "images/mechanism.png", "images/cam.png",
    "images/top.png", "images/wiring.png", "images/anchors.png",
    "images/bed-exploded.png", "images/left_service_carrier-exploded.png",
    "images/right_service_carrier-exploded.png", "images/camera-exploded.png", "images/controls-exploded.png",
)
VERIFICATION_EXPORTS = {"verification.json", "artifact-sha256.json"}
AUDIT_EXPORTS = {"collision-details.json", "wire-collision-details.json"}
GENERATION_SNAPSHOT = "generation-snapshot.json"


def export_names(manifest):
    """Ignore leftovers; reject missing, duplicate or unsafe manifest entries."""
    assemblies = manifest["assemblies"]
    if len(assemblies) != len(ASSEMBLY_NAMES) or set(assemblies) != set(ASSEMBLY_NAMES):
        raise ValueError("Manifest does not contain the current assembly inventory")
    parts = [part["name"] for part in manifest["parts"]]
    if len(parts) != len(set(parts)) or not parts:
        raise ValueError("Manifest must contain unique printable parts")
    if any(not re.fullmatch(r"[a-z][a-z0-9_]*", name) for name in parts):
        raise ValueError("Unsafe printable part name")
    return sorted([
        *FIXED_EXPORTS,
        *(f"assembly/{name}.step" for name in ASSEMBLY_NAMES),
        *(f"{folder}/{name}.{suffix}" for name in parts
          for folder, suffix in (("stl", "stl"), ("step", "step"), ("3mf", "3mf"), ("profiles", "ini"))),
    ])


def package_files(root, source_names, manifest):
    """Select only verified sources and the explicit current export inventory."""
    root = Path(root).resolve()
    names = sorted(set(source_names)) + [f"exports/{name}" for name in export_names(manifest)]
    files = []
    for name in names:
        path = root / name
        if not path.resolve().is_relative_to(root):
            raise ValueError(f"Package member escapes source directory: {name}")
        if not path.is_file():
            raise FileNotFoundError(f"Current package member missing: {name}")
        files.append(path)
    return files


def generated_export_names(manifest):
    return set(export_names(manifest)) - VERIFICATION_EXPORTS - AUDIT_EXPORTS - {GENERATION_SNAPSHOT}


def file_sha256(path):
    """Hash large CAD and archive files without holding their bytes in memory."""
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def export_hashes(out, names):
    return {name: file_sha256(Path(out) / name)
            for name in sorted(names)}


def assert_export_hashes(out, expected, names):
    if set(expected) != set(names):
        raise ValueError("Verified export hash inventory differs from manifest")
    for name, digest in expected.items():
        actual = file_sha256(Path(out) / name)
        if actual != digest:
            raise ValueError("Generated export changed after snapshot: " + name)


def assert_generation_snapshot(out, manifest, sources):
    """Accept only outputs freshly generated together from the current sources."""
    snapshot = json.loads((Path(out) / GENERATION_SNAPSHOT).read_bytes())
    if snapshot.get("schema") != 1 or snapshot.get("sources") != sources or manifest["sources"] != sources:
        raise ValueError("Generated exports belong to stale sources; regenerate")
    assert_export_hashes(out, snapshot["sha256"], generated_export_names(manifest))
    return snapshot


def assert_verified_exports(out, manifest, report):
    names = set(export_names(manifest)) - VERIFICATION_EXPORTS
    assert_export_hashes(out, report["export_sha256"], names)
    assert_generation_snapshot(out, manifest, report["sources"])


def generate_exports(root, out, generators, source_hashes):
    """Run every generator in an empty directory; publish a source-bound receipt last.

    The generators must expose their existing OUT global and main() entry point.
    Staging prevents a successful but incomplete generator from blessing leftovers.
    """
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    for name in (GENERATION_SNAPSHOT, *VERIFICATION_EXPORTS):
        (out / name).unlink(missing_ok=True)
    initial_sources = source_hashes()
    with tempfile.TemporaryDirectory(prefix="f2-generation-", dir=Path(root).parent) as directory:
        staging = Path(directory) / "exports"
        previous_outputs = [(generator, generator.OUT) for generator in generators]
        try:
            for generator in generators:
                generator.OUT = staging
            for generator in generators:
                generator.main()
            manifest = json.loads((staging / "manifest.json").read_bytes())
            if source_hashes() != initial_sources or manifest["sources"] != initial_sources:
                raise ValueError("Sources changed during export generation; regenerate")
            hashes = export_hashes(staging, generated_export_names(manifest))
            # Delete only retired parts previously registered by this pipeline.
            # A fresh staging directory cannot remove old files in published OUT.
            old_manifest = out / "manifest.json"
            if old_manifest.is_file():
                old = json.loads(old_manifest.read_bytes())
                current = {part["name"] for part in manifest["parts"]}
                for part in old.get("parts", []):
                    name = part["name"]
                    if not re.fullmatch(r"[a-z][a-z0-9_]*", name):
                        raise ValueError("Unsafe retired printable part name")
                    if name not in current:
                        for folder, extension in (("stl", "stl"), ("step", "step"), ("3mf", "3mf"), ("profiles", "ini")):
                            retired = (out / folder / f"{name}.{extension}").resolve()
                            if not retired.is_relative_to(out.resolve()):
                                raise ValueError("Retired manufacturing file escapes output directory")
                            retired.unlink(missing_ok=True)
            for name in hashes:
                destination = out / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(staging / name, destination)
            # Preserve current local audit bytes, without shipping generic G-code.
            # Only names belonging to this manifest are copied from fresh staging.
            if (staging/'slicing').is_dir():
                (out/'slicing').mkdir(exist_ok=True)
                audit_names=['tool.log']+[part['name']+suffix for part in manifest['parts']
                    for suffix in ('.gcode','-effective.ini','-slice.log','-project.log')]
                for name in audit_names:
                    source=staging/'slicing'/name
                    if source.is_file():
                        shutil.copyfile(source,out/'slicing'/name)
            if source_hashes() != initial_sources:
                raise ValueError("Sources changed while publishing generated exports; regenerate")
            assert_export_hashes(out, hashes, generated_export_names(manifest))
            snapshot = {"schema": 1, "sources": initial_sources, "sha256": hashes}
            (out / GENERATION_SNAPSHOT).write_text(
                json.dumps(snapshot, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        finally:
            for generator, previous in previous_outputs:
                generator.OUT = previous


def main():
    import argparse
    parser = argparse.ArgumentParser(description="Generate the complete source-bound F2 export snapshot.")
    parser.add_argument("--generate", action="store_true", required=True)
    parser.parse_args()
    import build
    import preview
    import drawings
    import inspect_data
    # Refresh the repository evidence snapshot before capturing the input sources.
    inspect_data.snapshot_sources()
    generate_exports(build.ROOT, build.OUT, (build, preview, drawings), build.source_hashes)


def assert_cad_hashes(root, manifest, report):
    """Refuse changed generated CAD, even when all source files are unchanged."""
    expected={name for name in export_names(manifest) if name.endswith(('.stl','.step'))}
    recorded=report['cad_sha256']
    if set(recorded)!=expected:
        raise ValueError('Verified CAD hash inventory differs from manifest')
    for name,digest in recorded.items():
        actual=file_sha256(Path(root)/'exports'/name)
        if actual!=digest:
            raise ValueError('Generated CAD changed after verification: '+name)
    for part in report['parts']:
        if recorded['stl/'+part['name']+'.stl']!=part['sha256']:
            raise ValueError('STL changed after mesh checks: '+part['name'])


if __name__ == "__main__":
    main()
