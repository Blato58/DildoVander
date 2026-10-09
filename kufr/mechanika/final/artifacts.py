"""Explicit inventory for the current F2 manufacturing package."""
from pathlib import Path
import re
import hashlib


ASSEMBLY_NAMES = (
    "open_rest", "open_presenting", "service", "closed_transport", "one_module",
)
FIXED_EXPORTS = (
    "manifest.json", "verification.json", "collision-details.json", "artifact-sha256.json",
    "print-bom.csv", "installed-hardware.csv", "viewer.html", "reference-sources.html",
    "layout.svg", "layout.png", "kinematika.svg",
    "cable-plan.json", "cable-cut-list.csv", "cable-layout.svg", "cable-layout.png",
    "images/open.png", "images/service.png", "images/transport.png",
    "images/mechanism.png", "images/cam.png",
    "images/top.png", "images/wiring.png", "images/anchors.png",
)


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
          for folder, suffix in (("stl", "stl"), ("step", "step"))),
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


def assert_cad_hashes(root, manifest, report):
    """Refuse changed generated CAD, even when all source files are unchanged."""
    expected={name for name in export_names(manifest) if name.endswith(('.stl','.step'))}
    recorded=report['cad_sha256']
    if set(recorded)!=expected:
        raise ValueError('Verified CAD hash inventory differs from manifest')
    for name,digest in recorded.items():
        actual=hashlib.sha256((Path(root)/'exports'/name).read_bytes()).hexdigest()
        if actual!=digest:
            raise ValueError('Generated CAD changed after verification: '+name)
    for part in report['parts']:
        if recorded['stl/'+part['name']+'.stl']!=part['sha256']:
            raise ValueError('STL changed after mesh checks: '+part['name'])
