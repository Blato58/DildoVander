"""CAD regression checks; no export and no physical acceptance claim."""
from pathlib import Path
import math
import sys
import tempfile

import cadquery as cq

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from common import bounds, box, cylinder, load_parameters, overlap
from printable.service import build, print_shape
from scripts import validate as delivery_validation


def validate(p):
    parts, items, metadata = build(p)
    assert len({i.name for i in items}) == len(items)
    for name, shape in parts.items():
        assert shape.isValid(), name
        assert len(shape.Solids()) == 1, (name, len(shape.Solids()))
        b = bounds(print_shape(name, shape))
        bed = p["manufacturing"]["printer_bed_mm"]
        assert all(b[j + 3] - b[j] <= bed[j] + .001 for j in range(3)), (name, "printer", b)
    stl_reports = {}
    with tempfile.TemporaryDirectory(prefix="kufr-service-stl-") as directory:
        for name, shape in parts.items():
            oriented = print_shape(name, shape)
            b = bounds(oriented)
            printed = oriented.translate(tuple(-v for v in b[:3]))
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
    return {"print_parts": len(parts), "items": len(items), "powerbank_extraction": "clear",
            "retainer_checks": len(metadata["retainers"]), "verified_temporary_STLs": len(stl_reports)}


if __name__ == "__main__":
    print(validate(load_parameters()))
