"""Shared millimetre geometry. Origin: inner front-left floor; X right, Y rear, Z up."""
from __future__ import annotations

from dataclasses import dataclass, field, replace
import json
from pathlib import Path

import cadquery as cq

ROOT = Path(__file__).resolve().parent
COLORS = {"wood": "#916039", "print": "#a2a8af", "electronics": "#32885b",
          "drive": "#367fc2", "power": "#bd454d", "wand": "#bb75c8",
          "metal": "#858b95", "cable": "#dbb65b", "space": "#71c9d4"}


@dataclass
class Item:
    name: str
    shape: cq.Shape
    kind: str
    group: str
    part: str | None = None
    evidence: str = "design"
    metadata: dict = field(default_factory=dict)

    def translated(self, delta):
        return replace(self, shape=self.shape.translate(delta))


def box(x, y, z, at=(0, 0, 0)):
    """A box from its minimum corner, never an implicit centre."""
    return cq.Solid.makeBox(x, y, z, cq.Vector(*at))


def cylinder(radius, length, at=(0, 0, 0), axis=(0, 0, 1)):
    return cq.Solid.makeCylinder(radius, length, cq.Vector(*at), cq.Vector(*axis))


def drill(shape, points, diameter=3.4, z=-1, depth=200):
    for x, y in points:
        shape = shape.cut(cylinder(diameter / 2, depth, (x, y, z)))
    return shape.clean()


def bounds(shape):
    b = shape.BoundingBox()
    return [b.xmin, b.ymin, b.zmin, b.xmax, b.ymax, b.zmax]


def overlap(a, b, tolerance=0.05):
    aa, bb = bounds(a), bounds(b)
    if any(min(aa[i + 3], bb[i + 3]) - max(aa[i], bb[i]) <= tolerance for i in range(3)):
        return 0.0
    return a.intersect(b).Volume()


def load_parameters(path=None):
    p = json.loads(Path(path or ROOT / "parameters.json").read_text(encoding="utf-8"))
    c = p["case"]
    for key, value in c.items():
        if isinstance(value, (int, float)) and value <= 0:
            raise ValueError(f"case.{key} must be positive")
    if len(p["manufacturing"]["printer_bed_mm"]) != 3 or min(p["manufacturing"]["printer_bed_mm"]) <= 0:
        raise ValueError("Printer volume must be three positive dimensions")
    return p


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def assembly(items, path):
    result = cq.Assembly(name=path.stem)
    for item in items:
        color = COLORS.get(item.kind, COLORS["metal"])
        result.add(item.shape, name=item.name, color=cq.Color(color))
    path.parent.mkdir(parents=True, exist_ok=True)
    result.save(str(path))
    return result
