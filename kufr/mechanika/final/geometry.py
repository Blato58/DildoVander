"""Original F1 CAD primitives and explicit assembly records, millimetres."""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import cadquery as cq

ROOT = Path(__file__).resolve().parent
PARAMS = json.loads((ROOT / "parameters.json").read_text(encoding="utf-8"))


def box(x, y, z, origin=(0, 0, 0)):
    return cq.Solid.makeBox(x, y, z, cq.Vector(*origin))


def cylinder(radius, length, origin=(0, 0, 0), axis=(0, 0, 1)):
    return cq.Solid.makeCylinder(radius, length, cq.Vector(*origin), cq.Vector(*axis))


def fuse(shapes):
    shapes = list(shapes)
    return shapes[0].fuse(*shapes[1:]).clean() if len(shapes) > 1 else shapes[0]


def cut(shape, tools):
    tools = list(tools)
    return shape.cut(*tools).clean() if tools else shape


def bounds(shape):
    b = shape.BoundingBox()
    return [b.xmin, b.ymin, b.zmin, b.xmax, b.ymax, b.zmax]


def print_position(shape, rotation=None):
    if rotation:
        shape = shape.rotate((0, 0, 0), rotation[0], rotation[1])
    b = bounds(shape)
    return shape.translate((-b[0], -b[1], -b[2]))


def hole_z(x, y, diameter, z=-1, height=140):
    return cylinder(diameter / 2, height, (x, y, z))


def strap_slots(x, y, spacing, width=14, z=-1, height=8):
    return [box(2.5, width, height, (x + dx - 1.25, y - width / 2, z))
            for dx in (-spacing / 2, spacing / 2)]


COLORS = {"shell": (0.23, 0.12, 0.065), "mechanism": (0.22, 0.25, 0.28),
          "tray": (0.18, 0.10, 0.23), "cover": (0.12, 0.055, 0.17),
          "pcb": (0.08, 0.38, 0.26), "metal": (0.55, 0.48, 0.32),
          "black": (0.055, 0.06, 0.07), "wand": (0.42, 0.22, 0.10),
          "light": (0.93, 0.69, 0.22), "white": (0.9, 0.9, 0.9)}


@dataclass
class Part:
    name: str
    shape: cq.Shape
    quantity: int
    reason: str
    rotation: tuple | None = None


@dataclass
class Item:
    name: str
    shape: cq.Shape
    group: str
    color: str
    source: str = "design"
    allowance: str = ""


def assembly(items, name="F1"):
    result = cq.Assembly(name=name)
    for item in items:
        result.add(item.shape, name=item.name, color=cq.Color(*COLORS[item.color]))
    return result
