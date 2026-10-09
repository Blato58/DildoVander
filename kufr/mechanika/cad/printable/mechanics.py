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

from common import Item, bounds, box, cylinder

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
        hinge_axis_z_mm=m["tray_floor"] + 7, link_tray_z_offset_mm=-2.5,
        wand_max_length_mm=m["wand_length"],
        wand_shaft_diameter_mm=m["wand_shaft_diameter"],
        wand_head_diameter_mm=m["wand_head_diameter"],
        servo_body_xyz_mm=[27, 22, 11.5], vibration_body_xyz_mm=[32, 24, 8],
        case_inside_mm=[case["inside_width"], case["inside_depth"], case["inside_height"]],
        case_dimensions_confirmed_internal=True,
        printer_bed_mm=p["manufacturing"]["printer_bed_mm"],
    )
    if m["tray_length"] != 240 or m["count"] != 6:
        raise ValueError("This six-module layout currently requires 240 mm trays")
    if not 170 <= m["wand_length"] <= m["tray_length"] - 24:
        raise ValueError("Wand must be 170..216 mm for this saddle spacing and tray; redesign before export")
    if m["base_z"] < 8:
        raise ValueError("Base must clear the 5 mm batten and provide room for keyed feet")
    return result


def module_origin(p, index):
    m, case = p["mechanism"], p["case"]
    return (case["inside_width"] / 2 + (index - (m["count"] - 1) / 2) * m["pitch"],
            case["inside_depth"] / 2, m["base_z"])


def _slug(name):
    return re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")


