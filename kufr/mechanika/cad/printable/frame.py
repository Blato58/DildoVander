"""Removable segmented frame: body feet at Z2, side electronics datum at Z8."""
import cadquery as cq
from common import Item, bounds, box, cylinder, drill, overlap


def build(p, floor_holes=()):
    c, m = p["case"], p["mechanism"]
    w, d = c["inside_width"], c["inside_depth"]
    parts, items, joints = {}, [], []
    x0 = w / 2 - 3 * m["pitch"]
    limits = [2, x0, x0 + 2 * m["pitch"], x0 + 4 * m["pitch"], x0 + 6 * m["pitch"], w - 2]
    if min(b - a for a, b in zip(limits, limits[1:])) < 20:
        raise ValueError("Case too narrow for service-side mounting rails")
    feet_y = [d / 2 - m["tray_length"] / 2 + 10, d / 2 + m["tray_length"] / 2 - 10]
    module_x = [w / 2 + (i - 2.5) * m["pitch"] for i in range(6)]
    side_ranges = [(2, x0 - 1), (x0 + 6 * m["pitch"] + 1, w - 2)]
    side_anchor_x = [x for a, b in side_ranges for x in [a + 12, b - 12]]

    def add(name, shape, note):
        parts[name] = shape
        items.append(Item(name, shape, "print", "frame", part=name, metadata={"purpose": note}))

    clip_x = module_x[::2]
    clip_y = feet_y[0] - 15
    for side, y in enumerate(feet_y):
        # A broad flat foot and an outer joint strip leave the module foot free.
        outward = -1 if side == 0 else 1
        ymin = y - 8 if side else y - 21
        hole_y = y + outward * 16
        for segment, (a, b) in enumerate(zip(limits, limits[1:])):
            holes = [(x + dx, y) for x in module_x for dx in [-16, 16] if a + 3 < x + dx < b - 3]
            holes.extend((x, y) for x in side_anchor_x if a + 3 < x < b - 3)
            if side == 0:
                holes.extend((x, clip_y) for x in clip_x if a + 3 < x < b - 3)
            for edge in [a, b]:
                if edge in limits[1:-1]:
                    holes.append((edge + (7 if edge == a else -7), hole_y))
            rail = drill(box(b - a, 29, 2, (a, ymin, 0)), holes, depth=4)
            add(f"frame_rail_{side + 1}_{segment + 1}", rail, "Floor rail, counterside splice strip outside mechanism feet")
        for joint, x in enumerate(limits[1:-1]):
            plate = drill(box(24, 9, 3, (x - 12, hole_y - 4.5, 2)),
                          [(x - 7, hole_y), (x + 7, hole_y)], z=1, depth=5)
            add(f"frame_splice_{side + 1}_{joint + 1}", plate, "Two M3 screws join adjacent floor rail segments")
            joints.extend([(x - 7, hole_y), (x + 7, hole_y)])
    floor_rails = [i.shape for i in items]
    for side, (a, b) in enumerate(side_ranges):
        for half, (front, rear) in enumerate([(3, d / 2), (d / 2, d - 3)]):
            shape = box(b - a, rear - front, 3, (a, front, 5))
            # Ends straddle the unknown brace footprint; where it widens the
            # baseplate remains on its 5 mm top rather than cutting the wood.
            ys = [front + 5, rear - 22] if half == 0 else [front + 22, rear - 5]
            for x in [a + 5, b - 5]:
                for y in ys:
                    shape = shape.fuse(cylinder(4, 5, (x, y, 0)))
            fy = feet_y[half]
            for x in [a + 12, b - 12]:
                shape = shape.fuse(box(8, 8, 3, (x - 4, fy - 4, 2)))
            holes = [(float(v[0]), float(v[1])) for v in floor_holes if a + 2 <= v[0] <= b - 2 and front + 2 <= v[1] <= rear - 2]
            holes.extend((x, fy) for x in [a + 12, b - 12])
            for rail in floor_rails:
                shape = shape.cut(rail)
            shape = drill(shape, holes, z=-1, depth=11)
            add(f"service_floor_{side + 1}_{half + 1}", shape.clean(), "Side service baseplate; supports at Z8; remove as a cassette")
    clip = box(14, 10, 3).fuse(box(14, 2, 7, (0, 0, 3)), box(14, 2, 7, (0, 8, 3)))
    clip = drill(clip, [(7, 5)], z=-1, depth=5)
    parts["harness_clip"] = clip
    for n, x in enumerate(clip_x, 1):
        items.append(Item(f"harness_clip_{n}", clip.translate((x - 7, clip_y - 5, 2)), "print", "frame", part="harness_clip",
                          metadata={"purpose": "M3 rail clip; tie flexible cable bundle across front, away from crank"}))
    # Carrier screws enter from below BEFORE the frame goes into the chest.
    # Recess their heads flush with the local underside; this is essential over
    # the 5 mm wooden brace, where an ordinary protruding head would hit wood.
    fasteners = []
    unique_holes = sorted({(round(v[0], 4), round(v[1], 4)) for v in floor_holes})
    for index, (x, y) in enumerate(unique_holes, 1):
        probe = cylinder(2.3, 10, (x, y, -.01)).cut(cylinder(1.75, 11, (x, y, -.02)))
        material = [i.shape.intersect(probe) for i in items if i.part]
        under = [bounds(s)[2] for s in material if s.Volume() > .01]
        if not under:
            raise ValueError(f"No frame support beneath carrier mounting hole {x, y}")
        bottom = max(0.0, min(under))
        shaft_cut = cylinder(1.7, 11, (x, y, -1))
        head_cut = cq.Solid.makeCone(3.2, 1.7, 1.5, cq.Vector(x, y, bottom), cq.Vector(0, 0, 1))
        cutter = shaft_cut.fuse(head_cut)
        for item in items:
            if item.part and overlap(item.shape, cutter) > .001:
                item.shape = item.shape.cut(cutter).clean()
                parts[item.part] = item.shape
        head = cq.Solid.makeCone(3, 1.5, 1.5, cq.Vector(x, y, bottom), cq.Vector(0, 0, 1))
        head = head.fuse(cylinder(1.5, 8 - bottom - 1.5, (x, y, bottom + 1.5))).clean()
        target_length = 13.4 - bottom
        fasteners.append({"x_mm": x, "y_mm": y, "head_bottom_z_mm": bottom,
                          "target_overall_length_mm": target_length,
                          "note": "Countersunk overall length includes head; select/trim after dry assembly; do not project into PCB"})
        items.append(Item(f"service_floor_bolt_{index:02d}_lower", head, "metal", "frame_interfaces",
                          evidence="required M3 countersunk fastener; exact length to fit, purchase not confirmed"))
    return parts, items, dict(rail_joints_m3_count=len(joints),
                             module_attachment_holes=[[x + dx, y] for x in module_x for y in feet_y for dx in [-16, 16]],
                             service_floor_datum_z_mm=8,
                             service_floor_fasteners=fasteners,
                             limitations=["No drilling into wood; liner/frame contact fit must be checked.",
                                          "Frame needs straps or fitted packing for transport; no unverified wood clamp loads.",
                                          "3 mm service plates are a prototype, not a calculated load rating."])
