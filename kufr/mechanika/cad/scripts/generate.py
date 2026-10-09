"""Reference -> components -> layout -> print parts -> checks -> real exports."""
from __future__ import annotations

import argparse
from collections import Counter
import csv
from dataclasses import replace
import hashlib
import json
import re
from pathlib import Path
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import cadquery as cq

from common import ROOT, Item, assembly, bounds, cylinder, load_parameters, overlap, write_json
from reference.case import build as case_build, rotate_lid, validate_and_export
from printable import frame, mechanics, service
from components import routes
from scripts import render, validate


def source_hashes():
    paths = list(ROOT.rglob("*.py")) + [ROOT / "parameters.json", ROOT.parent / "model.py", ROOT.parent / "parametry.json", ROOT.parent / "rozmery-nakoupenych-soucastek.md"]
    return {str(f.relative_to(ROOT.parent)): hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(paths)}


def physical(items):
    """Keep opaque clearance reservations out of the normal CAD display."""
    return [item for item in items if item.kind != "space"]


def check_lid(p, lid, contents):
    collisions = []
    for angle in range(int(p["case"]["lid_open_angle"]) + 1):
        for panel in lid:
            shape = rotate_lid(panel.shape, p, angle)
            for item in contents:
                if item.kind == "space":
                    continue
                vol = overlap(shape, item.shape)
                if vol > .1:
                    collisions.append([angle, panel.name, item.name, round(vol, 4)])
    return collisions


