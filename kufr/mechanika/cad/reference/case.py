"""Reference wooden chest with independent floor, walls, brace, lid and hardware."""
import math

from common import Item, assembly, box, cylinder, overlap, write_json


def hinge_axis(p):
    c = p["case"]
    return (0, c["inside_depth"] + c["wall_back"] + c["hinge_rear_offset"],
            c["inside_height"] + c["lid_gap"] / 2)


def rotate_lid(shape, p, angle):
    axis = hinge_axis(p)
    return shape.rotate(axis, (1, axis[1], axis[2]), -angle)


def build(p, angle=0):
    c = p["case"]
    w, d, h = (c[k] for k in ["inside_width", "inside_depth", "inside_height"])
    left, right, front, back = (c["wall_" + k] for k in ["left", "right", "front", "back"])
    outside_w, outside_d = w + left + right, d + front + back
    body = []

    def wood(name, shape, group="body"):
        item = Item(name, shape, "wood", group, evidence="reference: see parameters.evidence")
        (body if group == "body" else lid).append(item)

    wood("wood_floor", box(outside_w, outside_d, c["floor_thickness"], (-left, -front, -c["floor_thickness"])))
    wood("wood_left", box(left, d, h, (-left, 0, 0)))
    wood("wood_right", box(right, d, h, (w, 0, 0)))
    wood("wood_front", box(outside_w, front, h, (-left, -front, 0)))
    wood("wood_back", box(outside_w, back, h, (-left, d, 0)))
    wood("floor_cross_brace", box(w, c["brace_depth"], c["brace_height"],
                                  (0, d * c["brace_y_fraction"] - c["brace_depth"] / 2, 0)))
    lid = []
    z, lh = h + c["lid_gap"], c["lid_inside_height"]
    wood("lid_top", box(outside_w, outside_d, c["lid_top_thickness"], (-left, -front, z + lh)), "lid")
    wood("lid_left", box(left, d, lh, (-left, 0, z)), "lid")
    wood("lid_right", box(right, d, lh, (w, 0, z)), "lid")
    wood("lid_front", box(outside_w, front, lh, (-left, -front, z)), "lid")
    wood("lid_back", box(outside_w, back, lh, (-left, d, z)), "lid")
    axis = hinge_axis(p)
    for index, fraction in enumerate(c["hinge_x_fractions"], 1):
        x = fraction * w
        fixed_leaf = box(28, 1.5, 20, (x - 14, d + back, h - 20))
        for start in [x - 14, x + 8]:
            knuckle = cylinder(c["hinge_radius"], 6, (start, axis[1], axis[2]), (1, 0, 0))
            knuckle = knuckle.cut(cylinder(1.2, 8, (start - 1, axis[1], axis[2]), (1, 0, 0)))
            web = box(6, c["hinge_rear_offset"], 3, (start, d + back, axis[2] - 4))
            fixed_leaf = fixed_leaf.fuse(web, knuckle)
        body.append(Item(f"hinge_{index}_fixed_leaf", fixed_leaf.clean(), "metal", "body_hardware", evidence="provisional"))
        body.append(Item(f"hinge_{index}_pin", cylinder(1, 28, (x - 14, axis[1], axis[2]), (1, 0, 0)), "metal", "body_hardware", evidence="provisional"))
        barrel = cylinder(c["hinge_radius"], 15.6, (x - 7.8, axis[1], axis[2]), (1, 0, 0)).cut(cylinder(1.2, 17.6, (x - 8.8, axis[1], axis[2]), (1, 0, 0)))
        leaf = box(15.6, c["hinge_rear_offset"], 2, (x - 7.8, d + back, z + 1))
        leaf = leaf.fuse(box(15.6, 1.5, 15, (x - 7.8, d + back, z + 1)))
        lid.append(Item(f"hinge_{index}_moving_leaf", leaf.fuse(barrel), "metal", "lid_hardware", evidence="provisional"))
        body.append(Item(f"latch_{index}_body", box(20, 3, 24, (x - 10, -front - 3, h - 27)), "metal", "body_hardware", evidence="provisional"))
        lid.append(Item(f"latch_{index}_keeper", box(20, 2, 10, (x - 10, -front - 2, z + 3)), "metal", "lid_hardware", evidence="provisional"))
    # A real handle clearance shape rather than treating the front as unobstructed.
    handle = box(100, 12, 28, (w / 2 - 50, -front - 14, h * .42))
    handle = handle.cut(box(80, 14, 18, (w / 2 - 40, -front - 15, h * .42 + 5)))
    body.append(Item("front_handle", handle, "metal", "body_hardware", evidence="provisional"))
    for i, x in enumerate([-left, w + right - 12]):
        for j, y in enumerate([-front - 1, d + back]):
            body.append(Item(f"corner_strap_{i}_{j}", box(12, 1, 34, (x, y, 0)), "metal", "body_hardware", evidence="provisional"))
    posed = [Item(i.name, rotate_lid(i.shape, p, angle), i.kind, i.group, evidence=i.evidence) for i in lid]
    mount = box(w, d, h).cut(body[5].shape)
    return body, posed, Item("usable_inner_space", mount, "space", "reference")


def validate_and_export(p, root):
    body, closed_lid, mount = build(p)
    _, opened_lid, _ = build(p, p["case"]["lid_open_angle"])
    for i in body + closed_lid + [mount]:
        if not i.shape.isValid() or i.shape.Volume() <= 0:
            raise ValueError(f"Invalid reference geometry: {i.name}")
    collisions = []
    # Includes fixed and moving hinge knuckles and the pin as independent solids.
    for angle in range(math.ceil(p["case"]["lid_open_angle"]) + 1):
        for a in body:
            for b in closed_lid:
                volume = overlap(a.shape, rotate_lid(b.shape, p, angle))
                if volume > .1:
                    collisions.append([angle, a.name, b.name, round(volume, 4)])
    report = dict(status="PASS" if not collisions else "FAIL", units="mm",
                  inside_mm=[p["case"][k] for k in ["inside_width", "inside_depth", "inside_height"]],
                  hinge_axis_mm=list(hinge_axis(p)), sampled_lid_angles=math.ceil(p["case"]["lid_open_angle"]) + 1,
                  collisions=collisions, physical_dimension_verified=False,
                  connected_hinge_leaves=all(len(i.shape.Solids()) == 1 for i in body + closed_lid if "leaf" in i.name),
                  limitations=["Interior and brace height confirmed; walls chosen 3 mm by owner.",
                               "Floor, lid depth, hinge and brace footprint, handle and latches provisional.",
                               "No photographs accessible in current task; exterior not photo-reconstructed.",
                               "Hinge hardware fastening and strength require inspection; swept test is sampled."])
    write_json(root / "reference" / "validation.json", report)
    if collisions:
        raise ValueError(f"Reference lid/body collision: {collisions[:4]}")
    if not report["connected_hinge_leaves"]:
        raise ValueError("Disconnected hinge reference leaf")
    assembly(body, root / "reference" / "body.step")
    assembly(closed_lid, root / "reference" / "lid.step")
    assembly([mount], root / "reference" / "mounting-space.step")
    assembly(body + closed_lid, root / "reference" / "empty-closed.step")
    assembly(body + opened_lid, root / "reference" / "empty-open.step")
    return report
