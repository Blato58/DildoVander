"""Parametric printable concept for the six-wand case; dimensions in mm.

This is mechanical CAD, not firmware, servo calibration or measured hardware.
Run: python model.py [--config parametry.json] [--output output]
"""
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


def cylinder(r, length, at=(0, 0, 0), axis=(0, 0, 1)):
    return cq.Solid.makeCylinder(r, length, cq.Vector(*at), cq.Vector(*axis))


def holes_z(shape, points, diameter, z=-1, depth=100):
    for x, y in points:
        shape = shape.cut(cylinder(diameter / 2, depth, (x, y, z)))
    return shape.clean()


def validate_config(p):
    positive = ["tray_length_mm", "tray_width_mm", "base_width_mm", "rod_diameter_mm",
                "rod_length_mm", "crank_radius_mm", "compartment_pitch_mm"]
    for key in positive:
        if p[key] <= 0:
            raise ValueError(f"{key} must be positive")
    if p["compartment_count"] != 6 or p["segment_count"] != 3:
        raise ValueError("This prototype uses exactly six compartments and three segments")
    if not -80 <= p["angle_low_deg"] < p["angle_high_deg"] <= 80:
        raise ValueError("Crank must remain on its monotonic rising branch (-80..80 degrees)")
    if p["tray_length_mm"] < p["wand_max_length_mm"] + 16:
        raise ValueError("Tray needs at least 8 mm clearance at each wand end")
    if p["tray_width_mm"] < p["wand_max_diameter_mm"] + 10:
        raise ValueError("Wand diameter exceeds cradle/side clearance")
    if p["base_width_mm"] < p["tray_width_mm"] + 16:
        raise ValueError("Base must leave room for the side-mounted follower")
    if p["compartment_pitch_mm"] < p["base_width_mm"] + 2:
        raise ValueError("Neighbouring compartments overlap")
    if p["led_strip_width_mm"] > 12:
        raise ValueError("This tray supports LED strips up to 12 mm wide")
    for dims in [p["servo_body_xyz_mm"], p["vibration_body_xyz_mm"],
                 p["powerbank_body_xyz_mm"], *p["boxes"].values()]:
        if len(dims) != 3 or min(dims) <= 0:
            raise ValueError("Component dimensions must be three positive numbers")
    if p["vibration_body_xyz_mm"][0] > 32 or p["vibration_body_xyz_mm"][1] > 26:
        raise ValueError("Vibration cage needs redesign for a larger module")
    if p["vibration_body_xyz_mm"][2] > 17:
        raise ValueError("Vibration cage has only 17 mm of usable height")


