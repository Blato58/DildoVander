"""Reserved cross-case cable corridors, not a fabricated detailed wiring harness."""
from common import Item, cylinder


def build(p):
    w, d = p["case"]["inside_width"], p["case"]["inside_depth"]
    left = w / 2 - 3 * p["mechanism"]["pitch"] - 1
    right = w / 2 + 3 * p["mechanism"]["pitch"] + 1
    front_y = d / 2 - p["mechanism"]["tray_length"] / 2 - 5
    return [Item(name, cylinder(2.5, right - left, (left, y, 9), (1, 0, 0)), "space", "clearance",
                 evidence="design cable routing allowance, not measured harness",
                 metadata={"role": "cable_route", "purpose": purpose, "diameter_mm": 5,
                           "centreline_length_mm": right - left, "end_connections": "route physically into side bays; connectors unmeasured"})
            for name, y, purpose in [("front_cross_cable_route", front_y, "Front 5mm bundle corridor in rail clips below rest feet"),
                                     ("rear_cross_cable_route", d - 5, "Rear 5mm bundle corridor behind rear module bases")]]