def export_parts(p, parts, instances, mech_names):
    results, rows = {}, []
    quantities = Counter(i.part for i in instances if i.part)
    for name, shape in parts.items():
        is_gauge = name in {"fit_coupon", "head_fit_gauge"}
        if not quantities[name] and not is_gauge:
            raise ValueError(f"Uninstanced production part in BOM: {name}")
        if not shape.isValid() or len(shape.Solids()) != 1 or shape.Volume() <= 0:
            raise ValueError(f"Invalid/disconnected printable: {name}")
        printed = mechanics.print_shape(name, shape, p) if name in mech_names else shape
        b = bounds(printed)
        printed = printed.translate(tuple(-x for x in b[:3]))
        b = bounds(printed)
        dims, bed = [b[i + 3] - b[i] for i in range(3)], p["manufacturing"]["printer_bed_mm"]
        if dims[0] > bed[0] or dims[1] > bed[1]:
            printed = printed.rotate((0, 0, 0), (0, 0, 1), 90)
            b = bounds(printed)
            printed = printed.translate(tuple(-x for x in b[:3]))
            dims = [b[i + 3] - b[i] for i in range(3)]
        if any(size > available + .001 for size, available in zip(dims, bed)):
            raise ValueError(f"{name} {dims} exceeds configured bed {bed}; segment/revise before exporting")
        path = ROOT / "exports" / "stl" / f"{name}.stl"
        path.parent.mkdir(parents=True, exist_ok=True)
        cq.exporters.export(printed, str(path), tolerance=p["manufacturing"]["stl_tolerance"], angularTolerance=p["manufacturing"]["stl_angular_tolerance"])
        step_path = ROOT / "exports" / "step" / f"{name}.step"
        step_path.parent.mkdir(parents=True, exist_ok=True)
        cq.exporters.export(printed, str(step_path))
        result = validate.stl(path, printed)
        result["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest()
        result.update(cad_volume_mm3=shape.Volume(), dimensions_mm=dims, quantity=1 if is_gauge else quantities[name],
                      status="PROVISIONAL; geometry verified; physical fit pending",
                      purpose=next((i.metadata.get("purpose") or i.metadata.get("mounting") for i in instances if i.part == name), None))
        results[name] = result
        rows.append(dict(file=f"{name}.stl", quantity=result["quantity"], x_mm=round(dims[0], 2),
                         y_mm=round(dims[1], 2), z_mm=round(dims[2], 2), material="PETG",
                         layer_mm=.2, perimeters=4, infill_percent=40,
                         orientation="As exported, Z0 on bed; inspect overhangs in slicer",
                         status="PROVISIONAL - physical fitting required", purpose=result["purpose"] or name))
    with (ROOT / "exports" / "print-bom.csv").open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    write_json(ROOT / "exports" / "stl-validation.json", results)
    return results


def clear_previous_generated_geometry():
    """These directories belong to this generator, never the legacy output folder."""
    base = ROOT.resolve()
    for relative in ["exports/stl", "exports/step", "assemblies", "components", "renders"]:
        folder = (base / relative).resolve()
        if not folder.is_relative_to(base):
            raise ValueError("Generated directory escapes CAD workspace")
        if not folder.exists():
            continue
        for path in folder.iterdir():
            if path.is_file() and path.suffix in {".stl", ".step", ".png", ".html"}:
                if not path.resolve().is_relative_to(base):
                    raise ValueError("Generated file escapes CAD workspace")
                path.unlink()


def package(include_renders=True):
    archive = ROOT / "exports" / "kufr-complete-provisional.zip"
    # Geometry directories were emptied before the current successful export.
    # Explicitly omit renders for a geometry-only generation.
    files = [f for f in ROOT.rglob("*") if f.is_file() and f.suffix in {".py", ".json", ".step", ".stl", ".md", ".csv", ".ps1"} and "__pycache__" not in f.parts]
    if include_renders:
        files += list((ROOT / "renders").glob("*.png")) + list((ROOT / "renders").glob("*.html"))
    files += [ROOT.parent / name for name in ["model.py", "parametry.json", "requirements.txt", "README.md", "rozmery-nakoupenych-soucastek.md", "rozmery-doruceneho-kufru.md"]]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(set(files)):
            z.write(path, Path("mechanika") / path.relative_to(ROOT.parent))
        for name in ["efekty-a-napajeni.md", "pin-mapa-esp32-s3.md", "kamera.md"]:
            path = ROOT.parent.parent / "docs" / name
            z.write(path, Path("docs") / name)
    print(f"ZIP: {archive} ({archive.stat().st_size:,} bytes)", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-renders", action="store_true")
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()
    archive = ROOT / "exports" / "kufr-complete-provisional.zip"
    if not args.check_only:
        for previous in [archive, archive.with_suffix(".zip.sha256"), ROOT / "exports/delivery-validation.json"]:
            if previous.exists():
                previous.unlink()
    p = load_parameters()
    reference_report = validate_and_export(p, ROOT)
    print("A PASS: reference case", flush=True)
    mech_parts, mech_items, mech_meta = mechanics.build(p)
    # Legacy clip is superseded by the front-rail mounted cross-bundle clip.
    mech_parts.pop("cable_clip")
    service_parts, service_items, service_meta = service.build(p)
    floor_holes = service_meta.get("floor_mounting_holes", [])
    if not floor_holes:
        floor_holes = [hole for mount in service_meta["mounts"] for hole in mount["mounting_holes"] if abs(hole[2] - 8) < .01]
    frame_parts, frame_items, frame_meta = frame.build(p, floor_holes)
    all_parts = {**mech_parts, **service_parts, **frame_parts}
    if len(all_parts) != len(mech_parts) + len(service_parts) + len(frame_parts):
        raise ValueError("Printable part name collision")
    route_items = routes.build(p)
    contents = frame_items + service_items + mech_items + route_items
    if len({i.name for i in contents}) != len(contents):
        raise ValueError("Assembly instance name collision")
    body, lid, _ = case_build(p)
    _, opened_lid, _ = case_build(p, p["case"]["lid_open_angle"])
    print("B/C: component and layout geometry built", flush=True)
    mechanical_report = mechanics.verification(p)
    print("D: mechanics verified", flush=True)
    static_report = validate.pairs(contents, service_meta.get("intended_collision_pairs", []))
    boundary_failures = validate.containment(contents, p)
    wood_hits = []
    for a in body:
        for b in contents:
            if b.kind == "space":
                continue
            vol = overlap(a.shape, b.shape)
            if vol > .1:
                wood_hits.append([a.name, b.name, round(vol, 4)])
    lid_hits = check_lid(p, lid, contents)
    # Internal module validation and invariant X-separation establish neighbour
    # independence. Check full hardware/frame integration at 21 physical poses.
    moving_hits = []
    for sample in range(21):
        angle = mech_meta["parameters"]["angle_low_deg"] + sample / 20 * (mech_meta["parameters"]["angle_high_deg"] - mech_meta["parameters"]["angle_low_deg"])
        for a in mechanics.pose(p, mech_items, angle):
            if a.metadata["motion"] == "fixed":
                continue
            for b in frame_items + service_items + route_items + opened_lid + body:
                if b.kind == "space" and b.metadata.get("role") != "cable_route":
                    continue
                vol = overlap(a.shape, b.shape)
                if vol > .1:
                    moving_hits.append([sample, a.name, b.name, round(vol, 4)])
    extraction_hits = []
    extraction = next((i for i in service_items if i.name == "powerbank_extraction"), None)
    if extraction:
        for i in contents + opened_lid:
            if i.kind == "space" or i.name in {"powerbank", "powerbank_carrier"}:
                continue
            vol = overlap(extraction.shape, i.shape)
            if vol > .1:
                extraction_hits.append([i.name, round(vol, 4)])
    route_hits = []
    for route in route_items:
        for item in contents + body:
            if item.kind == "space":
                continue
            vol = overlap(route.shape, item.shape)
            if vol > .1:
                route_hits.append([route.name, item.name, round(vol, 4)])
    local_access_hits = []
    for reserve in service_items:
        if reserve.kind != "space" or reserve.name in {"powerbank_extraction", "camera_optical_axis"}:
            continue
        for item in contents + body:
            if item.kind == "space":
                continue
            vol = overlap(reserve.shape, item.shape)
            if vol > .1:
                local_access_hits.append([reserve.name, item.name, round(vol, 4)])
    sightline_hits = []
    camera = service_meta["camera"]
    direction = camera["optical_direction"]
    ray_start = [v + .1 * direction[i] for i, v in enumerate(camera["optical_origin_mm"])]
    ray = cylinder(.1, 220, ray_start, direction)
    for item in contents + body + opened_lid:
        if item.kind == "space" or item.name == "camera_provisional":
            continue
        vol = overlap(ray, item.shape, tolerance=.0001)
        if vol > .001:
            sightline_hits.append([item.name, round(vol, 4)])
    report = dict(reference=reference_report, mechanics=mechanical_report,
                  runtime={"python": sys.version.split()[0], "cadquery": cq.__version__},
                  static=static_report, containment_failures=boundary_failures,
                  wood_intersections=wood_hits, lid_sweep_intersections=lid_hits,
                  motion_integration_intersections=moving_hits, powerbank_extraction_intersections=extraction_hits,
                  cable_corridor_intersections=route_hits,
                  local_access_reservation_intersections=local_access_hits,
                  nominal_camera_centre_ray_intersections=sightline_hits,
                  service=service_meta, frame=frame_meta, source_sha256=source_hashes(),
                  physical_acceptance=False, print_release="PROVISIONAL",
                  coverage={"stowed_rigid_pairs": "all", "mechanism_angles": 21, "closure_angles": 101,
                            "lid_angles": reference_report["sampled_lid_angles"],
                            "flexible_wiring": "routing allowance only; physical loom not validated",
                            "strength_and_servo_torque": "physical acceptance required",
                            "hardware_fasteners": "mechanism envelopes present; side mounting screws not fully modelled"})
    failures = static_report["collisions"] + boundary_failures + wood_hits + lid_hits + moving_hits + extraction_hits + route_hits + local_access_hits + sightline_hits
    report["status"] = "FAIL" if failures else "PASS_NOMINAL_RIGID_GEOMETRY"
    write_json(ROOT / "assemblies" / "validation.json", report)
    if failures:
        print(json.dumps(failures[:40], indent=2), flush=True)
        raise ValueError(f"{len(failures)} integration geometry failures; see assemblies/validation.json")
    print("E PASS: rigid geometry, stow/closure, travel and powerbank extraction", flush=True)
    previous_failure = ROOT / "exports" / "last-failure.json"
    if previous_failure.exists():
        previous_failure.unlink()
    if args.check_only:
        return
    clear_previous_generated_geometry()
    stl_report = export_parts(p, all_parts, contents, set(mech_parts))
    assembly(body + opened_lid + physical(contents), ROOT / "assemblies" / "complete-open.step")
    assembly(body + lid + physical(contents), ROOT / "assemblies" / "complete-closed.step")
    raised = mechanics.pose(p, mech_items, mech_meta["parameters"]["angle_high_deg"], module=3)
    assembly(body + opened_lid + physical(frame_items + service_items + raised), ROOT / "assemblies" / "presentation.step")
    assembly(physical(frame_items + service_items), ROOT / "assemblies" / "service-layer.step")
    assembly([i for i in contents if i.kind == "space"], ROOT / "assemblies" / "clearance-reservations.step")
    assembly([i for i in mech_items if i.metadata["module"] == 1], ROOT / "assemblies" / "single-compartment.step")
    hardware = [i for i in service_items + mech_items if not i.part and i.kind != "space"]
    assembly(hardware, ROOT / "components" / "installed-hardware.step")
    components = {}
    for item in hardware:
        key = item.metadata.get("canonical_name") or re.sub(r"_\d+$", "", item.name)
        if item.name.startswith("amplifier_"):
            key = "DFR0954_amplifier"
        elif item.name.startswith("resistor_"):
            key = "resistor_" + str(item.metadata.get("value_ohm", "catalog"))
        if key in components:
            continue
        filename = "".join(c if c.isalnum() or c in "_-" else "_" for c in key) + ".step"
        b = bounds(item.shape)
        cq.exporters.export(item.shape.translate(tuple(-x for x in b[:3])), str(ROOT / "components" / filename))
        components[key] = dict(filename=filename, evidence=item.evidence, metadata=item.metadata)
    write_json(ROOT / "components" / "inventory.json", components)
    write_json(ROOT / "assemblies" / "inventory.json", [dict(name=i.name, kind=i.kind, group=i.group, part=i.part, evidence=i.evidence, bounds_mm=bounds(i.shape), metadata=i.metadata) for i in contents])
    with (ROOT / "assemblies" / "placements.csv").open("w", newline="", encoding="utf-8-sig") as f:
        writer = csv.writer(f)
        writer.writerow(["instance", "kind", "part", "xmin", "ymin", "zmin", "xmax", "ymax", "zmax", "evidence"])
        for i in contents:
            writer.writerow([i.name, i.kind, i.part or "", *[round(x, 3) for x in bounds(i.shape)], i.evidence])
    exploded = []
    for i in contents:
        if i.group == "mechanism":
            delta = ((i.metadata["module"] - 3.5) * 15, 0, 160 if i.metadata["motion"] != "fixed" else 60)
        elif i.group == "frame":
            delta = (0, 0, 20)
        else:
            delta = (-90 if bounds(i.shape)[0] < p["case"]["inside_width"] / 2 else 90, 0, 90)
        exploded.append(i.translated(delta))
    assembly(body + [i.translated((0, 0, 100)) for i in opened_lid] + physical(exploded), ROOT / "assemblies" / "exploded.step")
    if not args.skip_renders:
        scenes = [
            ("01-empty-case", body + opened_lid, "01 / Empty reference chest", False),
            ("02-electronics", body + opened_lid + frame_items + service_items, "02 / Electronics in lower body", False),
            ("03-complete", body + opened_lid + contents, "03 / Complete assembly - stowed", False),
            ("04-top", body + contents, "04 / Top layout - lid hidden", True),
            ("05-presentation", body + opened_lid + frame_items + service_items + raised, "05 / Presentation - module 3 raised", False),
            ("06-closed", body + lid + contents, "06 / Closed transport envelope", False),
            ("07-exploded", body + [i.translated((0, 0, 100)) for i in opened_lid] + exploded, "07 / Exploded assembly", False),
            ("08-service", frame_items + service_items, "08 / Removable service structure", False),
        ]
        for name, objects, title, top in scenes:
            render.png(objects, ROOT / "renders" / f"{name}.png", title, top)
        render.html(body + opened_lid + contents, ROOT / "renders" / "viewer.html")
    report.update(stl_count=len(stl_report), assembly_instance_count=len(contents), exported=True,
                  renders_exported=not args.skip_renders)
    write_json(ROOT / "assemblies" / "validation.json", report)
    package(include_renders=not args.skip_renders)
    print(f"DONE: {len(stl_report)} verified STL; {len(contents)} assembly instances.", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (Exception, KeyboardInterrupt) as error:
        # Never leave an old archive presented as this failed generation.
        archive = ROOT / "exports" / "kufr-complete-provisional.zip"
        if archive.exists():
            archive.unlink()
        write_json(ROOT / "exports" / "last-failure.json", {"status": "FAIL", "error": str(error), "exported": False})
        raise
