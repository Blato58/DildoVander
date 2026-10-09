"""Purchased component envelopes; no unmeasured PCB mounting-hole pattern.

Dimensions originate in ../../rozmery-nakoupenych-soucastek.md. Models express
occupied material conservatively, not manufacturer CAD or electrical routing.
"""
from __future__ import annotations

import cadquery as cq

from common import box, cylinder


CATALOG = {
    "esp32": (57, 28, 13, "catalog"),
    "pca9685": (70, 25, 15, "owner"),
    "mcp23017": (43, 18, 5, "catalog"),
    "pixel_shifter": (25.5, 15, 10.2, "catalog"),
    "microphone": (16.51, 12.7, 5, "catalog footprint; provisional height"),
    "amplifier": (18, 18, 8, "catalog footprint; provisional height"),
    "speaker": (100, 45, 21, "catalog"),
    "buck": (46, 31, 19, "catalog"),
    "pd": (28, 14, 11, "catalog"),
    "powerbank": (139, 80, 26.5, "owner"),
    "ptt": (14, 14, 35, "owner; thread 12 catalog; cutout provisional"),
    "rocker": (24, 24, 30, "catalog; panel cutout 19"),
    "breaker": (45, 28, 13, "catalog; actuator orientation provisional"),
    "perfboard": (50, 70, 1.6, "catalog footprint; provisional thickness"),
    "degson": (35, 22, 16, "provisional adjustable envelope; not delivered"),
    "capacitor": (10, 10, 17, "catalog cylindrical package"),
    "camera": (24, 24, 12, "provisional module/FPC; ordered variant unknown"),
}


def envelope(key, dimensions=None):
    """Bounding envelope in the installed orientation, chosen by the caller."""
    size = dimensions or CATALOG[key][:3]
    return box(*size)


def panel_switch(key):
    """Circular threaded/barrel body and face, using the published panel size.

The 24/14 mm faces are conservative square face envelopes. No terminal or
nut shape is asserted; terminal access is separately reserved as space.
"""
    if key == "rocker":
        return cylinder(9.5, 27, (12, 12, 0)).fuse(box(24, 24, 3, (0, 0, 27)))
    if key == "ptt":
        return cylinder(6, 32, (7, 7, 0)).fuse(box(14, 14, 3, (0, 0, 32)))
    raise ValueError(key)


def speaker():
    """21 X, 100 Y, 45 Z, acoustic face at positive X (orientation assumption)."""
    body = box(21, 100, 45)
    # A shallow recess identifies the face; it is not a claimed diaphragm size.
    face = cq.Workplane("YZ", origin=(20.5, 50, 22.5)).ellipse(42, 17).extrude(1).val()
    return body.cut(face).clean()


def camera():
    """Unknown camera: provisional square PCB and central lens, 12 mm total."""
    return box(24, 24, 3).fuse(cylinder(6, 9, (12, 12, 3))).clean()


def capacitor():
    return cylinder(5, 17, (5, 5, 0))


def resistor():
    return cylinder(1.2, 6.3, (0, 0, 1.2), (1, 0, 0))


def evidence(key):
    return {"source": "rozmery-nakoupenych-soucastek.md", "nominal_mm": list(CATALOG[key][:3]),
            "dimension_status": CATALOG[key][3], "mounting_hole_centres": "not asserted"}