def build(p):
    validate_config(p)
    parts, placements, purchased = {}, [], []
    L, W, B = p["tray_length_mm"], p["tray_width_mm"], p["base_width_mm"]
    S = L / 3
    rod_x, rod_y = 0, L / 2 - 12
    vib_y = -L/6
    rod_r = p["rod_diameter_mm"] / 2
    low = p["tray_floor_low_mm"]
    radius, axis_z = p["crank_radius_mm"], p["servo_axis_height_mm"]
    a0 = math.radians(p["angle_low_deg"])
    a1 = math.radians(p["angle_high_deg"])
    lift = radius * (math.sin(a1) - math.sin(a0))
    offset = low - axis_z - radius * math.sin(a0)
    if low - offset - 10 < 5:
        raise ValueError("Follower collides with the base; increase servo axis height")
    if low + lift + 12 > 4 + p["rod_length_mm"] - 4:
        raise ValueError("Guide rods are too short for the requested lift")
    pin_ys = [radius * math.cos(math.radians(a)) for a in
              range(math.ceil(p["angle_low_deg"]), math.floor(p["angle_high_deg"]) + 1)]
    ymin, ymax = min(pin_ys) - 3, max(pin_ys) + 3
    outer_y0, outer_y1 = ymin - 9, ymax + 9

    def add(name, shape, at=(0, 0, 0), color="#6b8ca9", group="lift", rotation=None):
        parts.setdefault(name, shape)
        placed = shape
        if rotation:
            placed = placed.rotate(*rotation)
        placed = placed.translate(at)
        placements.append(dict(name=name, shape=placed, color=color, group=group, at=at))

    # Three flat tray/base sections. Butt joints use replaceable M3 splice straps.
    for i in range(3):
        center_y = (i - 1) * S
        base = block(B, S, 4)
        tray = block(W, S, 4)
        tray = tray.fuse(block(2.4, S, 14, (-W / 2 + 1.2, 0, 4)),
                         block(2.4, S, 14, (W / 2 - 1.2, 0, 4)))
        if i != 1:
            ey = (-L / 2 if i == 0 else L / 2) - center_y
            tray = tray.fuse(block(W, 2.4, 9, (0, ey + (1.2 if i == 0 else -1.2), 4)))
        joint_holes = [(x, y) for x in [-12, 12] for y in [-S / 2 + 9, S / 2 - 9]]
        tray = holes_z(tray, joint_holes, 3.3, depth=6)
        base = holes_z(base, [(x, y) for x in [-B / 2 + 7, B / 2 - 7]
                             for y in [-S / 2 + 9, S / 2 - 9]], 3.3, depth=6)
        # A 200 mm LED strip mounts vertically on the inner side wall.
        y0, y1 = max(-100, center_y - S / 2), min(100, center_y + S / 2)
        if y1 > y0:
            tray = tray.cut(block(0.7, y1 - y0, p["led_strip_width_mm"] + 0.8,
                                  (-W / 2 + 2.2, (y0 + y1) / 2 - center_y, 5)))
        # Mounting holes follow global locations across segment boundaries.
        for x, y in [(x, y + vib_y) for x in [-16, 16] for y in [-17, 17]]:
            if center_y-S/2 <= y <= center_y+S/2:
                tray = tray.fuse(block(40, 8, 4, (0, y-center_y, 0)))
                tray = holes_z(tray, [(x, y-center_y)], 3.3, depth=6)
        if i == 1:
            tray = tray.cut(block(8, 8, 20, (-W / 2, -S / 2 + 4, 4)))
            # Follower attaches to floor with two accessible through bolts.
            for fy in [outer_y0 + 3, outer_y1 - 3]:
                tray = tray.fuse(block(12, 6, 4, (17, fy, 0)))
            tray = holes_z(tray, [(12, outer_y0 + 3), (12, outer_y1 - 3)], 3.3, depth=6)
        else:
            for sign in [1]:
                x, local_y = rod_x, (-rod_y if i == 0 else rod_y) - center_y
                socket = cylinder(6, 16, (x, local_y, 0))
                socket = socket.cut(cylinder(rod_r + 0.15, 13, (x, local_y, 4)))
                base = base.fuse(socket)
                sleeve = cylinder(6, 22, (x, local_y, -10))
                sleeve = sleeve.cut(cylinder(rod_r + p["rod_running_clearance_mm"] / 2,
                                              24, (x, local_y, -11)))
                tray = tray.fuse(sleeve).cut(cylinder(rod_r + p["rod_running_clearance_mm"]/2,
                                                      30, (x, local_y, -11)))
        add(f"base_{i+1}", base.clean(), (0, center_y, 0), "#4e6578")
        add(f"tray_{i+1}", tray.clean(), (0, center_y, low), "#d5ad6c")

    for joint in [-S / 2, S / 2]:
        splice = holes_z(block(32, 30, 3), [(x, y) for x in [-12, 12] for y in [-9, 9]],
                         3.3, depth=6)
        add("tray_splice", splice, (0, joint, low - 3), "#a88553")
        bs = holes_z(block(B, 30, 3), [(x, y) for x in [-B/2+7, B/2-7] for y in [-9, 9]],
                     3.3, depth=6)
        add("base_splice", bs, (0, joint, -3), "#4e6578")

    # A stock servo horn bolts to this adapter: no unverified printed spline.
    crank = cylinder(8, 4, (0, 0, 0), (1, 0, 0)).fuse(
        block(4, radius, 8, (2, radius/2, -4)),
        cylinder(5.5, 4, (0, radius, 0), (1, 0, 0)))
    for y, d in [(0, 3.5), (6, 2.2), (12, 2.2), (radius, 3.3)]:
        crank = crank.cut(cylinder(d/2, 6, (-1, y, 0), (1, 0, 0)))
    add("crank_horn_adapter", crank.clean(), (10, 0, axis_z), "#df765d",
        rotation=((0, 0, 0), (1, 0, 0), p["angle_low_deg"]))
    roller = cylinder(4, 7, axis=(1, 0, 0)).cut(cylinder(1.65, 9, (-1, 0, 0), (1, 0, 0)))
    add("roller", roller, (14.5, radius * math.cos(a0), axis_z + radius * math.sin(a0)), "#d9dfde")
    # Captured horizontal roller slot (Scotch yoke), moving on two end rods.
    follower = block(8, outer_y1 - outer_y0, 18, (19, (outer_y0+outer_y1)/2, -9))
    slot = block(10, ymax-ymin, 8.6, (19, (ymin+ymax)/2, -4.3))
    slot = slot.fuse(cylinder(4.3, 10, (14, ymin, 0), (1, 0, 0)),
                     cylinder(4.3, 10, (14, ymax, 0), (1, 0, 0)))
    follower = follower.cut(slot)
    for y in [outer_y0+3, outer_y1-3]:
        follower = follower.fuse(block(8, 6, offset-9, (19, y, 9)))
        follower = follower.fuse(block(12, 6, 4, (17, y, offset-4)))
        follower = follower.cut(cylinder(1.65, 6, (12, y, offset-5)))
    add("lift_follower", follower.clean(), (0, 0, low-offset), "#c58da9")

    # Strap cradle leaves horn face open; adapt to measured body and output axis.
    sx, sy, sz = p["servo_body_xyz_mm"]
    servo_x, servo_y = 7-sx/2, sy/2-8
    cradle = block(sx+8, sy+10, 3, (servo_x, servo_y, 0))
    cradle = cradle.fuse(block(3, sy+10, sz+2, (7-sx-2, servo_y, 3)),
                          block(sx+8, 3, sz+2, (servo_x, -10, 3)),
                          block(sx+8, 3, sz+2, (servo_x, sy-6, 3)))
    for y in [-5, sy-11]:
        cradle = cradle.cut(block(sx+10, 3, 1.6, (servo_x, y, 0.7)))
    # Two integral pillars bolt the cradle onto the central base panel.
    cradle_z = axis_z - sz/2 - 3
    for y in [-16, sy]:
        cradle = cradle.fuse(block(12, 8, cradle_z-1, (servo_x, y, 4-cradle_z)))
        cradle = cradle.cut(cylinder(1.65, cradle_z+5, (servo_x, y, 3-cradle_z)))
    # Keep a clear plane behind the rotating crank throughout its sweep.
    cradle = cradle.cut(block(40, sy+60, 100, (28.5, servo_y, -50)))
    parts["base_2"] = holes_z(parts["base_2"], [(servo_x, -16), (servo_x, sy)], 3.3, depth=6)
    for placement in placements:
        if placement["name"] == "base_2":
            placement["shape"] = parts["base_2"]
    add("servo_strap_cradle", cradle.clean(), (0, 0, cradle_z), "#76a7a2")
    servo_body = block(sx, sy, sz, (7-sx/2, sy/2-8, axis_z-sz/2))
    # The demo output-axis position must be matched to the delivered servo.
    purchased.append(dict(name="servo envelope (unmeasured)", shape=servo_body,
                          color="#4a86b7", group="lift"))

    cage = block(40, 40, 3)
    for x in [-16, 16]:
        for y in [-17, 17]:
            cage = cage.fuse(block(5, 5, 18, (x, y, 3)))
    cage = holes_z(cage, [(x, y) for x in [-16, 16] for y in [-17, 17]], 3.3, depth=25)
    for y in [-11, 11]:
        cage = cage.cut(block(36, 3, 4, (0, y, -0.5)))
    add("vibration_cage", cage.clean(), (0, vib_y, low-21), "#9bb96d")
    vx, vy, vz = p["vibration_body_xyz_mm"]
    purchased.append(dict(name="U059 envelope (unmeasured)",
                          shape=block(vx, vy, vz, (0, vib_y, low-18)), color="#374451", group="lift"))

    saddle = block(28, 12, 15)
    saddle = saddle.cut(cylinder(p["wand_max_diameter_mm"]/2, 14,
                                  (0, -7, 16), (0, 1, 0)))
    saddle = holes_z(saddle, [(-11, 0), (11, 0)], 2.5, depth=18)
    for y in [-L/2+35, L/2-35]:
        add("wand_saddle", saddle.clean(), (0, y, low+4), "#bb9862")
    # Lower support feet prevent tray load reaching the crank at rest.
    foot = block(12, 12, low-4)
    foot = holes_z(foot, [(0, 0)], 3.3, depth=low)
    for y in [-rod_y+18, rod_y-18]:
        add("lower_rest_pillar", foot, (0, y, 4), "#4e6578")
    collar = cylinder(5, 4).cut(cylinder(rod_r+0.15, 6, (0, 0, -1)))
    for x in [rod_x]:
        for y in [-rod_y, rod_y]:
            purchased.append(dict(name="4 mm guide rod", shape=cylinder(rod_r, p["rod_length_mm"], (x, y, 4)),
                                  color="#bbc9d3", group="lift"))
            add("rod_top_cap", collar, (x, y, p["rod_length_mm"]), "#4e6578")
    # Small cable clips for fixed frame; flexible loops serve the moving trays.
    clip = block(16, 12, 4).fuse(block(3, 12, 10, (-6.5, 0, 4)),
                                block(3, 12, 10, (6.5, 0, 4)))
    parts["cable_clip"] = holes_z(clip, [(0, 0)], 3.3, depth=6)
    gauge = block(60, 24, 5)
    gauge = holes_z(gauge, [(-20, 0), (-10, 0), (0, 0), (10, 0), (20, 0)], 3.3, depth=8)
    for i, diameter in enumerate([4.1, 4.3, 4.5, 4.7, 4.9]):
        gauge = gauge.cut(cylinder(diameter/2, 8, (-20+i*10, 7, -1)))
    parts["fit_coupon"] = gauge.clean()

    # Removable ventilated shells and universal perforated PCB carriers.
    for key, (w, d, h) in p["boxes"].items():
        wall = 2.4
        shell = block(w, d, h).cut(block(w-2*wall, d-2*wall, h, (0, 0, 3)))
        corners = [(x, y) for x in [-w/2+6, w/2-6] for y in [-d/2+6, d/2-6]]
        for x, y in corners:
            shell = shell.fuse(cylinder(4, h, (x, y, 0)))
        shell = holes_z(shell, corners, 3.3, depth=h+2)
        # Cable access on both opposing sides, with rounded edges via sleeves if needed.
        for x in [-w/2, w/2]:
            shell = shell.cut(block(8, min(22, d-24), 12, (x, 0, 8)))
        for x in range(-int(w/2)+16, int(w/2)-15, 10):
            for y in [-d/2, d/2]:
                shell = shell.cut(block(3, 8, min(10, h-12), (x, y, 8)))
        lid = holes_z(block(w, d, 3), corners, 3.3, depth=6)
        for x in range(-int(w/2)+16, int(w/2)-15, 10):
            lid = lid.cut(block(3, d-30, 6, (x, 0, -1)))
        if key == "microphone":
            lid = lid.cut(cylinder(3, 5, (0, 0, -1)))
        if key == "speaker":
            spx, spy, spz = p["speaker_body_xyz_mm"]
            if spx+12 > w or spy+12 > d or spz+8 > h:
                raise ValueError("Speaker does not fit shell")
            # Fixed dimensions from shopping list, but actual fixing holes are unmeasured.
        pw, pd = w-24, d-24
        panel = block(pw, pd, 3)
        panel_points = [(x, y) for x in range(-int(pw/2)+5, int(pw/2)-4, 10)
                        for y in range(-int(pd/2)+5, int(pd/2)-4, 10)]
        panel = holes_z(panel, panel_points, 3.3, depth=6)
        parts[f"{key}_box"] = shell.clean()
        parts[f"{key}_lid"] = lid.clean()
        if key not in ["speaker", "microphone"]:
            parts[f"{key}_pcb_panel"] = panel.clean()
    # Standoffs for PCB panels (M3 through bolt and nut, not PCB hole assumptions).
    parts["pcb_standoff"] = cylinder(3.5, 5).cut(cylinder(1.65, 7, (0, 0, -1)))
    bx, by, bz = p["powerbank_body_xyz_mm"]
    bank = block(bx+10, by+10, 3)
    bank = bank.fuse(block(3, by+10, 12, (-bx/2-3.5, 0, 3)),
                     block(3, by+10, 12, (bx/2+3.5, 0, 3)))
    for x in [-bx/3, bx/3]:
        bank = bank.cut(block(5, by+12, 2, (x, 0, 0.5)))
    bank = holes_z(bank, [(x, y) for x in [-bx/2+8, bx/2-8]
                          for y in [-by/2+8, by/2-8]], 3.3, depth=6)
    parts["powerbank_strap_cradle"] = bank.clean()
    panel = block(110, 46, 3)
    panel = panel.cut(cylinder(6.2, 5, (-32, 0, -1)),
                      block(20, 13, 5, (0, 0, -1)), block(20, 13, 5, (32, 0, -1)))
    panel = holes_z(panel, [(x, y) for x in [-49,49] for y in [-17,17]], 3.3, depth=6)
    parts["control_panel"] = panel.clean()
    return parts, placements, purchased, dict(lift_mm=lift, follower_offset_mm=offset,
                                             rod_x_mm=rod_x, rod_y_mm=rod_y)


