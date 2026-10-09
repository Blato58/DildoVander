"""Six hinged wand trays, adapted from the existing tested mechanical module.

Canonical parts use the original centred module frame. Instances use the case
frame from common.py. Component envelopes are catalogue sizes, not factory CAD.
"""
from __future__ import annotations

from dataclasses import replace
import json
import math
from pathlib import Path
import re
import sys

import cadquery as cq

from common import Item, bounds

MECHANIKA_ROOT = Path(__file__).resolve().parents[2]
if str(MECHANIKA_ROOT) not in sys.path:
    sys.path.insert(0, str(MECHANIKA_ROOT))
import model as legacy


def mechanics_parameters(p):
    """Keep legacy kinematics while using confirmed case/component envelopes."""
    result = json.loads((MECHANIKA_ROOT / "parametry.json").read_text(encoding="utf-8-sig"))
    m, case = p["mechanism"], p["case"]
    result.update(
        compartment_count=m["count"], compartment_pitch_mm=m["pitch"],
        tray_length_mm=m["tray_length"], tray_floor_low_mm=m["tray_floor"],
        hinge_axis_z_mm=m["tray_floor"], wand_max_length_mm=m["wand_length"],
        wand_shaft_diameter_mm=m["wand_shaft_diameter"],
        wand_head_diameter_mm=m["wand_head_diameter"],
        servo_body_xyz_mm=[27, 22, 11.5], vibration_body_xyz_mm=[32, 24, 8],
        case_inside_mm=[case["inside_width"], case["inside_depth"], case["inside_height"]],
        case_dimensions_confirmed_internal=True,
        printer_bed_mm=p["manufacturing"]["printer_bed_mm"],
    )
    legacy.validate_config(result)
    if m["base_z"] < 8:
        raise ValueError("The module feet extend 8 mm below its base plane")
    return result


def module_origin(p, index):
    m, case = p["mechanism"], p["case"]
    return (case["inside_width"] / 2 + (index - (m["count"] - 1) / 2) * m["pitch"],
            case["inside_depth"] / 2, m["base_z"])


def _slug(name):
    return re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")


def _local_build(p):
    parameters = mechanics_parameters(p)
    parts, printed, purchased, metadata = legacy.build(parameters)
    # Horizontal through-slots route elastic under the supported shaft. The
    # elastic itself is intentionally excluded from rigid collision checks.
    slot = legacy.block(parameters["tray_width_mm"], 3, 2,
                        (0, 0, parameters["tray_floor_low_mm"] + 10))
    led_channel = legacy.block(parameters["led_strip_width_mm"] + 1, 18, 6.5,
                               (5, 0, parameters["tray_floor_low_mm"] + 3.9))
    for item in printed:
        if item["name"] == "wand_saddle":
            center = item["shape"].BoundingBox().center.y
            item["shape"] = item["shape"].cut(slot.translate((0, center, 0)))
            item["shape"] = item["shape"].cut(led_channel.translate((0, center, 0))).clean()
    parts["wand_saddle"] = next(i["shape"] for i in printed if i["name"] == "wand_saddle")
    floor = parameters["tray_floor_low_mm"]
    for index in range(6):
        y = -100 + (index + 0.5) * 200 / 6
        purchased.append(dict(name="WS2812B LED chip envelope", motion="tray", color="#ffffff",
                              shape=legacy.block(5, 5, 1.6, (5, y, floor + 7.3))))
    return parameters, parts, printed, purchased, metadata


def build(p):
    parameters, parts, printed, purchased, metadata = _local_build(p)
    items = []
    for module in range(p["mechanism"]["count"]):
        origin = module_origin(p, module)
        counts = {}
        for item, is_printed in [(i, True) for i in printed] + [(i, False) for i in purchased]:
            canonical = item["name"]
            slug = _slug(canonical)
            counts[slug] = counts.get(slug, 0) + 1
            if is_printed:
                kind, evidence = "print", "design"
            elif canonical.startswith("wand "):
                kind, evidence = "wand", "provisional wand envelope"
            elif "servo envelope" in canonical or "U059" in canonical:
                kind, evidence = "drive", "rozmery-nakoupenych-soucastek.md"
            elif "LED" in canonical:
                kind, evidence = "electronics", "catalogue width/length; thickness and chips provisional"
            else:
                kind, evidence = "metal", "required consumable; purchase not confirmed"
            items.append(Item(
                f"module_{module + 1:02d}_{slug}_{counts[slug]:02d}",
                item["shape"].translate(origin), kind, "mechanism",
                part=canonical if is_printed else None, evidence=evidence,
                metadata=dict(motion=item.get("motion", "fixed"), module=module + 1,
                              canonical_name=canonical, origin_mm=list(origin)),
            ))
    metadata.update(
        parameters=parameters, module_origins_mm=[list(module_origin(p, i)) for i in range(6)],
        module_count=6, led_chip_count=36,
        foot_centres_local_y_mm=[-110, 110], foot_bottom_world_z_mm=p["mechanism"]["base_z"] - 8,
        base_splice_bottom_world_z_mm=p["mechanism"]["base_z"] - 3,
        required_consumables=["M3 fasteners and nuts for bases, splices, cradle, cage and linkage",
                              "M4 hinge bolts/nuts with friction retention", "servo and U059 retaining straps",
                              "two releasable elastic wand straps per module"],
        provisional=["servo spline/horn adapter central and auxiliary holes are design placeholders",
                     "servo output offset/ears are not factory geometry; cradle uses body straps",
                     "LED thickness/chip envelope; wand shaft/head envelope; flexible cable/strap routes"],
        physical_acceptance=["servo force and individual travel calibration", "wand loaded motion and removal",
                             "hinge friction retention on power loss", "cable/strap clearance and vibration"],
    )
    return parts, items, metadata


