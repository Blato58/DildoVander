"""Removable lower-body side-bay carriers; free connector ends and strap retention.

Mount holes belong to our carrier, never to an unmeasured purchased PCB. The
integration supplies floor rails/risers under the exported carrier hole pattern.
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


def _nut_geometry(x, y):
    """M3 hex nut: nominal 5.5 AF, 2.4 thick; clearance in its bore."""
    nut = cq.Workplane("XY").polygon(6, 5.5 / math.cos(math.pi / 6)).extrude(2.4).val()
    return nut.translate((x, y, 11)).cut(cylinder(1.6, 3, (x, y, 10.7))).clean()


def _nut_channel(x, y, direction, reach):
    """6.6 mm wide side-loading pocket/insertion prism above 3 mm floor."""
    dx, dy = direction
    if dx:
        start = x - 3.3 if dx > 0 else x - reach
        return box(reach + 3.3, 6.6, 2.6, (start, y - 3.3, 11))
    start = y - 3.3 if dy > 0 else y - reach
    return box(6.6, reach + 3.3, 2.6, (x - 3.3, start, 11))


def _floor_fasteners(parts, items, mounts, p):
    """Provide every floor hole a through-shaft and an accessible side nut.

Nuts load the 3 mm printed floor at Z8..11. They slide into Z11..13.6
pockets before components are installed; long blind bolts are unnecessary.
Heads/countersinks belong to the baseplate below this module's Z8 interface.
"""
    declarations = [(mount["part"], hole) for mount in mounts for hole in mount["mounting_holes"]
                    if abs(hole[2] - 8) < .01]
    nuts = [_nut_geometry(x, y) for _, (x, y, _) in declarations]
    shanks = [cylinder(1.5, 5.4, (x, y, 8)) for _, (x, y, _) in declarations]
    fasteners = []
    by_name = {item.name: item for item in items}
    for index, (part_name, (x, y, _)) in enumerate(declarations):
        instance = by_name[part_name]
        world = instance.shape
        old_world_bounds, canonical_bounds = bounds(world), bounds(parts[part_name])
        origin = [old_world_bounds[j] - canonical_bounds[j] for j in range(3)]
        shaft_clearance = cylinder(1.7, 6, (x, y, 7.8))
        others = [item.shape for item in items if item.kind != "space" and item.name != part_name]
        others += [nut for j, nut in enumerate(nuts) if j != index]
        chosen = None
        for direction in ((0, -1), (0, 1), (-1, 0), (1, 0)):
            pocket = _nut_channel(x, y, direction, 4.1)
            # At 7.3 mm centre travel the full 6.35 mm nut clears the 8 mm
            # boss; 10.6 mm includes its leading half and an insertion margin.
            insertion = _nut_channel(x, y, direction, 10.6)
            candidate = world.cut(shaft_clearance).cut(pocket).clean()
            b = bounds(insertion)
            if b[0] < 0 or b[1] < 0 or b[3] > p["case"]["inside_width"] or b[4] > p["case"]["inside_depth"]:
                continue
            if overlap(candidate, insertion) > .1 or overlap(candidate, nuts[index]) > .1:
                continue
            if any(overlap(insertion, other) > .1 for other in others):
                continue
            if not candidate.isValid() or len(candidate.Solids()) != 1:
                continue
            chosen = candidate, direction, insertion
            break
        if chosen is None:
            raise ValueError(f"No accessible M3 side-nut pocket for {part_name} at {x},{y}")
        instance.shape, direction, insertion = chosen
        parts[part_name] = instance.shape.translate(tuple(-v for v in origin))
        nut_name, shaft_name = f"floor_M3_nut_{index + 1:02d}", f"floor_M3_shaft_{index + 1:02d}"
        fasteners.append({"carrier": part_name, "hole_mm": [x, y, 8], "nut": nut_name, "shaft": shaft_name,
                          "nut_af_mm": 5.5, "nut_thickness_mm": 2.4, "pocket_height_mm": 2.6,
                          "floor_under_nut_mm": 3, "shaft_clearance_diameter_mm": 3.4,
                          "insertion_direction": list(direction), "insertion_bounds_mm": bounds(insertion),
                          "head_interface": "baseplate countersink below Z8; trim screw tip to Z13.4 (8.4 total length if head face Z5)",
                          "maximum_tip_z_mm": 13.4,
                          "assembly": "Insert nut from side before component; trim/deburr screws to end flush with nut, then fasten from underneath before fitting into case."})
        for name, shape in ((nut_name, nuts[index]), (shaft_name, shanks[index])):
            items.append(Item(name, shape, "metal", "service_fasteners",
                              evidence="required extra M3 hardware; not confirmed purchased",
                              metadata={"purchased": False, "carrier": part_name, "head_not_modelled": name == shaft_name}))
    return fasteners


def build(p):
    parts, items, mounts, intended, retainers = {}, [], [], [], []
    limitations = [
        "Nominal purchased envelopes, unmeasured connector directions and final wiring remain provisional.",
        "Degson envelope, camera variant/FPC, PCB thickness and amplifier/microphone heights provisional.",
        "Carrier retention requires the per-component thin straps/ties listed in retainers; straps and M3 hardware are not confirmed purchased.",
        "Speaker faces point inward; mechanism may obstruct sound. Physical acoustic acceptance is pending.",
        "Camera optical axis is nominal front/up 45 degrees; actual sensor/lens field of view is unknown.",
        "Carrier holes are design interfaces to removable rails/risers, not holes drilled into wood.",
    ]

    def printed(name, shape, at, note, holes=()):
        if name in parts:
            raise ValueError(f"Duplicate printable part {name}")
        parts[name] = shape
        items.append(Item(name, shape.translate(at), "print", "service", part=name,
                          metadata={"mounting": note, "print_orientation": "canonical Z up; bed bottom Z0"}))
        mounts.append({"part": name, "carrier_bounds_mm": bounds(shape.translate(at)),
                       "mounting_holes": [[at[0] + x, at[1] + y, at[2]] for x, y in holes],
                       "hole_diameter_mm": 3.4, "support_base_z_mm": 8,
                       "mounting": note})

    def add_component(name, key, at, dimensions=None, shape=None, kind="electronics", extra=None):
        md = hardware.evidence(key)
        md.update(extra or {})
        md["installed_dimensions_mm"] = list(dimensions or hardware.CATALOG[key][:3])
        items.append(Item(name, (shape or hardware.envelope(key, dimensions)).translate(at),
                          kind, "hardware", evidence=md["dimension_status"], metadata=md))

    def reserve(name, dims, at, purpose, owner):
        items.append(Item(name, box(*dims, at), "space", "clearance", metadata={"purpose": purpose, "owner": owner}))

    def mount_component(name, key, at, dims, kind="electronics", model=None):
        origin = (at[0] - 3.6, at[1] - 3.6, at[2] - 5.6)
        shape = cradle(dims[0], dims[1])
        b = bounds(shape)
        # Holes through end crossmembers; free end access remains above them.
        holes = [(4, 4), (b[3] - 4, 4), (4, b[4] - 4), (b[3] - 4, b[4] - 4)]
        for x, y in holes:
            shape = shape.cut(cylinder(1.7, 4, (x, y, -1)))
        printed(name + "_carrier", shape.clean(), origin,
                "Four edge pads; open connector ends; two releasable straps; M3 holes to removable rail/risers.", holes)
        strap_width, strap_centres, retainer_width = _strap_settings(dims[1], b[4])
        items[-1].metadata["strap_tunnel_width_mm"] = strap_width
        items[-1].metadata["required_retainer_width_mm"] = retainer_width
        items[-1].metadata["strap_tunnel_height_mm"] = 1.8
        items[-1].metadata["required_retainer_thickness_max_mm"] = 1.5
        retainers.append({"component": name, "tunnel_width_mm": strap_width,
                          "tunnel_height_mm": 1.8, "retainer_width_mm": retainer_width,
                          "retainer_thickness_max_mm": 1.5,
                          "paths_bounds_mm": [[origin[0], origin[1] + y - strap_width / 2, origin[2] + 3.3,
                                               origin[0] + b[3], origin[1] + y + strap_width / 2, origin[2] + 5.1]
                                              for y in strap_centres],
                          "status": "required extra strap/cable tie; not confirmed purchased"})
        add_component(name, key, at, dims, model, kind)
        return origin

    # Left bay: standing powerbank, extraction upwards without removing speakers.
    mount_component("powerbank", "powerbank", (6.6, 65, 13.6), (26.5, 139, 80), "power")
    reserve("powerbank_extraction", (26.5, 139, 220), (6.6, 65, 93.6),
            "Straight upward extraction after unplugging and releasing both straps; lid must be open.", "powerbank")
    reserve("powerbank_ports", (26.5, 14, 24), (6.6, 48, 69.6),
            "Provisional USB port access at front end; verify actual port orientation.", "powerbank")

    mount_component("speaker_left", "speaker", (39, 151, 59), (21, 100, 45), model=hardware.speaker())
    # Reflect across the local X midplane for the right inward-facing acoustic face.
    right_speaker = hardware.speaker().mirror("YZ", (10.5, 0, 0))
    mount_component("speaker_right", "speaker", (371.4, 151, 59), (21, 100, 45), model=right_speaker)
    reserve("speaker_left_acoustic", (7.9, 94, 38), (60, 154, 63),
            "Inward face gap only; no unobstructed sound-field claim.", "speaker_left")
    reserve("speaker_right_acoustic", (7.5, 94, 38), (363.9, 154, 63),
            "Inward face gap only; no unobstructed sound-field claim.", "speaker_right")

    # Lowest electronics layer. High current uses terminal blocks, not perfboard tracks.
    mount_component("buck", "buck", (374, 10, 13.6), (46, 31, 19), "power")
    reserve("buck_cooling", (46, 31, 8), (374, 10, 32.6), "Cooling/voltage-adjustment access.", "buck")
    mount_component("pd", "pd", (397, 50, 13.6), (28, 14, 11), "power")
    reserve("pd_usb_access", (24, 6, 8), (399, 44, 18), "Front USB connector approach above low crossmember; cable bend beyond this local gap unvalidated.", "pd")
    mount_component("pca9685", "pca9685", (398, 99, 13.6), (25, 70, 15))
    mount_component("mcp23017", "mcp23017", (372.2, 99, 13.6), (18, 43, 5))
    mount_component("pixel_shifter", "pixel_shifter", (372.2, 153, 13.6), (15, 25.5, 10.2))
    mount_component("amplifier_left", "amplifier", (40.6, 69, 13.6), (18, 18, 8))
    mount_component("amplifier_right", "amplifier", (40.6, 103, 13.6), (18, 18, 8))

    # Four separate common-pole blocks: +5/GND logic and switched +5/GND drives.
    for i, y in enumerate((73, 103, 133, 163), 1):
        mount_component(f"degson_{i}", "degson", (374, y, 34.6), (35, 22, 16), "power")
        reserve(f"degson_{i}_lever_access", (35, 22, 2), (374, y, 50.6),
                "Lever opening needs removable upper speaker/platform; terminal size still provisional.", f"degson_{i}")

    # One of two bought perfboards: six bought capacitors plus low-current resistors.
    mount_component("distribution_perfboard", "perfboard", (373, 189, 13.6), (50, 70, 1.6))
    for i, (x, y) in enumerate([(377, 195), (391, 195), (405, 195), (377, 215), (391, 215), (405, 215)], 1):
        add_component(f"capacitor_{i}", "capacitor", (x, y, 15.2), (10, 10, 17), hardware.capacitor(), "power",
                      {"circuit": ("LED" if i <= 2 else "switched drives" if i <= 4 else "logic/audio"),
                       "mounting": "soldered and strain relieved on perfboard; lead spacing provisional"})
    # OE two parallel22k, LED330, two amplifier DIN100k and PTT15k.
    for i, value in enumerate(("22k", "22k", "330", "100k", "100k", "15k"), 1):
        shape = hardware.resistor()
        items.append(Item(f"resistor_{i}_{value}", shape.translate((376 + (i - 1) * 8, 248, 15.2)),
                          "electronics", "hardware", evidence="catalog package; lead spacing provisional",
                          metadata={"value_ohm": value, "mounting": "soldered to distribution_perfboard"}))

    # Front-left user controls and exposed microphone.
    mount_component("breaker", "breaker", (7, 7, 13.6), (45, 28, 13), "power")
    mount_component("microphone", "microphone", (8, 43, 13.6), (16.51, 12.7, 5))
    reserve("microphone_acoustic", (16.51, 12.7, 15), (8, 43, 18.6),
            "Top acoustic port open; no lid mounted microphone.", "microphone")

    # Panel barrel locations use catalog diameter. Faces sit on the upper surface.
    panel = box(61, 37, 3)
    for x in (17, 44):
        panel = panel.cut(cylinder(9.8, 5, (x, 16, -1)))
    for x in (0, 58):
        panel = panel.fuse(box(3, 37, 64, (x, 0, -64)))
    panel = panel.translate((0, 0, 64))
    printed("switch_panel", panel.clean(), (367, 6, 8),
            "Self supporting 64 mm legs; two 19.6 design cutouts for 19 mm catalog barrels. Verify snap retention.")
    add_component("rocker_red", "rocker", (372, 10, 48), shape=hardware.panel_switch("rocker"), kind="power")
    add_component("rocker_green", "rocker", (399, 10, 48), shape=hardware.panel_switch("rocker"), kind="power")
    # PTT independent high accessible top, away from the powerbank lift prism.
    ptt_panel = box(21.2, 21.2, 3).cut(cylinder(6.3, 5, (10.6, 10.6, -1)))
    for x in (0, 18.2):
        ptt_panel = ptt_panel.fuse(box(3, 21.2, 49, (x, 0, -49)))
    ptt_holes = []
    for x in (0, 13.2):
        for y in (0, 13.2):
            ptt_panel = ptt_panel.fuse(box(8, 8, 3, (x, y, -49)))
            ptt_panel = ptt_panel.cut(cylinder(1.7, 5, (x + 4, y + 4, -50)))
            ptt_holes.append((x + 4, y + 4))
    printed("ptt_panel", ptt_panel.translate((0, 0, 49)).clean(), (37.4, 39.4, 8),
            "Two full side webs and four floor bosses; provisional 12.6 cutout for catalog 12 thread; nut fit to verify.", ptt_holes)
    add_component("ptt", "ptt", (41, 43, 28), shape=hardware.panel_switch("ptt"))

    for owner, dims, at in (
        ("pca9685", (25, 7, 10), (398, 91, 18)),
        ("mcp23017", (18, 6, 11), (372.2, 92, 13.6)),
        ("pixel_shifter", (15, 6, 11), (372.2, 146, 13.6)),
        ("amplifier_left", (18, 6, 8), (40.6, 62, 13.6)),
        ("amplifier_right", (18, 6, 8), (40.6, 96, 13.6)),
        ("speaker_left", (12, 6, 8), (44, 144, 62)),
        ("speaker_right", (12, 6, 8), (376, 144, 62)),
        ("rocker_red", (24, 24, 8), (372, 10, 40)),
        ("rocker_green", (24, 24, 8), (399, 10, 40)),
        ("ptt", (12, 12, 8), (42, 44, 20)),
    ):
        reserve(owner + "_connector_access", dims, at,
                "Local open connector/terminal approach; unmeasured terminal direction and final wire bend remain provisional.", owner)
    reserve("breaker_actuator_access", (45, 28, 15), (7, 7, 26.6),
            "Open top for manual reset; actual actuator orientation to verify.", "breaker")

    # Shared raised platform keeps ESP and provisional camera in the front right.
    platform = box(58.8, 103, 3).cut(box(51.6, 95.8, 4, (3.6, 3.6, -.5)))
    platform = platform.fuse(box(58.8, 4, 3, (0, 62, 0)))
    # ESP support pads/edge rails occupy margins outside the 28 x 57 envelope.
    platform = platform.fuse(box(2.4, 60, 6, (0, 2, 0)))
    platform = platform.fuse(box(2, 60, 6, (31.6, 2, 0)))
    for x in (2.4, 27.6):
        for y in (3.6, 56.6):
            platform = platform.fuse(box(4, 4, 3, (x, y, 0)))
    holes = []
    for x, y in holes:
        platform = platform.cut(cylinder(1.7, 9, (x, y, -1)))
    printed("esp_camera_platform", platform.clean(), (369.6, 48, 77),
            "Shared removable platform; ESP edge pads/strap retention; M3 risers down to floor rails.", holes)
    add_component("esp32", "esp32", (372, 51.6, 80), (28, 57, 13))
    reserve("esp32_usb", (28, 12, 13), (372, 39, 80), "Provisional front USB connector approach above switch faces; confirm board orientation.", "esp32")

    # Camera saddle printed flat on its side: open centre, edge contact, two straps.
    # Keep the two strap channels away from the pivot/lock bolt holes.
    camera_cradle = cradle(24, 24, strap_centres=(9, 27))
    for y in (4, 22):
        camera_cradle = camera_cradle.cut(cylinder(1.7, 34, (-1, y, 4.5), (1, 0, 0)))
    tilted = camera_cradle.rotate((0, 0, 0), (1, 0, 0), 45)
    tb = bounds(tilted)
    tilted = tilted.translate((0, -tb[1], -tb[2]))
    printed("camera_saddle", tilted.clean(), (386, 116, 83),
            "Two side M3 pivots and two locking bolts; stationary arc slots permit 35-50 degree adjustment. Two board straps.")
    retainers.append({"component": "camera_provisional", "tunnel_width_mm": 6,
                      "tunnel_height_mm": 1.8, "retainer_width_mm": 3, "retainer_thickness_max_mm": 1.5,
                      "path_pose": {"translation_mm": [386, 116 - tb[1], 83 - tb[2]], "rotation_x_degrees": 45,
                                    "local_bounds_mm": [[0, 6, 3.3, 31.2, 12, 5.1], [0, 24, 3.3, 31.2, 30, 5.1]]},
                      "status": "required extra thin strap/cable tie; not confirmed purchased"})
    # Compute exactly the same pose as its open edge saddle.
    cam = hardware.camera().translate((3.6, 3.6, 5.6)).rotate((0, 0, 0), (1, 0, 0), 45)
    cam = cam.translate((0, -tb[1], -tb[2]))
    add_component("camera_provisional", "camera", (386, 116, 83), shape=cam,
                  extra={"optical_direction": [0, -0.70710678, 0.70710678], "not_delivered": True})
    reserve("camera_fpc", (24, 16, 6), (389.6, 98, 99), "Flexible service loop; connector/flex variant unknown.", "camera_provisional")

    # The stationary cheeks are outside the moving saddle, with real pivots and
    # an arc slot for the second bolt. Adjustment is manual, not a deployable mast.
    pivot_y = 116 + (4 - 4.5) / math.sqrt(2) - tb[1]
    pivot_z = 83 + (4 + 4.5) / math.sqrt(2) - tb[2]
    support_at = (382.6, 110, 80)
    support = box(38, 41, 3)
    for x in (0, 35):
        cheek = box(3, 31, 29, (x, 6, 3))
        cheek = cheek.cut(cylinder(1.7, 5, (x - 1, pivot_y - 110, pivot_z - 80), (1, 0, 0)))
        for degrees in range(35, 51):
            radians = math.radians(degrees)
            cheek = cheek.cut(cylinder(1.8, 5,
                               (x - 1, pivot_y - 110 + 18 * math.cos(radians),
                                pivot_z - 80 + 18 * math.sin(radians)), (1, 0, 0)))
        support = support.fuse(cheek)
    camera_platform_holes = [(6.4, 4), (35.4, 4), (6.4, 38), (35.4, 38)]
    for x, y in camera_platform_holes:
        support = support.cut(cylinder(1.7, 5, (x, y, -1)))
    printed("camera_angle_support", support.clean(), support_at,
            "M3 to ESP platform; pivot/arc-slot friction adjustment 35-50 degrees. Lock before operating.", camera_platform_holes)
    # Axle/locking-bolt shanks are required new hardware, never declared bought.
    for side, x in (("left", 382.6), ("right", 414.2)):
        for label, y, z in (("pivot", pivot_y, pivot_z),
                            ("lock", pivot_y + 18 / math.sqrt(2), pivot_z + 18 / math.sqrt(2))):
            items.append(Item(f"camera_{side}_{label}_bolt", cylinder(1.5, 6.4, (x, y, z), (1, 0, 0)),
                              "metal", "hardware", evidence="required extra M3 bolt, not confirmed purchased",
                              metadata={"purpose": "mechanical retention/angle adjustment", "purchased": False}))
    lens_centre = (401.6, 116 + (15.6 - 17.6) / math.sqrt(2) - tb[1],
                   83 + (15.6 + 17.6) / math.sqrt(2) - tb[2])
    items.append(Item("camera_optical_axis", cylinder(1.2, 185, lens_centre, (0, -1, 1)), "space", "clearance",
                      metadata={"purpose": "nominal centre ray front/up over front rim; not a field-of-view guarantee", "owner": "camera_provisional"}))

    # The upper right service modules share a physical rack. Its two broad legs
    # miss every low PCB; all joined print geometry is a single canonical part.
    rack_names = [f"degson_{i}_carrier" for i in range(1, 5)] + ["speaker_right_carrier", "esp_camera_platform"]
    rack = box(8, 8, 24, (420, 68, 8)).fuse(box(8, 8, 24, (420, 177, 8)))
    rack = rack.fuse(box(7, 123, 3, (421, 68, 29)))
    rack = rack.fuse(box(3, 123, 3, (368.3, 68, 29)))
    for y in (69.4, 99.4, 129.4, 159.4, 185.6):
        # Positive overlap into the raised edge ledges avoids edge-only boolean
        # junctions: exact 3 mm endpoints produced valence-four STL edges at Z32.
        rack = rack.fuse(box(59.7, 3.4, 3, (368.3, y - .2, 29)))
    # Speaker beam is clear of the 50.6 mm Degson envelopes, with two rack columns.
    rack = rack.fuse(box(3, 110.8, 6.4, (367.8, 147.4, 47)))
    for y in (154, 183):
        rack = rack.fuse(box(3, 4, 24.4, (368.3, y, 29)))
    for x, y, w, d in ((422, 68, 6, 8), (422, 144, 6, 8), (369.6, 125, 3, 6)):
        rack = rack.fuse(box(w, d, 48, (x, y, 29)))
    for name in rack_names:
        instance = next(i for i in items if i.name == name)
        rack = rack.fuse(instance.shape)
    # Camera fasteners pass through actual platform end/cross members.
    for x, y in ((389, 114), (418, 114), (389, 148), (418, 148)):
        rack = rack.cut(cylinder(1.7, 5, (x, y, 76)))
    for x, y in ((424, 72), (424, 181)):
        rack = rack.cut(cylinder(1.7, 6, (x, y, 7)))
    parts = {k: v for k, v in parts.items() if k not in rack_names}
    items[:] = [i for i in items if i.name not in rack_names]
    mounts[:] = [m for m in mounts if m["part"] not in rack_names]
    rack_at = (367.8, 48, 8)
    printed("right_service_rack", rack.translate(tuple(-v for v in rack_at)).clean(), rack_at,
            "Connected upper modules; 8 mm floor feet, two M3 clearance holes; removable for terminal servicing.",
            [(424 - rack_at[0], 72 - rack_at[1]), (424 - rack_at[0], 181 - rack_at[1])])

    # Left speaker has four positive feet to the floor plate, outside the lift prism.
    name = "speaker_left_carrier"
    instance = next(i for i in items if i.name == name)
    raised = instance.shape
    speaker_floor_holes = []
    for x in (37, 55.6):
        for y in (147.4, 250.2):
            raised = raised.fuse(box(8, 8, 48.4, (x, y, 8)))
            raised = raised.cut(cylinder(1.7, 6, (x + 4, y + 4, 7)))
            speaker_floor_holes.append([x + 4, y + 4, 8])
    left_at = (35.4, 147.4, 8)
    parts[name] = raised.translate(tuple(-v for v in left_at)).clean()
    instance.shape = parts[name].translate(left_at)
    mounts[:] = [m for m in mounts if m["part"] != name]
    mounts.append({"part": name, "carrier_bounds_mm": bounds(instance.shape), "mounting_holes": speaker_floor_holes,
                   "hole_diameter_mm": 3.4, "support_base_z_mm": 8, "mounting": "Four 8 mm square floor feet; M3 to removable baseplate."})

    # Front power modules and their switch panel form one retained removable
    # carrier; this also braces the tall switch webs against the lower cradles.
    front_names = ["switch_panel", "buck_carrier", "pd_carrier"]
    front = box(61, 3, 3, (367, 6, 8)).fuse(box(3, 10, 3, (425, 40, 8)))
    front_holes = []
    for name in front_names:
        front = front.fuse(next(i.shape for i in items if i.name == name))
        front_holes.extend(next(m["mounting_holes"] for m in mounts if m["part"] == name))
    parts = {k: v for k, v in parts.items() if k not in front_names}
    items[:] = [i for i in items if i.name not in front_names]
    mounts[:] = [m for m in mounts if m["part"] not in front_names]
    front_at = (367, 6, 8)
    printed("front_power_control_carrier", front.translate(tuple(-v for v in front_at)).clean(), front_at,
            "Buck/PD open edge cradles brace switch-panel side webs; M3 floor interfaces; releasable board straps.",
            [(x - front_at[0], y - front_at[1]) for x, y, _ in front_holes])

    # A regenerated case width moves the entire right bay coherently, including
    # its carrier interfaces. Narrower incompatible cases fail assembly checks.
    width_delta = p["case"]["inside_width"] - 431.8
    if width_delta:
        for item in items:
            if bounds(item.shape)[0] > 350:
                item.shape = item.shape.translate((width_delta, 0, 0))
        for mount in mounts:
            if mount["carrier_bounds_mm"][0] > 350:
                mount["carrier_bounds_mm"][0] += width_delta
                mount["carrier_bounds_mm"][3] += width_delta
                for hole in mount["mounting_holes"]:
                    hole[0] += width_delta
        for retainer in retainers:
            for path in retainer.get("paths_bounds_mm", []):
                if path[0] > 350:
                    path[0] += width_delta
                    path[3] += width_delta
            if "path_pose" in retainer:
                retainer["path_pose"]["translation_mm"][0] += width_delta

    floor_holes = [hole for mount in mounts for hole in mount["mounting_holes"] if abs(hole[2] - 8) < .01]
    fasteners = _floor_fasteners(parts, items, mounts, p)

    return parts, items, {
        "limitations": limitations, "mounts": mounts, "floor_mounting_holes": floor_holes,
        "floor_fasteners": fasteners, "retainers": retainers, "intended_collision_pairs": intended,
        "installed_counts": {"esp32": 1, "pca9685": 1, "mcp23017": 1, "pixel_shifter": 1,
                             "microphone": 1, "amplifier": 2, "speaker": 2, "buck": 1, "pd": 1,
                             "powerbank": 1, "ptt": 1, "rocker": 2, "breaker": 1, "perfboard": 1,
                             "degson": 4, "capacitor": 6, "camera_provisional": 1},
        "spares_excluded": ["second PCA", "second red rocker", "four capacitors", "second perfboard",
                            "stock resistors", "unused LED roll", "multimeter", "separate SD reader/card"],
        "unused_stock_pending_distribution": ["six Degson; four installed blocks are a provisional distribution choice"],
        "required_extra_hardware": ["8 mm releasable straps for large cradles; 3 mm straps or 2.5 mm thin cable ties for small cradles; thickness <=1.5 mm",
                                    "M3 5.5 AF x2.4 mm nuts; countersunk screws trimmed/deburred flush at Z13.4 (8.4 mm total if head face Z5); camera pivot/lock hardware",
                                    "insulating capacitor strain relief", "PTT nut/rocker retention supplied or separately verified"],
        "print_notes": "PETG; canonical cradle bases on bed; camera saddle orientation must be evaluated in slicer. No strength acceptance claimed.",
        "camera": {"optical_origin_mm": [lens_centre[0] + width_delta, lens_centre[1], lens_centre[2]],
                   "optical_direction": [0, -1 / math.sqrt(2), 1 / math.sqrt(2)],
                   "field_of_view": "unknown ordered module; no numeric cone accepted"},
        "camera_adjustment": {"pivot_point_mm": [386 + width_delta, pivot_y, pivot_z],
                              "axis": [1, 0, 0], "nominal_degrees": 45, "range_degrees": [35, 50],
                              "moving_items": ["camera_saddle", "camera_provisional", "camera_left_lock_bolt", "camera_right_lock_bolt"]},
    }
