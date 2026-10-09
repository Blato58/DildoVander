"""Four removable side cassettes with integrated cradles and detachable bridges.

Unknown PCB hole patterns are replaced by edge supports and releasable straps.
Keyed glued feet attach to the wooden floor with adhesive hook-and-loop.
"""
from __future__ import annotations

import math
import cadquery as cq

from common import Item, box, bounds, cylinder, overlap
from components import hardware


def _strap_settings(depth, outer_y):
    if depth < 20:
        return 2.8, (outer_y / 2 - 1.6, outer_y / 2 + 1.6), 2.5
    return min(10, depth / 4), (outer_y / 3, 2 * outer_y / 3), (3 if depth < 40 else 8)


def cradle(width, depth, clearance=.6, wall=3, strap_centres=None):
    """Open centre/ends, four edge pads, side fences and two strap tunnels.

Board bottom is z=5.6; fences overlap only the free margin outside its
    conservative envelope. Tunnels are 2.8-10 mm wide by 1.8 mm high;
    per-component metadata specifies a compatible retainer.
"""
    outer_x, outer_y = width + 2 * (wall + clearance), depth + 2 * (wall + clearance)
    part = box(outer_x, outer_y, 3).cut(box(outer_x - 6, outer_y - 6, 4, (3, 3, -.5)))
    for x in (0, outer_x - wall):
        part = part.fuse(box(wall, outer_y, 9, (x, 0, 0)))
    # Continuous edge ledges overlap the lower frame, ensuring one solid.
    for x in (2, outer_x - 5):
        part = part.fuse(box(3, outer_y - 6, 5.6, (x, 3, 0)))
    for x in (0, outer_x - 8):
        for y in (0, outer_y - 8):
            part = part.fuse(box(8, 8, 3, (x, y, 0)))
    strap_width, default_centres, _ = _strap_settings(depth, outer_y)
    for x in (0, outer_x - wall):
        slot_x = -1 if x == 0 else outer_x - 6
        for y in (strap_centres or default_centres):
            part = part.cut(box(7, strap_width, 1.8, (slot_x, y - strap_width / 2, 3.3)))
    # Tangential support contact, not an intersection with the component.
    return part.clean()