def pose(p, items, servo_angle, module=None):
    """Pose world-frame home instances; module is optional and one-based."""
    parameters = mechanics_parameters(p)
    if not parameters["angle_low_deg"] <= servo_angle <= parameters["angle_high_deg"]:
        raise ValueError("Servo angle is outside the calibrated design travel")
    if module is not None and module not in range(1, p["mechanism"]["count"] + 1):
        raise ValueError("Module must be 1..6")
    posed = []
    for item in items:
        if item.group != "mechanism" or (module is not None and item.metadata["module"] != module):
            posed.append(item)
            continue
        origin = item.metadata["origin_mm"]
        local = dict(shape=item.shape.translate(tuple(-v for v in origin)), motion=item.metadata["motion"])
        shape = legacy.pose_items([local], parameters, servo_angle)[0]["shape"].translate(origin)
        posed.append(replace(item, shape=shape))
    return posed


def print_shape(name, shape, p):
    """Put thin X-axis pivot parts flat; retain tray-floor-up orientations."""
    parameters = mechanics_parameters(p)
    if name == "servo_link":
        home = legacy.linkage_state(parameters, parameters["angle_low_deg"])
        shape = shape.rotate((0, 0, 0), (1, 0, 0), -home["link_angle_deg"])
    elif name == "crank_horn_adapter":
        shape = shape.rotate((0, 0, 0), (1, 0, 0), -parameters["angle_low_deg"])
    if name in {"servo_link", "crank_horn_adapter", "link_spacer_3mm", "hinge_spacer"}:
        shape = shape.rotate((0, 0, 0), (0, 1, 0), -90)
    return legacy.normalize_for_print(shape)


def _combined_bounds(items):
    values = [bounds(item.shape) for item in items]
    return [min(b[i] for b in values) for i in range(3)] + [max(b[i] for b in values) for i in range(3, 6)]


def verification(p):
    parameters, parts, printed, purchased, _ = _local_build(p)
    _, items, _ = build(p)
    states = [legacy.linkage_state(parameters, parameters["angle_low_deg"] +
                                  (parameters["angle_high_deg"] - parameters["angle_low_deg"]) * i / 100)
              for i in range(101)]
    if any(s["closure_error_mm"] > 1e-8 or s["toggle_margin_mm"] <= 5 for s in states):
        raise ValueError("Linkage closure/toggle margin failed")
    if any(b["tilt_deg"] < a["tilt_deg"] - 1e-8 for a, b in zip(states, states[1:])):
        raise ValueError("Presentation is not monotonic")
    travel = legacy.verify_travel(parameters, printed + purchased)
    home_bounds = _combined_bounds(items)
    raised_bounds = _combined_bounds(pose(p, items, parameters["angle_high_deg"]))
    case = p["case"]
    limit = [case["inside_width"], case["inside_depth"], case["inside_height"]]
    if any(home_bounds[i] < -1e-6 or home_bounds[i + 3] > limit[i] + 1e-6 for i in range(3)):
        raise ValueError(f"Stowed mechanism exceeds case: {home_bounds}")
    if any(raised_bounds[i] < -1e-6 or raised_bounds[i + 3] > limit[i] + 1e-6 for i in range(2)):
        raise ValueError(f"Raised mechanism exceeds case footprint: {raised_bounds}")
    printed_dimensions = {}
    bed = p["manufacturing"]["printer_bed_mm"]
    for name, shape in parts.items():
        if not shape.isValid() or len(shape.Solids()) != 1 or shape.Volume() <= 0:
            raise ValueError(f"Invalid or disconnected part: {name}")
        b = print_shape(name, shape, p).BoundingBox()
        dims = [b.xlen, b.ylen, b.zlen]
        if not (dims[0] <= bed[0] and dims[1] <= bed[1] or dims[1] <= bed[0] and dims[0] <= bed[1]) or dims[2] > bed[2]:
            raise ValueError(f"Part exceeds printer envelope: {name} {dims}")
        printed_dimensions[name] = dims
    # Every motion is around X, so the module's X bounds are invariant. Disjoint
    # X intervals prove rigid neighbour clearance for arbitrary mixed poses.
    first = [i for i in items if i.metadata["module"] == 1]
    b = _combined_bounds(first)
    gap = p["mechanism"]["pitch"] - (b[3] - b[0])
    if gap < 0:
        raise ValueError("Neighbouring module sweeps overlap")
    return dict(status="PASS", closure_samples=101, solid_collision_samples=len(travel),
                stowed_bounds_mm=home_bounds, raised_bounds_mm=raised_bounds,
                stowed_lid_clearance_mm=case["inside_height"] - home_bounds[5],
                neighbour_sweep_gap_mm=gap, travel_samples=travel,
                printed_dimensions_mm=printed_dimensions,
                exclusions=["closed lid during raised motion", "lid/open-case and electronics integration",
                            "flexible cables and straps", "force/strength/friction and physical acceptance",
                            "continuous solid collision proof between the 21 sampled positions"])