def _local_build(p):
    parameters = mechanics_parameters(p)
    parts, printed, purchased = {}, [], []
    def add(name, shape, motion="fixed", bought=False):
        shape = shape.clean()
        (purchased if bought else printed).append(dict(name=name, shape=shape, motion=motion))
        if not bought:
            parts[name] = shape

    b = legacy.block
    floor = parameters["tray_floor_low_mm"]
    hy, hz = parameters["hinge_y_mm"], parameters["hinge_axis_z_mm"]
    home = legacy.linkage_state(parameters, parameters["angle_low_deg"])
    sy, sz, radius = parameters["servo_axis_y_mm"], parameters["servo_axis_height_mm"], parameters["crank_radius_mm"]

    # All structural features grow from a flat 4 mm base. Separate feet avoid a
    # 220 mm unsupported bridge underneath that base during FDM printing.
    chassis = b(46, 240, 4)
    chassis = chassis.fuse(b(40, 8, floor - 4, (0, -108, 4)))
    for x in (-11, 11):
        chassis = chassis.fuse(b(6, 16, hz - 4, (x, hy, 4)),
                               cylinder(8, 6, (x - 3, hy, hz), (1, 0, 0)))
    chassis = chassis.cut(cylinder(2.25, 40, (-20, hy, hz), (1, 0, 0)))
    # Servo support is solid down to the base; two 3 mm tie tunnels retain its
    # body without pretending that the unknown mounting ears are measured.
    sx, dy, dz = parameters["servo_body_xyz_mm"]
    centre = (7 - sx / 2, sy + dy / 2 - 8, sz - dz / 2)
    cradle_bottom = centre[2] - 3
    chassis = chassis.fuse(b(sx + 6, dy + 8, centre[2] - 4, (centre[0], centre[1], 4)))
    for y in (centre[1] - dy / 2 - 2.5, centre[1] + dy / 2 + 2.5):
        chassis = chassis.fuse(b(sx + 6, 3, dz + 2, (centre[0], y, centre[2])))
    chassis = chassis.fuse(b(3, dy + 8, dz + 2, (7 - sx - 1.5, centre[1], centre[2])))
    # Output face and nut sweep stay open at X >= 7.
    chassis = chassis.cut(box(40, dy + 8, 70, (7, centre[1] - (dy + 8) / 2, 4)))
    for y in (centre[1] - 7, centre[1] + 7):
        chassis = chassis.cut(b(sx + 8, 3, 1.8, (centre[0], y, cradle_bottom + .5)))
    for y in (-110, 110):
        # Two keys locate each separately printed foot. Glue the keys once;
        # the hook-loop case interface remains releasable.
        foot_height = p["mechanism"]["base_z"] - 2
        foot = b(46, 16, foot_height, (0, y, -foot_height))
        for x in (-16, 16):
            foot = foot.fuse(b(4, 4, 1, (x, y, 0)))
            chassis = chassis.cut(b(4.6, 4.6, 1.6, (x, y, -.1)))
        add("module_foot", foot)
        add("foot case hook_loop", b(42, 12, 2, (0, y, -p["mechanism"]["base_z"])), bought=True)
    add("module_chassis", chassis)
    add("servo envelope (unmeasured)", b(sx, dy, dz, centre), bought=True)

    tray = b(46, 240, 4, (0, 0, floor))
    # Rear neck fits between the fixed cheeks. Barrel is above the flat floor.
    for x in (-16, 16):
        tray = tray.cut(b(17, 32, 24, (x, hy + 4, floor - 1)))
    tray = tray.fuse(cylinder(6, 15.6, (-7.8, hy, hz), (1, 0, 0)))
    tray = tray.cut(cylinder(2.25, 18, (-9, hy, hz), (1, 0, 0)))
    ty, tz = home["attachment_y_mm"], home["attachment_z_mm"]
    tray = tray.cut(box(15, 24, 22, (16.7, ty - 12, floor - 1)))
    tray = tray.fuse(cylinder(4.5, 6.7, (10, ty, tz), (1, 0, 0)))
    tray = tray.cut(cylinder(1.65, 10, (7, ty, tz), (1, 0, 0)))
    tray = tray.cut(cylinder(3.5, 2.6, (7.4, ty, tz), (1, 0, 0)))
    # Open 90-degree V saddles accept tapered shafts with a soft removable liner.
    # The V position uses the conservative 16 mm shaft, not a photo measurement.
    vertex = floor + 29 - parameters["wand_shaft_diameter_mm"] / math.sqrt(2)
    wedge = cq.Workplane("XZ").polyline([(-30, vertex + 30), (0, vertex), (30, vertex + 30)]).close().extrude(18).val()
    for y in (-25, 75):
        saddle = b(22, 16, 24, (0, y, floor + 4)).cut(wedge.translate((0, y + 9, 0)))
        saddle = saddle.cut(b(24, 4, 2, (0, y, floor + 10)))
        tray = tray.fuse(saddle)
    # U059 sits on the deck under the shaft; no hanging cage or cage screws.
    for x in (-11.1, 17.1):
        tray = tray.fuse(b(3, 34, 4, (x, 25, floor + 4)))
    for y in (16, 34):
        tray = tray.cut(b(32, 3, 1.8, (3, y, floor + 1)))
    # Empty-tray transport wrap rides above the LED strip on two small pads.
    # Raised ends locate its 10 mm width; release/remove before powering servos.
    for x in (-9.5, 20):
        tray = tray.fuse(b(3, 12, 5, (x, -92, floor + 4)))
        for y in (-97.5, -86.5):
            tray = tray.fuse(b(3, 1, 1, (x, y, floor + 9)))
    add("wand_tray", tray, "tray")
    add("U059 envelope (unmeasured)", b(24, 32, 8, (3, 25, floor + 4)), "tray", True)
    add("LED strip envelope (unmeasured)", b(10, 200, 3.2, (-18, 0, floor + 4.1)), "tray", True)
    for index in range(6):
        y = -100 + (index + 0.5) * 200 / 6
        add("WS2812B LED chip envelope", b(5, 5, 1.6, (-18, y, floor + 7.3)), "tray", True)
    # Conservative envelopes retained for clearance tests, including ornaments.
    wand_length = parameters["wand_max_length_mm"]
    add("wand shaft envelope", cylinder(parameters["wand_shaft_diameter_mm"] / 2, wand_length - 50,
        (0, -wand_length / 2 + 50, floor + 29), (0, 1, 0)), "tray", True)
    add("wand decorative head envelope (unmeasured)", cylinder(parameters["wand_head_diameter_mm"] / 2, 50,
        (0, -wand_length / 2, floor + 29), (0, 1, 0)), "tray", True)

    crank = cylinder(7, 4, (10, sy, sz), (1, 0, 0)).fuse(b(4, radius, 10, (12, sy + radius / 2, sz - 5)),
        cylinder(5.5, 4, (10, sy + radius, sz), (1, 0, 0)), cylinder(4.5, 2.7, (14, sy + radius, sz), (1, 0, 0)))
    for y, diameter in ((sy, 3.5), (sy + 6, 2.2), (sy + 12, 2.2), (sy + radius, 3.3)):
        crank = crank.cut(cylinder(diameter / 2, 10, (9, y, sz), (1, 0, 0)))
    add("crank_horn_adapter", crank.rotate((0, sy, sz), (1, sy, sz), parameters["angle_low_deg"]), "crank")
    cy, cz, length = home["crank_y_mm"], home["crank_z_mm"], home["link_length_mm"]
    link = cylinder(5.5, 4, (17, 0, 0), (1, 0, 0)).fuse(cylinder(5.5, 4, (17, length, 0), (1, 0, 0)),
        b(4, length, 11, (19, length / 2, -5.5)))
    for y in (0, length):
        link = link.cut(cylinder(1.65, 6, (16, y, 0), (1, 0, 0)))
    add("servo_link", link.rotate((0, 0, 0), (1, 0, 0), home["link_angle_deg"]).translate((0, cy, cz)), "link")
    for y, z, motion in ((cy, cz, "crank"), (ty, tz, "tray")):
        add("link pivot M3 head envelope", cylinder(2.75, 3, (21, y, z), (1, 0, 0)), motion, True)
        nut = cylinder(3.25, 2.4, (7.6, y, z), (1, 0, 0)).cut(cylinder(1.65, 4, (7, y, z), (1, 0, 0)))
        add("link pivot M3 nut envelope", nut, motion, True)
        add("link pivot M3x14 shaft envelope", cylinder(1.5, 14, (7, y, z), (1, 0, 0)), motion, True)
    add("M4x35 hinge shaft envelope", cylinder(2, 35, (-15, hy, hz), (1, 0, 0)), bought=True)
    add("M4 hinge head envelope", cylinder(3.5, 4, (-19, hy, hz), (1, 0, 0)), bought=True)
    for x in (-15, 14):
        washer = cylinder(4.5, 1, (x, hy, hz), (1, 0, 0)).cut(cylinder(2.2, 3, (x - 1, hy, hz), (1, 0, 0)))
        add("M4 metal washer envelope", washer, bought=True)
    nut = cylinder(4.1, 3.2, (15, hy, hz), (1, 0, 0)).cut(cylinder(2.2, 5, (14, hy, hz), (1, 0, 0)))
    add("M4 ordinary nut envelope", nut, bought=True)
    gauge = b(72, 30, 5)
    for i, diameter in enumerate((3.1, 3.3, 4.1, 4.3, 4.5, 4.7)):
        gauge = gauge.cut(cylinder(diameter / 2, 7, (-27 + i * 10.8, 0, -1)))
    parts["fit_coupon"] = gauge.clean()
    high = legacy.linkage_state(parameters, parameters["angle_high_deg"])
    metadata = dict(home_state=home, high_state=high, lift_mm=(hy + 120) * math.sin(math.radians(high["tilt_deg"])))
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
            elif "hook_loop" in canonical:
                kind, evidence = "cable", "required adhesive hook-and-loop; compressed thickness assumption 2 mm"
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
        foot_centres_local_y_mm=[-110, 110], foot_bottom_world_z_mm=2,
        required_consumables=["two M3x14 link pivots and one M4x35 hinge axle per module",
                              "M4 ordinary nuts, two 1 mm washers, metal thread locking; adjust friction", "servo and U059 retaining straps",
                              "two releasable elastic wand straps per module", "10 mm transport strap around empty tray and chassis",
                              "42 x 12 mm adhesive hook-loop pads beneath two keyed glued feet"],
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


def transport(p, items):
    """Wands and powerbank removed, empty trays strapped to their own bases."""
    result = [i for i in items if i.kind != "wand" and i.name != "powerbank" and i.kind != "space"]
    top = p["mechanism"]["tray_floor"] + 9
    strap = box(48.6, 10, top + 1.6, (-24.3, -97, -.8))
    strap = strap.cut(box(47, 12, top, (-23.5, -98, 0))).clean()
    for index in range(p["mechanism"]["count"]):
        result.append(Item(f"transport_wrap_{index + 1}", strap.translate(module_origin(p, index)), "cable", "transport",
                           evidence="required 10 mm releasable strap; 0.8 mm thickness assumption",
                           metadata={"release_before_motion": True, "purpose": "Retain empty tray independently of servo; pads keep strap above LEDs."}))
    return result


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
    if name in {"module_chassis", "wand_tray"}:
        shape = shape.rotate((0, 0, 0), (0, 0, 1), 45)
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