def build(p):
    parts, items, retainers, mounts = {}, [], [], []
    floor_shapes = {
        "left_front_cassette": box(62, 124, 3, (2, 5, 8)),
        "left_rear_cassette": box(62, 133, 3, (2, 130, 8)),
        "right_front_cassette": box(64, 82, 3, (366.8, 5, 8)),
        "right_rear_cassette": box(64, 174, 3, (366.8, 89, 8)),
    }
    # Stiffening channels on the long rear cassette bridge the uncertain batten
    # area without using that wood as a dimensionally exact mounting datum.
    for x in (366.8, 427.8):
        floor_shapes["right_rear_cassette"] = floor_shapes["right_rear_cassette"].fuse(box(3, 174, 6, (x, 89, 8)))

    def printed(name, shape, note, orientation="flat"):
        parts[name] = shape.clean()
        items.append(Item(name, parts[name], "print", "service", part=name,
                          metadata={"mounting": note, "print_orientation": orientation}))
        mounts.append({"part": name, "mounting": note, "mounting_holes": []})

    def component(name, key, at, dims=None, shape=None, kind="electronics"):
        md = hardware.evidence(key)
        md["installed_dimensions_mm"] = list(dims or hardware.CATALOG[key][:3])
        items.append(Item(name, (shape if shape is not None else hardware.envelope(key, dims)).translate(at),
                          kind, "hardware", evidence=md["dimension_status"], metadata=md))

    def reserve(name, dims, at, purpose, owner):
        items.append(Item(name, box(*dims, at), "space", "clearance", metadata={"purpose": purpose, "owner": owner}))

    def mount(name, key, at, dims, cassette=None, model=None, kind="electronics"):
        origin = (at[0] - 3.6, at[1] - 3.6, at[2] - 5.6)
        carrier = cradle(dims[0], dims[1]).translate(origin)
        if cassette:
            floor_shapes[cassette] = floor_shapes[cassette].fuse(carrier)
        else:
            printed(name + "_carrier", carrier, "Open end cradle; ties through two tunnels; direct seat on bridge with full-bay wrap retention.")
        width, centres, tie = _strap_settings(dims[1], dims[1] + 7.2)
        retainers.append({"component": name, "retainer_width_mm": tie, "tunnel_width_mm": width,
                          "retainer_thickness_max_mm": 1.5, "tunnel_height_mm": 1.8,
                          "paths_bounds_mm": [[origin[0], origin[1] + y - width / 2, origin[2] + 3.3,
                                                origin[0] + dims[0] + 7.2, origin[1] + y + width / 2, origin[2] + 5.1] for y in centres]})
        component(name, key, at, dims, model, kind)

    mount("breaker", "breaker", (7, 7, 13.6), (45, 28, 13), "left_front_cassette", kind="power")
    mount("microphone", "microphone", (8, 43, 13.6), (16.51, 12.7, 5), "left_front_cassette")
    for name, y in (("amplifier_left", 69), ("amplifier_right", 103)):
        mount(name, "amplifier", (43.6, y, 13.6), (18, 18, 8), "left_front_cassette")
    # Four provisional common-pole terminals underneath the removable bank.
    for index, y in enumerate((74, 104, 134, 164), 1):
        cassette = "left_front_cassette" if index <= 2 else "left_rear_cassette"
        mount(f"degson_{index}", "degson", (3.8, y, 13.6), (35, 22, 16), cassette, kind="power")
    # Two solid end saddles grow straight out of the flat cassette; no roof to
    # bridge, no hidden fasteners, and terminal access follows bank removal.
    for cassette, y in (("left_front_cassette", 64), ("left_rear_cassette", 198)):
        foot = box(33, 8, 20, (3.8, y, 11))
        for x in (3.8, 33.7):
            foot = foot.fuse(box(3.1, 8, 6, (x, y, 31)))
        foot = foot.cut(box(35, 3, 2, (2.8, y + 2.5, 25)))
        floor_shapes[cassette] = floor_shapes[cassette].fuse(foot)
    component("powerbank", "powerbank", (7.2, 65, 31), (26.5, 139, 80), kind="power")
    reserve("powerbank_extraction", (26.5, 139, 220), (7.2, 65, 111), "Unplug, release two saddle straps and lift straight up.", "powerbank")
    reserve("powerbank_ports", (26.5, 8, 24), (7.2, 56, 87), "Front-end USB access assumption; actual ports to verify.", "powerbank")
    retainers.append({"component": "powerbank", "retainer_width_mm": 2.5, "tunnel_width_mm": 3, "tunnel_height_mm": 2,
                      "retainer_thickness_max_mm": 1.5,
                      "paths_bounds_mm": [[2.8, y + 2.5, 25, 37.8, y + 5.5, 27] for y in (64, 198)],
                      "instruction": "Two releasable thin straps through end saddles; pad contact faces. Remove bank for transport."})
    mount("speaker_left", "speaker", (43.2, 151, 13.6), (21, 100, 45), "left_rear_cassette", hardware.speaker())

    mount("buck", "buck", (374, 10, 13.6), (46, 31, 19), "right_front_cassette", kind="power")
    mount("pd", "pd", (395, 50, 13.6), (28, 14, 11), "right_front_cassette", kind="power")
    mount("pca9685", "pca9685", (398, 99, 13.6), (25, 70, 15), "right_rear_cassette")
    mount("mcp23017", "mcp23017", (375, 99, 13.6), (18, 43, 5), "right_rear_cassette")
    mount("pixel_shifter", "pixel_shifter", (375, 153, 13.6), (15, 25.5, 10.2), "right_rear_cassette")
    mount("distribution_perfboard", "perfboard", (373, 189, 13.6), (50, 70, 1.6), "right_rear_cassette")
    for index, (x, y) in enumerate(((377, 195), (391, 195), (405, 195), (377, 215), (391, 215), (405, 215)), 1):
        component(f"capacitor_{index}", "capacitor", (x, y, 15.2), shape=hardware.capacitor(), kind="power")
    for index, value in enumerate(("22k", "22k", "330", "100k", "100k", "15k")):
        items.append(Item(f"resistor_{index + 1}_{value}", hardware.resistor().translate((376 + index * 8, 248, 15.2)),
                          "electronics", "hardware", evidence="catalog package; soldered on perfboard", metadata={"value_ohm": value}))

    def hood(width, depth, height, at, holes):
        roof = box(width, depth, 3, (0, 0, height))
        for x, y, radius in holes:
            roof = roof.cut(cylinder(radius, 5, (x, y, height - 1)))
        for x in (0, width - 3):
            roof = roof.fuse(box(3, depth, height, (x, 0, 0)))
        return roof.translate(at)

    printed("switch_hood", hood(61, 37, 61, (367, 6, 11), [(17, 16, 9.8), (44, 16, 9.8)]),
            "Roof-down print. Glue webs to cassette; retaining snap barrels must be physically checked.", "roof_down")
    component("rocker_red", "rocker", (372, 10, 48), shape=hardware.panel_switch("rocker"), kind="power")
    component("rocker_green", "rocker", (399, 10, 48), shape=hardware.panel_switch("rocker"), kind="power")
    printed("ptt_hood", hood(21.2, 21.2, 46, (37.4, 39.4, 11), [(10.6, 10.6, 6.3)]),
            "Roof-down print. Glue webs to cassette; use supplied PTT nut.", "roof_down")
    component("ptt", "ptt", (41, 43, 28), shape=hardware.panel_switch("ptt"))

    bridge = hood(63.8, 103, 59, (367, 48, 11), [])
    # Allow the rear cassette's 6 mm ribs under the detachable bridge.
    for x in (367, 427.8):
        bridge = bridge.cut(box(3.1, 62, 3.6, (x, 89, 10.9)))
    printed("controller_bridge", bridge, "Roof-down print. Secure to floor with releasable 6 mm wrap at Y145..151; remove bridge to access PCBs.", "roof_down")
    mount("esp32", "esp32", (374, 51.6, 78.6), (28, 57, 13))
    # Cradle-to-roof is glued; cradle tunnels retain the removable board.
    next(i for i in items if i.name == "esp32_carrier").metadata["mounting"] = "Glue cradle to bridge roof; two releasable thin straps retain ESP."
    tilted = cradle(24, 24, strap_centres=(9, 27)).rotate((0, 0, 0), (1, 0, 0), 45)
    tb = bounds(tilted)
    tilted = tilted.translate((0, -tb[1], -tb[2]))
    bottom = cq.Workplane("YZ").polyline([(6.164, 0), (28.426, 0), (28.426, 22.262)]).close().extrude(31.2).val()
    printed("camera_wedge", tilted.fuse(bottom).translate((386, 116, 73)),
            "Print on X side; glue flat bottom to bridge. Nominal 45 degree camera; two thin releasable PCB straps.", "side")
    cam = hardware.camera().translate((3.6, 3.6, 5.6)).rotate((0, 0, 0), (1, 0, 0), 45).translate((386, 116 - tb[1], 73 - tb[2]))
    component("camera_provisional", "camera", (0, 0, 0), shape=cam)
    lens = [401.6, 116 - tb[1] + (15.6 - 17.6) / math.sqrt(2), 73 - tb[2] + (15.6 + 17.6) / math.sqrt(2)]
    retainers.append({"component": "camera_provisional", "retainer_width_mm": 3, "tunnel_width_mm": 6,
                      "tunnel_height_mm": 1.8, "retainer_thickness_max_mm": 1.5,
                      "path_pose": {"translation_mm": [386, 116 - tb[1], 73 - tb[2]], "rotation_x_degrees": 45,
                                    "local_bounds_mm": [[0, 6, 3.3, 31.2, 12, 5.1], [0, 24, 3.3, 31.2, 30, 5.1]]},
                      "instruction": "Two 3 mm releasable ties through saddle channels, below lens. Camera variant still unknown."})

    # A roof-down L bracket and a flat cradle are simpler to print than the old
    # tall rack. A wrap around the full cassette retains both at Y234..242.
    speaker_bridge = box(30.2, 107.2, 3, (366.8, 151, 33.4)).fuse(box(2.5, 107.2, 19.4, (366.8, 151, 14)))
    printed("speaker_bridge", speaker_bridge, "Roof-down print; full-bay wrap retains speaker, bridge and cassette. Supported on rear cassette edge rib.", "roof_down")
    mount("speaker_right", "speaker", (374, 151, 42), (21, 100, 45), model=hardware.speaker().mirror("YZ", (10.5, 0, 0)))
    # Actual retention routes around complete cassettes, outside the lower PCB
    # envelopes. The speaker wrap follows the convex contact outline rather
    # than pretending a tensioned fabric band can bridge empty space rigidly.
    def wrap(name, points, y, width):
        profile = cq.Workplane("XZ").polyline(points).close()
        outside = profile.offset2D(.8).extrude(width).val()
        inside = cq.Workplane("XZ").polyline(points).close().extrude(width + 2).val().translate((0, 1, 0))
        items.append(Item(name, outside.cut(inside).translate((0, y + width, 0)).clean(), "cable", "service",
                          evidence="required releasable strap; nominal 0.8 mm fabric; closure not modelled",
                          metadata={"mounting": "Release wrap before removing upper bridge; secure closure on accessible upper face."}))
    wrap("controller_retention_wrap", [(366.8, 8), (430.8, 8), (430.8, 73), (367, 73), (366.8, 11)], 145, 6)
    wrap("speaker_retention_wrap", [(366.8, 8), (430.8, 8), (430.8, 14), (395, 87), (374, 87), (366.8, 36.4)], 234, 8)

    for owner, dims, at in (
        ("buck", (46, 31, 8), (374, 10, 32.6)),
        ("pd", (24, 6, 8), (397, 44, 18)),
        ("pca9685", (25, 7, 10), (398, 91, 18)),
        ("mcp23017", (18, 6, 11), (375, 92, 13.6)),
        ("pixel_shifter", (15, 6, 11), (375, 146, 13.6)),
        ("amplifier_left", (18, 6, 8), (43.6, 62, 13.6)),
        ("amplifier_right", (18, 6, 8), (43.6, 96, 13.6)),
        ("rocker_red", (24, 24, 8), (372, 10, 40)),
        ("rocker_green", (24, 24, 8), (399, 10, 40)),
        ("ptt", (12, 12, 8), (42, 44, 20)),
        ("esp32", (28, 9, 13), (374, 42, 78.6)),
    ):
        reserve(owner + "_connector_access", dims, at, "Provisional local connector/cooling allowance; final connector and wire bend to verify.", owner)
    reserve("microphone_acoustic", (16.51, 12.7, 15), (8, 43, 18.6), "Open acoustic port.", "microphone")
    reserve("breaker_actuator_access", (45, 28, 15), (7, 7, 26.6), "Reset actuator approach.", "breaker")
    reserve("camera_fpc", (24, 8, 6), (389.6, 107, 96), "Provisional service loop; actual flex length and connector unknown.", "camera_provisional")
    # End feet sit outside the entire conservative brace strip. Shallow keyed
    # sockets locate separate flat-print feet; glue keeps them with the cassette.
    feet = []
    for name, shape in floor_shapes.items():
        # Adjacent cradle walls share material after fusion. Reopen every tie
        # tunnel through that shared material; otherwise the neighbour blocks it.
        for retainer in retainers:
            for path in retainer.get("paths_bounds_mm", []):
                shape = shape.cut(box(*(path[i + 3] - path[i] for i in range(3)), at=path[:3]))
        left = name.startswith("left")
        x = 10 if left else 375
        ys = (12, 72) if "front" in name else (188, 246)
        for y in ys:
            feet.append([x, y, 2])
            for dx in (5, 37):
                shape = shape.cut(box(4.6, 4.6, 1.6, (x + dx - .3, y + 3.7, 7.9)))
        printed(name, shape, "Flat print; glue keyed feet into underside sockets, apply hook-loop beneath feet. Board ties use integrated tunnels.")
    # Right bay follows case width; all measurements remain millimetres.
    delta = p["case"]["inside_width"] - 431.8
    if delta:
        for i in items:
            if bounds(i.shape)[0] > 350:
                i.shape = i.shape.translate((delta, 0, 0))
                if i.part:
                    parts[i.part] = i.shape
        for foot in feet:
            if foot[0] > 350:
                foot[0] += delta
        lens[0] += delta
    return parts, items, {
        "mounts": mounts, "feet": feet, "floor_mounting_holes": [], "floor_fasteners": [],
        "retainers": retainers, "intended_collision_pairs": [],
        "camera": {"optical_origin_mm": lens, "optical_direction": [0, -1 / math.sqrt(2), 1 / math.sqrt(2)], "field_of_view": "unknown"},
        "installed_counts": {"esp32": 1, "pca9685": 1, "mcp23017": 1, "pixel_shifter": 1, "microphone": 1,
                             "amplifier": 2, "speaker": 2, "buck": 1, "pd": 1, "powerbank": 1, "ptt": 1,
                             "rocker": 2, "breaker": 1, "perfboard": 1, "degson": 4, "capacitor": 6, "camera_provisional": 1},
        "limitations": ["Bodies and connector allowances are nominal, not physically fitted.",
                        "Unknown camera, Degson dimensions and board heights remain provisional.",
                        "Hook-loop adhesive, PETG glue and strap retention need a loaded carry test.",
                        "Remove powerbank to service terminals. Remove right bridges to service controller PCBs.",
                        "Acoustic projection and actual camera FOV require physical tests."],
        "spares_excluded": ["second PCA, second red rocker, second perfboard, unused stock, separate SD reader"],
    }


def print_shape(name, shape):
    if name.endswith("_hood") or name in {"controller_bridge", "speaker_bridge"}:
        return shape.rotate((0, 0, 0), (1, 0, 0), 180)
    if name == "camera_wedge":
        return shape.rotate((0, 0, 0), (0, 1, 0), -90)
    return shape
