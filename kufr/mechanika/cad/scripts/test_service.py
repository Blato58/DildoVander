"""CAD regression checks; no export and no physical acceptance claim."""
from pathlib import Path
import math
import sys
import tempfile

import cadquery as cq

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from common import bounds, box, cylinder, load_parameters, overlap
from printable.service import build
from scripts import validate as delivery_validation


def validate(p):
    parts, items, metadata = build(p)
    assert len({i.name for i in items}) == len(items)
    for name, shape in parts.items():
        assert shape.isValid(), name
        assert len(shape.Solids()) == 1, (name, len(shape.Solids()))
        b = bounds(shape)
        assert min(b[:3]) >= -.001, (name, "canonical below bed", b)
        bed = p["manufacturing"]["printer_bed_mm"]
        assert all(b[j + 3] - b[j] <= bed[j] + .001 for j in range(3)), (name, "printer", b)
    stl_reports = {}
    with tempfile.TemporaryDirectory(prefix="kufr-service-stl-") as directory:
        for name, shape in parts.items():
            b = bounds(shape)
            printed = shape.translate(tuple(-v for v in b[:3]))
            path = Path(directory) / (name + ".stl")
            cq.exporters.export(printed, str(path), tolerance=p["manufacturing"]["stl_tolerance"],
                                angularTolerance=p["manufacturing"]["stl_angular_tolerance"])
            stl_reports[name] = delivery_validation.stl(path, printed)
    solids = [i for i in items if i.kind != "space"]
    for index, item in enumerate(solids):
        b = bounds(item.shape)
        assert min(b[:3]) >= -.001, (item.name, b)
        assert all(b[j + 3] <= p["case"][key] + .001
                   for j, key in enumerate(("inside_width", "inside_depth", "inside_height"))), (item.name, b)
        for other in solids[index + 1:]:
            assert overlap(item.shape, other.shape) <= .1, (item.name, other.name)
    extraction = next(i for i in items if i.name == "powerbank_extraction")
    for item in solids:
        assert overlap(item.shape, extraction.shape) <= .1, ("extraction", item.name)
    declared = metadata["floor_mounting_holes"]
    fasteners = metadata["floor_fasteners"]
    assert len(fasteners) == len(declared) == 54
    assert sorted(tuple(f["hole_mm"]) for f in fasteners) == sorted(tuple(h) for h in declared)
    for fastener in fasteners:
        x, y, _ = fastener["hole_mm"]
        carrier = next(i for i in solids if i.name == fastener["carrier"])
        nut = next(i for i in solids if i.name == fastener["nut"])
        shaft = next(i for i in solids if i.name == fastener["shaft"])
        assert overlap(carrier.shape, nut.shape) <= .1, ("nut fit", carrier.name, nut.name)
        assert overlap(carrier.shape, cylinder(1.69, 5.59, (x, y, 8))) <= .1, ("shaft clearance", carrier.name)
        assert fastener["floor_under_nut_mm"] >= 3
        bearing = cq.Workplane("XY").polygon(6, 5.5 / math.cos(math.pi / 6)).extrude(3).val().translate((x, y, 8))
        bearing = bearing.cut(cylinder(1.75, 4, (x, y, 7.5)))
        assert bearing.Volume() - bearing.intersect(carrier.shape).Volume() <= .1, ("nut bearing floor", carrier.name)
        b = fastener["insertion_bounds_mm"]
        insertion = box(b[3] - b[0], b[4] - b[1], b[5] - b[2], b[:3])
        for other in solids:
            if other.name not in (nut.name, shaft.name):
                assert overlap(insertion, other.shape) <= .1, ("nut insertion", nut.name, other.name)
    for retainer in metadata["retainers"]:
        assert retainer["retainer_width_mm"] <= retainer["tunnel_width_mm"]
        assert retainer["retainer_thickness_max_mm"] <= retainer["tunnel_height_mm"]
        paths = [box(b[3] - b[0], b[4] - b[1], b[5] - b[2], b[:3])
                 for b in retainer.get("paths_bounds_mm", [])]
        if "path_pose" in retainer:
            pose = retainer["path_pose"]
            for b in pose["local_bounds_mm"]:
                paths.append(box(b[3] - b[0], b[4] - b[1], b[5] - b[2], b[:3])
                             .rotate((0, 0, 0), (1, 0, 0), pose["rotation_x_degrees"])
                             .translate(pose["translation_mm"]))
        for path in paths:
            for other in solids:
                assert overlap(path, other.shape) <= .1, ("strap path", retainer["component"], other.name)
    adjustment = metadata["camera_adjustment"]
    pivot = adjustment["pivot_point_mm"]
    end = (pivot[0] + 1, pivot[1], pivot[2])
    moving = [i for i in solids if i.name in adjustment["moving_items"]]
    stationary = [i for i in solids if i.name not in adjustment["moving_items"]]
    for angle in (35, 40, 45, 50):
        for item in moving:
            shape = item.shape.rotate(pivot, end, angle - 45)
            assert bounds(shape)[5] <= p["case"]["inside_height"] + .001, ("angle height", angle, item.name)
            for other in stationary:
                assert overlap(shape, other.shape) <= .1, ("angle collision", angle, item.name, other.name)
    return {"print_parts": len(parts), "items": len(items), "floor_mount_holes": len(metadata["floor_mounting_holes"]),
            "camera_angles_checked": [35, 40, 45, 50], "powerbank_extraction": "clear",
            "M3_side_nut_pockets": len(fasteners), "retainer_width_checks": len(metadata["retainers"]),
            "verified_temporary_STLs": len(stl_reports), "rack_manifold_edges": stl_reports["right_service_rack"]["manifold_edges"]}


if __name__ == "__main__":
    print(validate(load_parameters()))
