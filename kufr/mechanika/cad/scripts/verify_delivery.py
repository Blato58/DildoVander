"""Read actual STEP exports back, check source freshness and package integrity."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cadquery as cq

from common import ROOT, bounds, load_parameters, write_json
from reference.case import build, rotate_lid
from scripts.generate import package, source_hashes
from scripts import validate


def main():
    report = json.loads((ROOT / "assemblies/validation.json").read_text(encoding="utf-8"))
    if report.get("status") != "PASS_NOMINAL_RIGID_GEOMETRY" or not report.get("exported"):
        raise ValueError("Successful full generation required first")
    if report["source_sha256"] != source_hashes():
        raise ValueError("Source changed since generation; regenerate before delivery")
    stls = json.loads((ROOT / "exports/stl-validation.json").read_text(encoding="utf-8"))
    if {path.stem for path in (ROOT / "exports/stl").glob("*.stl")} != set(stls):
        raise ValueError("STL filenames do not match validation report")
    imported = {}
    for name, expected in stls.items():
        shape = cq.importers.importStep(str(ROOT / "exports/step" / f"{name}.step")).val()
        if not shape.isValid() or len(shape.Solids()) != 1 or shape.Volume() <= 0:
            raise ValueError(f"Invalid STEP round trip: {name}")
        error = max(abs(a - b) for a, b in zip(bounds(shape), expected["bounds_mm"]))
        if error > .15 or abs(shape.Volume() - expected["cad_volume_mm3"]) > expected["cad_volume_mm3"] * .0001:
            raise ValueError(f"STEP/STL geometry mismatch: {name}")
        stl_path = ROOT / "exports/stl" / f"{name}.stl"
        if hashlib.sha256(stl_path.read_bytes()).hexdigest() != expected["sha256"]:
            raise ValueError(f"STL changed since generation: {name}")
        actual_stl = validate.stl(stl_path, shape)
        imported[name] = {"valid": True, "solids": 1, "max_stl_bound_error_mm": error,
                          "actual_stl_manifold": actual_stl["manifold_edges"], "stl_sha256": expected["sha256"]}
    assembly = cq.importers.importStep(str(ROOT / "assemblies/complete-open.step")).val()
    if not assembly.isValid() or assembly.Volume() <= 0:
        raise ValueError("Invalid complete assembly STEP")
    inventory = json.loads((ROOT / "assemblies/inventory.json").read_text(encoding="utf-8"))
    highest = max(i["bounds_mm"][5] for i in inventory if i["kind"] != "space")
    p = load_parameters()
    _, lid, _ = build(p)
    # Additional conservative certificate: the nominal moving metal hardware
    # stays entirely above every stowed internal solid at each sampled angle.
    minimum_gap = min(bounds(rotate_lid(i.shape, p, angle))[2] - highest
                      for angle in range(report["reference"]["sampled_lid_angles"])
                      for i in lid if i.group == "lid_hardware")
    if minimum_gap <= 0:
        raise ValueError("Moving lid hardware needs exact intersection testing for this parameter set")
    receipt = {"status": "PASS", "step_print_parts": imported,
               "complete_open_step_solid_count": len(assembly.Solids()),
               "source_hashes_match": True, "moving_lid_hardware_vertical_gap_mm": minimum_gap,
               "physical_acceptance": False}
    write_json(ROOT / "exports/delivery-validation.json", receipt)
    package(include_renders=report["renders_exported"])
    archive = ROOT / "exports/kufr-complete-provisional.zip"
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None:
            raise ValueError("ZIP CRC failure")
        expected_stls = {f"mechanika/cad/exports/stl/{name}.stl" for name in stls}
        if {n for n in z.namelist() if n.endswith(".stl")} != expected_stls:
            raise ValueError("ZIP STL inventory mismatch")
        for name, expected in stls.items():
            if hashlib.sha256(z.read(f"mechanika/cad/exports/stl/{name}.stl")).hexdigest() != expected["sha256"]:
                raise ValueError(f"ZIP STL changed: {name}")
        for relative, expected_hash in report["source_sha256"].items():
            member = "mechanika/" + relative.replace("\\", "/")
            if hashlib.sha256(z.read(member)).hexdigest() != expected_hash:
                raise ValueError(f"ZIP source mismatch: {relative}")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(digest + "  " + archive.name + "\n", encoding="ascii")
    print(f"PASS: {len(imported)} STEP parts read back; complete assembly valid; source hashes and ZIP CRC verified.")


def run():
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        for path in [ROOT / "exports/kufr-complete-provisional.zip", ROOT / "exports/kufr-complete-provisional.zip.sha256"]:
            if path.exists():
                path.unlink()
        write_json(ROOT / "exports/delivery-validation.json", {"status": "FAIL", "error": str(error)})
        raise


if __name__ == "__main__":
    run()
