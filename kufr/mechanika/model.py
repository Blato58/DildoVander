"""Printable hinged wand trays and removable electronics; all dimensions in mm."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import shutil
import zipfile

import cadquery as cq


def block(x, y, z, at=(0, 0, 0)):
    return cq.Workplane("XY").box(x, y, z, centered=(True, True, False)).translate(at).val()


def cylinder(radius, length, at=(0, 0, 0), axis=(0, 0, 1)):
    return cq.Solid.makeCylinder(radius, length, cq.Vector(*at), cq.Vector(*axis))


def holes_z(shape, points, diameter, z=-1, depth=100):
    for x, y in points:
        shape = shape.cut(cylinder(diameter / 2, depth, (x, y, z)))
    return shape.clean()


def validate_config(p):
    positive = ["tray_length_mm", "tray_width_mm", "tray_head_width_mm",
                "base_width_mm", "crank_radius_mm", "compartment_pitch_mm",
                "wand_max_length_mm", "wand_shaft_diameter_mm", "wand_head_diameter_mm"]
    if any(p[key] <= 0 for key in positive):
        raise ValueError("Mechanical dimensions must be positive")
    if p["compartment_count"] != 6 or p["segment_count"] != 2:
        raise ValueError("This design uses six compartments and two tray sections")
    if not -75 <= p["angle_low_deg"] < p["angle_high_deg"] <= 75:
        raise ValueError("Servo travel must remain within -75..75 degrees")
    if p["tray_length_mm"] < p["wand_max_length_mm"] + 24:
        raise ValueError("Tray needs at least 12 mm clearance at each wand end")
    if p["tray_width_mm"] < p["wand_shaft_diameter_mm"] + 10:
        raise ValueError("Shaft is wider than its tray")
    if p["tray_head_width_mm"] < p["wand_head_diameter_mm"] + 6:
        raise ValueError("Decorative head is wider than its tray; measure or enlarge pitch")
    if p["tray_head_length_mm"] < p["wand_head_length_mm"] + 12:
        raise ValueError("Decorative head is longer than the widened tray section")
    if p["compartment_pitch_mm"] < max(p["base_width_mm"], p["tray_head_width_mm"]) + 2:
        raise ValueError("Neighbouring compartments overlap")
    if not 0 < p["hinge_y_mm"] <= p["tray_length_mm"] / 2 - 8:
        raise ValueError("Hinge must fit within the rear tray section")
    if p["hinge_axis_z_mm"] != p["tray_floor_low_mm"]:
        raise ValueError("This tray places its pivot on the floor plane")
    if p["led_strip_length_mm"] > p["tray_length_mm"] - 24 or p["led_strip_width_mm"] > 10:
        raise ValueError("LED strip exceeds its under-wand channel")
    for key in ["servo_body_xyz_mm", "vibration_body_xyz_mm", "powerbank_body_xyz_mm",
                "speaker_body_xyz_mm", "controller_body_xyz_mm", "camera_body_xyz_mm",
                "microphone_body_xyz_mm", "case_inside_mm", "printer_bed_mm"]:
        if len(p[key]) != 3 or min(p[key]) <= 0:
            raise ValueError(f"{key} must contain three positive dimensions")
    if p["speaker_count"] != 2:
        raise ValueError("This layout mounts the requested pair of speakers")
    if any(a > b for a, b in zip(p["vibration_body_xyz_mm"], [32, 24, 16])):
        raise ValueError("U059 envelope exceeds its strap cage")


def linkage_state(p, angle_deg):
    """Solve the planar crank/link/hinged-tray closure, keeping the rest branch."""
    hy, hz = p["hinge_y_mm"], p["hinge_axis_z_mm"]
    sy, sz, radius = p["servo_axis_y_mm"], p["servo_axis_height_mm"], p["crank_radius_mm"]
    rest_vector = (p["link_tray_y_mm"] - hy, p["link_tray_z_offset_mm"])
    rest_angle = math.atan2(rest_vector[1], rest_vector[0])
    a0, angle = math.radians(p["angle_low_deg"]), math.radians(angle_deg)
    crank_rest = (sy + radius * math.cos(a0), sz + radius * math.sin(a0))
    rest_attachment = (hy + rest_vector[0], hz + rest_vector[1])
    length = math.dist(crank_rest, rest_attachment)
    cy, cz = sy + radius * math.cos(angle), sz + radius * math.sin(angle)
    dy, dz = cy - hy, cz - hz
    distance, tray_radius = math.hypot(dy, dz), math.hypot(*rest_vector)
    if distance <= 0 or length < 12:
        raise ValueError("Degenerate linkage")
    along = (tray_radius ** 2 - length ** 2 + distance ** 2) / (2 * distance)
    height_squared = tray_radius ** 2 - along ** 2
    if height_squared <= 1:
        raise ValueError("Linkage cannot close or approaches a toggle")
    height = math.sqrt(height_squared)
    candidates = []
    for sign in [-1, 1]:
        ty = hy + along * dy / distance - sign * height * dz / distance
        tz = hz + along * dz / distance + sign * height * dy / distance
        delta = math.atan2(tz - hz, ty - hy) - rest_angle
        delta = (delta + math.pi) % (2 * math.pi) - math.pi
        candidates.append((abs(delta), delta, ty, tz))
    _, delta, ty, tz = min(candidates)
    tilt = -math.degrees(delta)
    if not -0.01 <= tilt <= 25:
        raise ValueError(f"Tray tilt {tilt:.2f} outside supported presentation range")
    return dict(tilt_deg=tilt, crank_y_mm=cy, crank_z_mm=cz,
                attachment_y_mm=ty, attachment_z_mm=tz, link_length_mm=length,
                link_angle_deg=math.degrees(math.atan2(tz - cz, ty - cy)),
                closure_error_mm=abs(math.dist((cy, cz), (ty, tz)) - length),
                toggle_margin_mm=height)


def pose_items(items, p, angle_deg):
    state, home = linkage_state(p, angle_deg), linkage_state(p, p["angle_low_deg"])
    hinge = (0, p["hinge_y_mm"], p["hinge_axis_z_mm"])
    servo = (0, p["servo_axis_y_mm"], p["servo_axis_height_mm"])
    crank = (0, home["crank_y_mm"], home["crank_z_mm"])
    posed = []
    for item in items:
        shape, motion = item["shape"], item.get("motion", "fixed")
        if motion == "tray":
            shape = shape.rotate(hinge, (1, hinge[1], hinge[2]), -state["tilt_deg"])
        elif motion == "crank":
            shape = shape.rotate(servo, (1, servo[1], servo[2]), angle_deg - p["angle_low_deg"])
        elif motion == "link":
            shape = shape.rotate(crank, (1, crank[1], crank[2]),
                                 state["link_angle_deg"] - home["link_angle_deg"])
            shape = shape.translate((0, state["crank_y_mm"] - home["crank_y_mm"],
                                     state["crank_z_mm"] - home["crank_z_mm"]))
        posed.append(dict(item, shape=shape))
    return posed


def build(p):
    validate_config(p)
    parts, items, purchased = {}, [], []
    length, width, head_width = p["tray_length_mm"], p["tray_width_mm"], p["tray_head_width_mm"]
    base_width, floor = p["base_width_mm"], p["tray_floor_low_mm"]
    hinge_y, hinge_z = p["hinge_y_mm"], p["hinge_axis_z_mm"]
    home, high = linkage_state(p, p["angle_low_deg"]), linkage_state(p, p["angle_high_deg"])

    def add(name, shape, color, motion="fixed", bought=False):
        if not bought:
            parts.setdefault(name, shape)
        (purchased if bought else items).append(dict(name=name, shape=shape, color=color, motion=motion))

    for index, center in enumerate([-length / 4, length / 4]):
        base = block(base_width, length / 2, 4, (0, center, 0))
        base = holes_z(base, [(x, y) for x in [-16, 16]
                              for y in [center - length / 4 + 10, center + length / 4 - 10]], 3.3, depth=6)
        if index == 0:
            sx, sy, _ = p["servo_body_xyz_mm"]
            servo_x = 7 - sx / 2
            servo_y = p["servo_axis_y_mm"] + sy / 2 - 8
            servo_mount_offset = sy / 2 + 9
            base = holes_z(base, [(servo_x, servo_y - servo_mount_offset), (servo_x, servo_y + servo_mount_offset),
                                  (-14, -length / 2 + 26), (14, -length / 2 + 26)], 3.3, depth=6)
        else:
            base = holes_z(base, [(-14, hinge_y - 12), (14, hinge_y - 12)], 3.3, depth=6)
        add(f"base_{index + 1}", base, "#566f7c")
        tray = block(width, length / 2, 4, (0, center, floor))
        tray = tray.fuse(*(block(2.4, length / 2, 10, (x, center, floor + 4))
                           for x in [-width / 2 + 1.2, width / 2 - 1.2]))
        if index == 0:
            head_length = p["tray_head_length_mm"]
            head_center = -length / 2 + head_length / 2
            tray = tray.cut(block(width + 2, head_length, 16, (0, head_center, floor - 1)))
            tray = tray.fuse(block(head_width, head_length, 4, (0, head_center, floor)),
                             *(block(2.4, head_length, 10, (x, head_center, floor + 4))
                               for x in [-head_width / 2 + 1.2, head_width / 2 - 1.2]),
                             block(head_width, 2.4, 10, (0, -length / 2 + 1.2, floor + 4)))
            # One short connecting link attaches under the broad head section.
            ly, lz = p["link_tray_y_mm"], floor + p["link_tray_z_offset_mm"]
            lug = cylinder(6, 4, (10, ly, lz), (1, 0, 0)).fuse(block(4, 10, floor + 4 - lz, (12, ly, lz)))
            lug = lug.cut(cylinder(1.65, 6, (9, ly, lz), (1, 0, 0)))
            tray = tray.fuse(lug)
        else:
            # Narrow rear neck swings between two fixed hinge cheeks.
            for x in [-15.5, 15.5]:
                tray = tray.cut(block(17, 32, 30, (x, hinge_y + 4, floor - 8)))
            barrel = cylinder(6, 14, (-7, hinge_y, hinge_z), (1, 0, 0))
            tray = tray.fuse(barrel).cut(cylinder(2.25, 16, (-8, hinge_y, hinge_z), (1, 0, 0)))
            for y in [7, 43]:
                tray = tray.fuse(block(40, 8, 4, (0, y, floor)))
                tray = holes_z(tray, [(-16, y), (16, y)], 3.3, z=floor - 1, depth=6)
        tray = holes_z(tray, [(x, y) for x in [-8, 8] for y in [-10, 10]
                              if center - length / 4 < y < center + length / 4], 3.3, z=floor - 1, depth=6)
        for x in [-8, 8]:
            y = -10 if index == 0 else 10
            tray = tray.cut(cq.Solid.makeCone(1.65, 3, 1.35, cq.Vector(x, y, floor + 2.65), cq.Vector(0, 0, 1)))
        add(f"tray_{index + 1}", tray.clean(), "#d6ad6d", "tray")

    for y in [-length / 2 + 10, length / 2 - 10]:
        pad = holes_z(block(base_width, 16, 8, (0, y, -8)), [(-16, y), (16, y)], 3.3, z=-9, depth=14)
        for x in [-16, 16]:
            pad = pad.cut(cylinder(3, 3, (x, y, -8)))
        add("base_floor_pad", pad.clean(), "#566f7c")

    add("base_splice", holes_z(block(base_width, 32, 3, (0, 0, -3)),
                               [(x, y) for x in [-16, 16] for y in [-10, 10]], 3.3, z=-4, depth=6), "#566f7c")
    add("tray_splice", holes_z(block(22, 32, 3, (0, 0, floor - 3)),
                               [(x, y) for x in [-8, 8] for y in [-10, 10]], 3.3, z=floor - 4, depth=6), "#ba9259", "tray")
    support = block(36, 28, 4, (0, hinge_y - 2, 4))
    for x in [-11, 11]:
        support = support.fuse(block(6, 16, hinge_z - 8, (x, hinge_y, 8)),
                               cylinder(8, 6, (x - 3, hinge_y, hinge_z), (1, 0, 0)))
    support = support.cut(cylinder(2.25, 38, (-19, hinge_y, hinge_z), (1, 0, 0)))
    support = holes_z(support, [(-14, hinge_y - 12), (14, hinge_y - 12)], 3.3, z=3, depth=6)
    add("hinge_support", support.clean(), "#7f9b9e")
    washer = cylinder(5.8, 1, (7, hinge_y, hinge_z), (1, 0, 0)).cut(cylinder(2.25, 3, (6, hinge_y, hinge_z), (1, 0, 0)))
    add("hinge_spacer", washer, "#bdd0d0")
    items.append(dict(items[-1], shape=washer.translate((-15, 0, 0))))
    add("M4 hinge bolt envelope", cylinder(2, 36, (-18, hinge_y, hinge_z), (1, 0, 0)), "#aab6bf", bought=True)
    rest_y = -length / 2 + 12
    rest = block(40, 36, 4, (0, rest_y, 4)).fuse(block(40, 8, floor - 8, (0, rest_y, 8)))
    rest = holes_z(rest, [(-14, rest_y + 14), (14, rest_y + 14)], 3.3, z=3, depth=6)
    add("lower_rest", rest, "#566f7c")
    for name, ys in [("hinge foot M3 head envelope", [hinge_y - 12]), ("rest foot M3 head envelope", [rest_y + 14])]:
        for y in ys:
            for x in [-14, 14]:
                add(name, cylinder(2.75, 3, (x, y, 8)), "#aab6bf", bought=True)

    sy, sz, radius = p["servo_axis_y_mm"], p["servo_axis_height_mm"], p["crank_radius_mm"]
    crank = cylinder(7, 4, (10, sy, sz), (1, 0, 0)).fuse(
        block(4, radius, 10, (12, sy + radius / 2, sz - 5)),
        cylinder(5.5, 4, (10, sy + radius, sz), (1, 0, 0)))
    for y, diameter in [(sy, 3.5), (sy + 6, 2.2), (sy + 12, 2.2), (sy + radius, 3.3)]:
        crank = crank.cut(cylinder(diameter / 2, 6, (9, y, sz), (1, 0, 0)))
    crank = crank.rotate((0, sy, sz), (1, sy, sz), p["angle_low_deg"])
    add("crank_horn_adapter", crank.clean(), "#d87662", "crank")
    cy, cz, link_length = home["crank_y_mm"], home["crank_z_mm"], home["link_length_mm"]
    link = cylinder(5.5, 4, (17, 0, 0), (1, 0, 0)).fuse(
        cylinder(5.5, 4, (17, link_length, 0), (1, 0, 0)), block(4, link_length, 11, (19, link_length / 2, -5.5)))
    for y in [0, link_length]:
        link = link.cut(cylinder(1.65, 6, (16, y, 0), (1, 0, 0)))
    link = link.rotate((0, 0, 0), (1, 0, 0), home["link_angle_deg"]).translate((0, cy, cz))
    add("servo_link", link.clean(), "#be90ae", "link")
    for name, thickness, x, y, z, motion in [
        ("link_spacer_3mm", 3, 14, cy, cz, "crank"),
        ("link_spacer_3mm", 3, 14, home["attachment_y_mm"], home["attachment_z_mm"], "tray")
    ]:
        spacer = cylinder(4.5, thickness, (x, y, z), (1, 0, 0)).cut(cylinder(1.65, thickness + 2, (x - 1, y, z), (1, 0, 0)))
        add(name, spacer, "#c4d1d1", motion)
        add("link pivot M3 head envelope", cylinder(2.75, 3, (21, y, z), (1, 0, 0)), "#aab6bf", motion, True)
        nut = cylinder(3.25, 2.4, (7.6, y, z), (1, 0, 0)).cut(cylinder(1.65, 4.4, (6.6, y, z), (1, 0, 0)))
        add("link pivot ordinary M3 nut envelope", nut, "#aab6bf", motion, True)
        add("link pivot M3x14 shaft envelope", cylinder(1.5, 14, (7, y, z), (1, 0, 0)), "#aab6bf", motion, True)
    sx, body_y, body_z = p["servo_body_xyz_mm"]
    body_center = (7 - sx / 2, sy + body_y / 2 - 8, sz - body_z / 2)
    cradle_bottom = sz - body_z / 2 - 3
    cradle = block(sx + 6, body_y + 8, 3, (body_center[0], body_center[1], cradle_bottom))
    for y in [body_center[1] - body_y / 2 - 2.5, body_center[1] + body_y / 2 + 2.5]:
        cradle = cradle.fuse(block(sx + 6, 3, body_z + 2, (body_center[0], y, cradle_bottom + 3)))
    cradle = cradle.fuse(block(3, body_y + 8, body_z + 2, (7 - sx - 1.5, body_center[1], cradle_bottom + 3)))
    for y in [body_center[1] - body_y / 2 - 9, body_center[1] + body_y / 2 + 9]:
        cradle = cradle.fuse(block(12, 14, 3, (body_center[0], y, cradle_bottom)))
        cradle = cradle.fuse(block(12, 6, cradle_bottom - 4, (body_center[0], y, 4)))
        cradle = holes_z(cradle, [(body_center[0], y)], 3.3, z=3, depth=cradle_bottom + 2)
    for y in [body_center[1] - 7, body_center[1] + 7]:
        cradle = cradle.cut(block(sx + 8, 3, 1.5, (body_center[0], y, cradle_bottom + 0.6)))
    # Leave the entire linkage-nut sweep beyond the open output face.
    cradle = cradle.cut(block(40, body_y + 30, 80, (27, body_center[1], 0)))
    add("servo_strap_cradle", cradle.clean(), "#77a9a0")
    add("servo envelope (unmeasured)", block(sx, body_y, body_z, body_center), "#407fb4", bought=True)
    # Reserve ordinary M3 head space on the external ears, outside the servo body.
    for y in [body_center[1] - body_y / 2 - 9, body_center[1] + body_y / 2 + 9]:
        add("servo mount M3 head envelope", cylinder(2.75, 3, (body_center[0], y, cradle_bottom + 3)), "#aab6bf", bought=True)

    for x in [-8, 8]:
        for y in [-10, 10]:
            head = cq.Solid.makeCone(1.5, 3, 1.5, cq.Vector(x, y, floor + 2.5), cq.Vector(0, 0, 1))
            add("tray splice countersunk M3 head envelope", head, "#aab6bf", "tray", True)

    cage = block(40, 40, 3, (0, 25, floor - 22))
    for x in [-16, 16]:
        for y in [7, 43]:
            cage = cage.fuse(block(5, 5, 19, (x, y, floor - 19)))
    cage = holes_z(cage, [(x, y) for x in [-16, 16] for y in [7, 43]], 3.3, z=floor - 23, depth=25)
    for y in [14, 36]:
        cage = cage.cut(block(34, 3, 2, (0, y, floor - 21.5)))
    add("vibration_cage", cage.clean(), "#94b669", "tray")
    vx, vy, vz = p["vibration_body_xyz_mm"]
    add("U059 envelope (unmeasured)", block(vx, vy, vz, (0, 25, floor - 19)), "#334451", "tray", True)
    saddle = block(width - 6, 16, 20, (0, 0, floor + 4))
    shaft_z = floor + 29
    saddle = saddle.cut(cylinder(p["wand_shaft_diameter_mm"] / 2 + 0.5, 18, (0, -9, shaft_z), (0, 1, 0)))
    saddle = saddle.cut(block(p["led_strip_width_mm"] + 1, 18, 4.5, (5, 0, floor + 3.9)))
    for y in [-25, 75]:
        add("wand_saddle", saddle.translate((0, y, 0)).clean(), "#bd975e", "tray")
    add("LED strip envelope (unmeasured)", block(p["led_strip_width_mm"], p["led_strip_length_mm"], 3.2, (5, 0, floor + 4.1)), "#b7d888", "tray", True)
    head_length = p["wand_head_length_mm"]
    add("wand shaft envelope", cylinder(p["wand_shaft_diameter_mm"] / 2, p["wand_max_length_mm"] - head_length,
                                       (0, -p["wand_max_length_mm"] / 2 + head_length, shaft_z), (0, 1, 0)), "#71442e", "tray", True)
    add("wand decorative head envelope (unmeasured)", cylinder(p["wand_head_diameter_mm"] / 2, head_length,
                                                             (0, -p["wand_max_length_mm"] / 2, shaft_z), (0, 1, 0)), "#6d8179", "tray", True)
    parts["cable_clip"] = holes_z(block(16, 12, 4).fuse(block(3, 12, 8, (-6.5, 0, 4)), block(3, 12, 8, (6.5, 0, 4))), [(0, 0)], 3.3, depth=6)
    gauge = block(72, 30, 5)
    for i, diameter in enumerate([3.1, 3.3, 4.1, 4.3, 4.5, 4.7]):
        gauge = gauge.cut(cylinder(diameter / 2, 7, (-27 + i * 10.8, 0, -1)))
    parts["fit_coupon"] = gauge.clean()
    head_gauge = block(210, 30, 5)
    for x, diameter in zip([-75, -25, 25, 75], [32, 36, 40, 44]):
        head_gauge = head_gauge.cut(cylinder(diameter / 2, 7, (x, 15, -1)))
    parts["head_fit_gauge"] = head_gauge.clean()
    angle = math.radians(high["tilt_deg"])
    lift = (hinge_y + length / 2) * math.sin(angle)
    mechanics = dict(lift_mm=lift, tilt_deg=high["tilt_deg"], link_length_mm=link_length,
                     hinge_y_mm=hinge_y, hinge_axis_z_mm=hinge_z, compartment_origin_y_mm=-75,
                     compartment_origin_z_mm=8, home_state=home, high_state=high,
                     power_loss_retention="Adjustable friction hinge requires physical load/creep verification")
    return parts, items, purchased, mechanics


def normalize_for_print(shape):
    bounds = shape.BoundingBox()
    return shape.translate((-bounds.xmin, -bounds.ymin, -bounds.zmin))


def verify_travel(p, items):
    collisions, samples = [], []
    for step in range(21):
        angle = p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * step / 20
        state = linkage_state(p, angle)
        samples.append(dict(servo_angle_deg=angle, **state))
        posed = pose_items(items, p, angle)
        for index, item in enumerate(posed):
            for other in posed[index + 1:]:
                # Joined printed tray pieces and stationary supports are checked too.
                a, b = item["shape"].BoundingBox(), other["shape"].BoundingBox()
                if a.xmax <= b.xmin or a.xmin >= b.xmax or a.ymax <= b.ymin or a.ymin >= b.ymax or a.zmax <= b.zmin or a.zmin >= b.zmax:
                    continue
                overlap = item["shape"].intersect(other["shape"]).Volume()
                if overlap > 0.01:
                    collisions.append(dict(servo_angle_deg=angle, a=item["name"], b=other["name"], volume_mm3=round(overlap, 4)))
    if collisions:
        raise ValueError(f"Mechanical travel collision(s): {collisions[:5]}")
    return samples


def verify_electronics(items, metadata):
    def overlap(a, b):
        x, y = a.BoundingBox(), b.BoundingBox()
        if x.xmax <= y.xmin or x.xmin >= y.xmax or x.ymax <= y.ymin or x.ymin >= y.ymax or x.zmax <= y.zmin or x.zmin >= y.zmax:
            return 0
        return a.intersect(b).Volume()

    for index, item in enumerate(items):
        for other in items[index + 1:]:
            if overlap(item["shape"], other["shape"]) > 0.01:
                raise ValueError(f"Electronics collision: {item['name']} / {other['name']}")
    heads = metadata["fastener_head_reservations"]
    for head in heads:
        shape = cylinder(head["radius_mm"], head["height_mm"], head["at"], head["axis"])
        for item in items:
            if overlap(shape, item["shape"]) > 0.01:
                raise ValueError(f"Fastener head blocked: {head['owner']} / {item['name']}")
    return dict(electronics_volume_intersections="PASS", fastener_head_clearances="PASS", fastener_heads_checked=len(heads))


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=root / "parametry.json")
    parser.add_argument("--output", type=Path, default=root / "output")
    args = parser.parse_args()
    p = json.loads(args.config.read_text(encoding="utf-8-sig"))
    parts, lift_items, purchased, mechanics = build(p)
    from electronics import build_electronics
    electronics_parts, electronics_items, electronics_bought, electronics_metadata = build_electronics(p)
    parts.update(electronics_parts)
    electronics_checks = verify_electronics(electronics_items + electronics_bought, electronics_metadata)
    from preview import make_case_layout, write_preview, write_bom
    case_items = make_case_layout(p, lift_items + purchased, electronics_items + electronics_bought, mechanics)
    report = dict(status=p["status"], mechanics=mechanics, electronics=electronics_metadata, parts={}, checks={}, limitations=[
        "Case internal dimensions, decorative head width, PCB holes and camera FPC are unmeasured.",
        "Friction hinge retention, lifting force, wear, vibration and cable travel require physical tests.",
        "Speaker/microphone acoustic performance and camera field of view require installed tests.",
        "Envelopes are not factory CAD; most fasteners, flexible straps and wires are not modelled."])
    report["travel_samples"] = verify_travel(p, lift_items + purchased)
    report["checks"]["linkage_and_solid_travel_21_samples"] = "PASS"
    report["checks"].update(electronics_checks)
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    old_report = out / "verification.json"
    previous_parts = set(json.loads(old_report.read_text(encoding="utf-8"))["parts"]) if old_report.exists() else set()
    # Regenerate only owned CAD files, keeping unrelated user output intact.
    for ext in ["stl", "step"]:
        (out / ext).mkdir(exist_ok=True)
        for stale in (out / ext).glob(f"*.{ext}"):
            if stale.stem in previous_parts and stale.stem not in parts:
                stale.unlink()
    for name, shape in parts.items():
        if not shape.isValid() or len(shape.Solids()) != 1 or shape.Volume() <= 0:
            raise ValueError(f"Invalid or disconnected part: {name}")
        normal = normalize_for_print(shape)
        bounds, bed = normal.BoundingBox(), p["printer_bed_mm"]
        dims = [bounds.xlen, bounds.ylen, bounds.zlen]
        if not ((dims[0] <= bed[0] and dims[1] <= bed[1]) or (dims[1] <= bed[0] and dims[0] <= bed[1])) or dims[2] > bed[2]:
            raise ValueError(f"{name} {dims} exceeds print envelope {bed}")
        cq.exporters.export(normal, str(out / "stl" / f"{name}.stl"), tolerance=0.12, angularTolerance=0.15)
        cq.exporters.export(shape, str(out / "step" / f"{name}.step"))
        report["parts"][name] = dict(dimensions_mm=[round(x, 3) for x in dims], volume_mm3=round(shape.Volume(), 3), solid_count=1, valid=True)
    for filename, items in [("single-compartment.step", lift_items + purchased), ("case-assembly.step", case_items)]:
        assembly = cq.Assembly(name=Path(filename).stem)
        for index, item in enumerate(items):
            assembly.add(item["shape"], name=f"part_{index:04d}", color=cq.Color(item["color"]))
        assembly.export(str(out / filename))
    report["checks"].update(print_envelope="PASS", single_solid_and_brep_validity="PASS",
                             provisional_case_envelope="PASS (internal dimensions not confirmed)")
    from verify_exports import verify
    meshes = {name: verify(out / "stl" / f"{name}.stl") for name in parts}
    for name, result in meshes.items():
        if abs(result["signed_volume_mm3"] - parts[name].Volume()) / parts[name].Volume() > 0.02:
            raise ValueError(f"STL/CAD volume mismatch: {name}")
    report["checks"]["exported_stl_closed_positive_volume"] = "PASS"
    (out / "verification.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (out / "stl-verification.json").write_text(json.dumps(meshes, indent=2) + "\n", encoding="utf-8")
    write_preview(out, p, case_items, lift_items + purchased, mechanics)
    write_bom(out, parts, case_items)
    module_parts = {name: parts[name] for name in {item["name"] for item in lift_items}}
    write_bom(out, module_parts, lift_items, filename="prvni-prihradka.csv", accessories=False)
    shutil.copy(args.config, out / "parametry.json")
    guide = (root / "README.md").read_text(encoding="utf-8")
    guide = guide.replace("](../../docs/", "](https://github.com/Blato58/DildoVander/blob/main/docs/")
    guide = guide.replace("](../docs/", "](https://github.com/Blato58/DildoVander/blob/main/kufr/docs/")
    for filename in ["NAVOD.md", "README.md"]:
        (out / filename).write_text(guide, encoding="utf-8")
    for filename in ["model.py", "electronics.py", "preview.py", "verify_exports.py", "requirements.txt", "test_model.py", "montazni-material.csv"]:
        shutil.copy(root / filename, out / filename)
    package_files = [out / ext / f"{name}.{ext}" for ext in ["stl", "step"] for name in parts]
    package_files += [out / filename for filename in [
        "single-compartment.step", "case-assembly.step", "verification.json", "stl-verification.json",
        "nahled.png", "nahled.html", "rozlozeni.png", "tiskovy-kusovnik.csv", "prvni-prihradka.csv",
        "parametry.json", "NAVOD.md", "README.md", "model.py", "electronics.py", "preview.py",
        "verify_exports.py", "requirements.txt", "test_model.py", "montazni-material.csv"]]
    with zipfile.ZipFile(out / "kufr-mechanika-prototyp.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for file in package_files:
            archive.write(file, file.relative_to(out))
    print(json.dumps(dict(parts=len(parts), lift_mm=round(mechanics["lift_mm"], 2), tilt_deg=round(mechanics["tilt_deg"], 2), output=str(out), checks=report["checks"])))


if __name__ == "__main__":
    main()