def normalize_for_print(shape):
    b = shape.BoundingBox()
    return shape.translate((-b.xmin, -b.ymin, -b.zmin))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parent
    parser.add_argument("--config", type=Path, default=root/"parametry.json")
    parser.add_argument("--output", type=Path, default=root/"output")
    args = parser.parse_args()
    p = json.loads(args.config.read_text(encoding="utf-8"))
    parts, lift_items, purchased, mechanics = build(p)
    from preview import write_preview, make_case_layout, write_bom
    case_items = make_case_layout(p, parts, lift_items, purchased, mechanics)
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    for ext in ["stl", "step"]:
        (out/ext).mkdir(exist_ok=True)
    report = dict(status=p["status"], mechanics=mechanics, parts={}, checks={}, limitations=[
        "Unmeasured case, wands, servo output geometry and most component envelopes.",
        "No physical proof of vibration, lift force, wear, thermal behaviour or cable travel.",
        "Crank/yoke is backdrivable: independent passive support/retention must be designed and tested before powered presentation.",
        "STEP contains printed parts and labelled envelopes; bolts, wires and actual PCB designs are not modelled."])
    for name, shape in parts.items():
        if not shape.isValid() or len(shape.Solids()) != 1 or shape.Volume() <= 0:
            raise ValueError(f"Invalid or disconnected part: {name}")
        normal = normalize_for_print(shape)
        bounds = normal.BoundingBox()
        dims = [bounds.xlen, bounds.ylen, bounds.zlen]
        bed = p["printer_bed_mm"]
        if not ((dims[0] <= bed[0] and dims[1] <= bed[1]) or
                (dims[1] <= bed[0] and dims[0] <= bed[1])) or dims[2] > bed[2]:
            raise ValueError(f"{name} {dims} exceeds print envelope {bed}")
        cq.exporters.export(normal, str(out/"stl"/f"{name}.stl"), tolerance=0.12, angularTolerance=0.15)
        cq.exporters.export(shape, str(out/"step"/f"{name}.step"))
        report["parts"][name] = dict(dimensions_mm=[round(x, 3) for x in dims],
                                    volume_mm3=round(shape.Volume(), 3), solid_count=1, valid=True)
    # Module STEP retains exact CAD geometry and its coordinate system.
    assembly = cq.Assembly(name="single_compartment_DEMO")
    for i, item in enumerate(lift_items + purchased):
        assembly.add(item["shape"], name=f"{i:02d}_{item['name']}",
                     color=cq.Color(item["color"]))
    assembly.export(str(out/"single-compartment.step"))
    report["checks"]["print_envelope"] = "PASS"
    report["checks"]["single_solid_and_brep_validity"] = "PASS"
    # Exact pairwise solid intersection at eleven positions: printed moving parts
    # against printed stationary parts and metal rods (intentional contacts excluded).
    moving_names = {"tray_1", "tray_2", "tray_3", "tray_splice", "lift_follower",
                    "vibration_cage", "wand_saddle"}
    stationary = [i for i in lift_items if i["name"] not in moving_names | {"crank_horn_adapter", "roller"}]
    stationary += [i for i in purchased if i["name"] == "4 mm guide rod"]
    collisions = []
    crank_home = next(i["shape"] for i in lift_items if i["name"] == "crank_horn_adapter")
    for step in range(11):
        angle = p["angle_low_deg"] + (p["angle_high_deg"] - p["angle_low_deg"]) * step / 10
        dz = p["crank_radius_mm"] * (math.sin(math.radians(angle)) - math.sin(math.radians(p["angle_low_deg"])))
        for moving in [i for i in lift_items if i["name"] in moving_names]:
            ms = moving["shape"].translate((0, 0, dz))
            for fixed in stationary:
                overlap = ms.intersect(fixed["shape"]).Volume()
                if overlap > 0.01:
                    collisions.append(dict(angle_deg=angle, moving=moving["name"], fixed=fixed["name"], volume_mm3=overlap))
        crank_now = crank_home.rotate((10,0,p["servo_axis_height_mm"]),
                                     (11,0,p["servo_axis_height_mm"]), angle-p["angle_low_deg"])
        targets = stationary + [dict(i, shape=i["shape"].translate((0,0,dz)))
                                for i in lift_items if i["name"] in moving_names]
        for fixed in targets:
            overlap = crank_now.intersect(fixed["shape"]).Volume()
            if overlap > 0.01:
                collisions.append(dict(angle_deg=angle,moving="crank",fixed=fixed["name"],volume_mm3=overlap))
    report["checks"]["guide_and_crank_travel_11_samples"] = "PASS" if not collisions else collisions
    report["checks"]["provisional_case_envelope"] = "PASS (internal dimensions not confirmed)"
    (out/"verification.json").write_text(json.dumps(report, indent=2, ensure_ascii=False)+"\n", encoding="utf-8")
    if collisions:
        raise ValueError(f"Moving tray collisions: {collisions[:3]}")
    # Viewer and manufacturing bill use the same exact CAD source.
    case = cq.Assembly(name="six_wand_case_DEMO")
    for i, item in enumerate(case_items):
        case.add(item["shape"], name=f"part_{i:04d}", color=cq.Color(item["color"]))
    case.export(str(out/"case-assembly.step"))
    write_preview(out, p, case_items, lift_items + purchased, mechanics)
    write_bom(out, p, parts, lift_items)
    from verify_exports import verify
    mesh_results = {file.stem: verify(file) for file in sorted((out/"stl").glob("*.stl"))}
    if set(mesh_results) != set(parts):
        raise ValueError("Stale or missing STL files in output directory")
    for name, result in mesh_results.items():
        volume = parts[name].Volume()
        if abs(result["signed_volume_mm3"]-volume)/volume > .02:
            raise ValueError(f"STL volume differs from CAD: {name}")
    (out/"stl-verification.json").write_text(json.dumps(mesh_results,indent=2)+"\n",encoding="utf-8")
    report["checks"]["exported_stl_closed_positive_volume"] = "PASS"
    (out/"verification.json").write_text(json.dumps(report,indent=2,ensure_ascii=False)+"\n",encoding="utf-8")
    shutil.copy(args.config, out/"parametry.json")
    guide = (root/"README.md").read_text(encoding="utf-8")
    guide = guide.replace("](../../docs/", "](https://github.com/Blato58/DildoVander/blob/main/docs/")
    guide = guide.replace("](../docs/", "](https://github.com/Blato58/DildoVander/blob/main/kufr/docs/")
    for filename in ["NAVOD.md", "README.md"]:
        (out/filename).write_text(guide,encoding="utf-8")
    for filename in ["model.py", "preview.py", "verify_exports.py", "requirements.txt"]:
        shutil.copy(root/filename, out/filename)
    with zipfile.ZipFile(out/"kufr-mechanika-prototyp.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for file in sorted(out.rglob("*")):
            if file.is_file() and file.suffix != ".zip":
                archive.write(file, file.relative_to(out))
    print(json.dumps(dict(parts=len(parts), lift_mm=round(mechanics["lift_mm"], 2),
                          output=str(out), checks=report["checks"]), ensure_ascii=False))


if __name__ == "__main__":
    main()
