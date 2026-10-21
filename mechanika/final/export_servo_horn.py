"""Export just the horn fit kit, without regenerating the rest of the suitcase."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

import cadquery as cq

from geometry import ROOT, Part, bounds, print_position
import manufacturing
import servo_horn
import slicer_validation
from source_inputs import file_hashes


def export_kit(out, slicer=None):
    out = Path(out).resolve()
    archive = out / "SG90-servopaka-fit-kit.zip"
    archive.unlink(missing_ok=True)
    for folder in ("stl", "step", "3mf"):
        (out / folder).mkdir(parents=True, exist_ok=True)
    parts = [Part(f"servo_horn_{round((4.95 + 2 * allowance) * 100):03d}",
                  servo_horn.horn(allowance), 1,
                  f"Prototype; socket maximum diameter {4.95 + 2 * allowance:.2f} mm; hand fit first",
                  servo_horn.PRINT_ROTATION)
             for allowance in servo_horn.FIT_ALLOWANCES]
    parts.append(servo_horn.parts()[1])
    rows = []
    for part in parts:
        printed = print_position(part.shape, part.rotation)
        if not printed.isValid() or len(printed.Solids()) != 1:
            raise ValueError(f"Invalid horn fit-kit part: {part.name}")
        for folder, extension in (("stl", "stl"), ("step", "step"), ("3mf", "3mf")):
            cq.exporters.export(printed, str(out / folder / f"{part.name}.{extension}"),
                                tolerance=.005, angularTolerance=.04)
        analysis = manufacturing.analyze(printed, manufacturing.ANALYTICAL_PROFILE)
        if analysis["status"] != "PASS":
            raise ValueError(f"Analytical print screen failed: {part.name}: {analysis}")
        rows.append({"name": part.name, "quantity": 1,
                     "bounds_mm": bounds(printed), "analytical_printability": analysis,
                     "manufacturing": manufacturing.record(part, out)})
    (out / "manifest.json").write_text(json.dumps({"parts": rows, "sources": file_hashes(ROOT),
                                                  "cadquery_version": cq.__version__,
                                                  "physical_fit": "UNVERIFIED"}, indent=2), encoding="utf-8")
    manufacturing.export_records(out, rows)
    report = slicer_validation.validate_exports(out, slicer)
    (out / "slicer-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    if slicer and report["status"] != "PASS_SLICER":
        raise ValueError("Horn fit-kit slicer validation failed; inspect local audit logs")
    shutil.copyfile(ROOT / "SERVO-HORN.md", out / "README.md")
    # Printer-independent kit only. Audit G-code is intentionally not shipped.
    files = [out / "README.md", out / "manifest.json", out / "printing-manifest.csv",
             out / "slicer-validation.json"]
    files.extend(out / folder / f"{part.name}.{extension}"
                 for folder, extension in (("stl","stl"),("step","step"),
                                           ("3mf","3mf"),("profiles","ini"))
                 for part in parts)
    hashes = {path.relative_to(out).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
              for path in files}
    (out / "sha256.json").write_text(json.dumps(hashes, indent=2), encoding="utf-8")
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as kit:
        for path in [*files, out / "sha256.json"]:
            kit.write(path, path.relative_to(out).as_posix())
    print(json.dumps({"kit": str(archive), "slicer_status": report["status"]}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=ROOT / "exports" / "servo-horn")
    parser.add_argument("--slicer", help="PrusaSlicer console executable for actual toolpath audit")
    arguments = parser.parse_args()
    export_kit(arguments.out, arguments.slicer)
