"""Printable SG90 double horn; nominal spline fit still needs a physical test.

Coordinates match the cam: shaft along +X, arm along Y, front face X=2.2.
Only the socket section is scaled for fit; the arm and M2 pitch stay unchanged.
"""
from functools import lru_cache

import cadquery as cq

from geometry import PARAMS, Part, box, cylinder, cut, fuse
from servo_reference import servo

REAR_X = -1.55
SOCKET_FLOOR_X = 1.4
FRONT_X = 2.2
ARM_REAR_X = .2
SHAFT_RADIUS = 2.475
FIT_ALLOWANCES = (0.0, .025, .05)
DEFAULT_ALLOWANCE = .025
PRINT_ROTATION = ((0, 1, 0), 90)


@lru_cache(maxsize=1)
def shaft_section():
    """Exact STEP outer wire, including its 20 grooves, centered on the X axis."""
    mechanism = PARAMS["mechanism"]
    section_x = mechanism["cam_x"] - 1
    slab = box(.1, 8, 8, (section_x, mechanism["cam_y"] - 4,
                           mechanism["cam_z"] - 4))
    faces = [face for face in servo().intersect(slab).Faces()
             if face.geomType() == "PLANE"]
    face = min(faces, key=lambda candidate: candidate.Center().x)
    return face.outerWire().translate((-section_x, -mechanism["cam_y"],
                                       -mechanism["cam_z"]))


@lru_cache(maxsize=1)
def printable_section():
    # Facet the imported curves finely for predictable, compact FDM meshes.
    # Native STEP splines otherwise create hundreds of thousands of triangles.
    # Tests bound this chord approximation to 0.003 mm against the source wire.
    remaining = [edge.sample(17 if edge.geomType() == "BSPLINE" else 5)[0]
                 for edge in shaft_section().Edges()]
    points = remaining.pop(0)
    # Edge sampling follows curve parameters, not the wire's edge orientation.
    while remaining:
        for index, segment in enumerate(remaining):
            if (points[-1] - segment[0]).Length < 1e-6:
                break
            if (points[-1] - segment[-1]).Length < 1e-6:
                segment = segment[::-1]
                break
        else:
            raise ValueError("Disconnected STEP shaft section")
        points.extend(segment[1:])
        remaining.pop(index)
    if (points[0] - points[-1]).Length > 1e-6:
        raise ValueError("Open STEP shaft section")
    return cq.Wire.makePolygon(points[:-1], close=True)


def socket(allowance):
    if allowance not in FIT_ALLOWANCES:
        raise ValueError("Choose a tested coupon allowance: 0, .025 or .05 mm")
    wire = printable_section().scale(1 + allowance / SHAFT_RADIUS)
    wire = wire.translate((REAR_X - .05, 0, 0))
    return cq.Solid.extrudeLinear(wire, [],
                                  cq.Vector(SOCKET_FLOOR_X - REAR_X + .05, 0, 0))


def hub(allowance):
    solid = cylinder(4.5, FRONT_X - REAR_X, (REAR_X, 0, 0), (1, 0, 0))
    return cut(solid, [socket(allowance),
                      cylinder(1.1, 5, (-2, 0, 0), (1, 0, 0))])


@lru_cache(maxsize=3)
def horn(allowance=DEFAULT_ALLOWANCE):
    arm = fuse([box(2, 29, 6, (ARM_REAR_X, -14.5, -3)),
                *(cylinder(3, 2, (ARM_REAR_X, y, 0), (1, 0, 0))
                  for y in (-14.5, 14.5))])
    arm = cut(arm, [cylinder(1.2, 3, (0, y, 0), (1, 0, 0))
                    for y in (-9, 9)])
    return cut(fuse([arm, hub(allowance)]),
               [socket(allowance), cylinder(1.1, 5, (-2, 0, 0), (1, 0, 0))])


@lru_cache(maxsize=1)
def fit_coupon():
    # One connected coupon, same cap thickness/hub/socket and print orientation.
    # One, two and three edge notches identify increasing clearance, respectively.
    bridge = box(.8, 34, 12, (SOCKET_FLOOR_X, -17, -6))
    shapes = [bridge]
    tools = []
    for index, allowance in enumerate(FIT_ALLOWANCES):
        y = (index - 1) * 12
        shapes.append(hub(allowance).translate((0, y, 0)))
        tools.extend([socket(allowance).translate((0, y, 0)),
                      cylinder(1.1, 5, (-2, y, 0), (1, 0, 0))])
        tools.extend(box(1, .8, 1.2, (1.3, y - 1.5 + notch * 1.5, 5))
                     for notch in range(index + 1))
    return cut(fuse(shapes), tools)


def parts():
    return [Part("servo_horn", horn(), 6,
                 "PETG 100%; test spline coupon first; physical grip, retaining screw and load unverified",
                 PRINT_ROTATION),
            Part("servo_horn_fit_coupon", fit_coupon(), 1,
                 "One/two/three notches: socket maximum diameters 4.95/5.00/5.05 mm; hand fit only",
                 PRINT_ROTATION)]
