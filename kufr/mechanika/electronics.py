"""Open, printable electronics supports; all dimensions are millimetres.

Hardware envelopes are fit reservations, not factory models. No drilled pattern
claims to match an unmeasured PCB. Supports have independent feet on the case
floor; shelf openings let the speaker and camera posts pass without collision.
"""
from __future__ import annotations

import math

import cadquery as cq


def block(width, depth, height, at=(0, 0, 0)):
    return cq.Workplane("XY").box(
        width, depth, height, centered=(True, True, False)
    ).translate(at).val()


def cylinder(radius, length, at=(0, 0, 0), axis=(0, 0, 1)):
    return cq.Solid.makeCylinder(radius, length, cq.Vector(*at), cq.Vector(*axis))


def holes_z(shape, points, diameter=3.3, z=-1, depth=200):
    for x, y in points:
        shape = shape.cut(cylinder(diameter / 2, depth, (x, y, z)))
    return shape.clean()


def slot_z(width, length, at, depth=8):
    """Rounded Y slot for through bolts, not a claimed factory pattern."""
    x, y, z = at
    middle = max(0.01, length - width)
    return block(width, middle, depth, at).fuse(
        cylinder(width / 2, depth, (x, y - middle / 2, z)),
        cylinder(width / 2, depth, (x, y + middle / 2, z)),
    ).clean()


def shelf(width, post_openings):
    depth, post_offset_y = 150, 65
    shape = block(width, depth, 4, (0, 0, 40))
    post_offset_x = width / 2 - 10
    for x in (-post_offset_x, post_offset_x):
        for y in (-post_offset_y, post_offset_y):
            shape = shape.fuse(block(9, 9, 40, (x, y, 0)), block(20, 20, 4, (x, y, 0)))
            # The offset hole is outside the upright; nuts can be reached below.
            sign_x, sign_y = math.copysign(1, x), math.copysign(1, y)
            shape = holes_z(shape, [(x + 7 * sign_x, y + 7 * sign_y)])
    for x, y, w, d in post_openings:
        shape = shape.cut(block(w, d, 6, (x, y, 39)))
    for x in (-width / 3, 0, width / 3):
        for y in (-48, 0, 48):
            shape = shape.cut(block(3, 14, 6, (x, y, 39)))
    return shape.clean()


def universal_carrier(width, depth, clearance_openings=()):
    """Flat service plate with strap slots and accessible through-bolt holes."""
    shape = block(width, depth, 3)
    points = [(x, y) for x in (-width / 2 + 5, width / 2 - 5)
              for y in (-depth / 2 + 5, depth / 2 - 5)]
    shape = holes_z(shape, points, depth=5)
    for x in (-width / 2 + 10, width / 2 - 10):
        shape = shape.cut(slot_z(3.3, max(7, depth - 18), (x, 0, -1)))
    for x, y, w, d in clearance_openings:
        shape = shape.cut(block(w, d, 5, (x, y, -1)))
    return shape.clean()


def powerbank_cradle(p):
    length, width, _ = p["powerbank_body_xyz_mm"]
    clearance = p["powerbank_clearance_mm"]
    plate_width, plate_length = width + 14.2, length + 7
    shape = block(plate_width, plate_length, 3)
    stop_x = width / 2 + clearance + 1.2
    for x in (-stop_x, stop_x):
        shape = shape.fuse(block(2.4, length - 16, 6, (x, 0, 3)))
    # Both short ends remain open for PB720 ports, button and cable routing.
    for x in (-width / 2 - 5.7, width / 2 + 5.7):
        for y in (-40, 40):
            shape = shape.cut(block(3, 12, 5, (x, y, -1)))
    return holes_z(shape, [(x, y) for x in (-plate_width / 2 + 3.5, plate_width / 2 - 3.5)
                          for y in (-plate_length / 2 + 5, plate_length / 2 - 5)], depth=5)


def power_conversion_carrier():
    shape = universal_carrier(125, 100).translate((0, 0, 4))
    for x in (-60, 60):
        shape = shape.cut(block(18, 22, 10, (x, 40, -1)))
    shape = shape.cut(block(12, 12, 10, (42, 53, -1)))
    feet = [(x, y) for x in (-35, 35) for y in (-40, 40)]
    for x, y in feet:
        shape = shape.fuse(block(14, 14, 4, (x, y, 0)))
    shape = holes_z(shape, feet, depth=9)
    # Generic spacer pattern can be changed after measuring the actual boards.
    return holes_z(shape, [(x, y) for x in (-35, 35) for y in (-25, 25)], depth=9)


def insulating_spacer():
    return cylinder(4, 5).cut(cylinder(1.65, 7, (0, 0, -1))).clean()


def front_control_tower():
    """Removable panel with historical, explicitly unmeasured switch apertures."""
    shape = block(95, 18, 4)
    for x in (-34, 34):
        shape = shape.fuse(block(6, 4, 151, (x, 0, 4)))
    shape = shape.fuse(block(85, 4, 36, (0, 0, 155)))
    shape = shape.cut(cylinder(6.2, 8, (-25, -4, 173), (0, 1, 0)))
    for x in (0, 25):
        shape = shape.cut(block(20, 8, 13, (x, 0, 166.5)))
    return holes_z(shape, [(x, y) for x in (-40, 40) for y in (-6, 6)], depth=6)


def speaker_tower(p):
    width, height, depth = p["speaker_body_xyz_mm"]
    # The driver's 45 mm face is vertical. The front points along negative Y.
    column_x = width / 2 + 5
    shape = block(width + 18, 24, 4, (0, 18, 0))
    for x in (-column_x, column_x):
        shape = shape.fuse(block(6, 12, 88, (x, 18, 0)))
    shape = shape.fuse(block(width + 12, 38, 4, (0, 6, 84)))
    for x in (-width / 2 - 2.5, width / 2 + 2.5):
        shape = shape.fuse(block(3, depth + 4, 8, (x, 0, 88)))
    shape = holes_z(shape, [(x, y) for x in (-width / 2 + 2, width / 2 - 2)
                           for y in (10, 26)], depth=6)
    for x in (-width / 2 - 3, width / 2 + 3):
        for y in (-8, 8):
            shape = shape.cut(block(3, 5, 6, (x, y, 83)))
    return shape.clean()


def controller_camera_platform(p):
    shape = block(120, 78, 4, (23, 183, 151))
    for x in (-34, 75):
        shape = shape.fuse(block(8, 8, 151, (x, 218, 0)), block(22, 8, 4, (x, 218, 0)))
        shape = holes_z(shape, [(x - 8, 218), (x + 8, 218)], depth=6)
    # Small pads leave the controller underside and solder joints clear.
    for x in (-7, 53):
        for y in (180, 196):
            shape = shape.fuse(block(8, 8, 5, (x, y, 155)))
    for x in (-17, 63):
        for y in (179, 197):
            shape = shape.cut(block(3, 8, 6, (x, y, 150)))
    shape = holes_z(shape, [(-22, 150), (22, 150), (-22, 167), (22, 167)],
                    z=150, depth=7)
    return shape.clean()


def camera_aim_mount(p, angle=20):
    """Replaceable bolted wedge with a strap plate, upward toward negative Y."""
    width, height, _ = p["camera_body_xyz_mm"]
    plate_width = width + 12
    plate = block(plate_width, 3, height + 10, (0, 1.5, 0))
    for x in (-width / 2 - 3, width / 2 + 3):
        for z in (8, height + 2):
            plate = plate.cut(block(3, 5, 6, (x, 1.5, z)))
    plate = plate.rotate((0, 0, 0), (1, 0, 0), -angle).translate((0, 0, 3))
    base = block(54, 36, 3, (0, 10, 0))
    # Side gussets support the inclined plate without closing the FPC path.
    for x in (-plate_width / 2, plate_width / 2 - 3):
        gusset = cq.Workplane("YZ").polyline([(0, 3), (16, 3), (11, 32), (0, 3)]).close().extrude(3).val()
        base = base.fuse(gusset.translate((x, 0, 0)))
    shape = base.fuse(plate)
    return holes_z(shape, [(-22, 5), (22, 5), (-22, 22), (22, 22)], depth=5)


def microphone_tower():
    shape = block(40, 18, 4)
    shape = shape.fuse(block(40, 4, 180, (0, 0, 4)))
    for x in (-14, 14):
        # Rounded vertical M3 adjustment slot, 35 mm centre travel.
        opening = block(3.5, 8, 35, (x, 0, 130))
        opening = opening.fuse(cylinder(1.75, 8, (x, -4, 130), (0, 1, 0)),
                               cylinder(1.75, 8, (x, -4, 165), (0, 1, 0)))
        shape = shape.cut(opening)
    return holes_z(shape, [(-15, -6), (15, -6), (-15, 6), (15, 6)], depth=6)


def microphone_slider(p):
    width, height, _ = p["microphone_body_xyz_mm"]
    shape = block(width + 20, 3, height + 10, (0, 0, 0))
    for x in (-14, 14):
        shape = shape.cut(cylinder(1.65, 5, (x, -2.5, (height + 10) / 2), (0, 1, 0)))
    for x in (-width / 2 - 2, width / 2 + 2):
        for z in (3, height + 4):
            shape = shape.cut(block(3, 5, 3, (x, 0, z)))
    return shape.clean()


def build_electronics(p):
    """Return local printed shapes, global placements, hardware, fit metadata."""
    parts, placements, purchased = {}, [], []

    def add(name, shape, at=(0, 0, 0), color="#7298a4"):
        parts.setdefault(name, shape)
        placements.append(dict(name=name, shape=shape.translate(at), color=color,
                               group="fixed", at=at))

    def hardware(name, shape, at, color):
        purchased.append(dict(name=name, shape=shape.translate(at), color=color,
                              group="fixed", at=at))

    # Tall speaker supports pass through clearance windows in each shelf.
    power_deck = shelf(150, [(-55, -40, 8, 14), (55, -40, 8, 14), (42, 78, 10, 10)])
    length, width, height = p["powerbank_body_xyz_mm"]
    cradle_holes = [(x, y) for x in (-(width + 14.2) / 2 + 3.5, (width + 14.2) / 2 - 3.5)
                    for y in (-(length + 7) / 2 + 5, (length + 7) / 2 - 5)]
    add("power_shelf", holes_z(power_deck, cradle_holes, z=39, depth=6), (-76, 140, 0))
    logic_deck = shelf(144, [(-55, -40, 8, 14), (55, -40, 8, 14), (-1, 78, 10, 10)])
    # These holes match our carriers, not the hardware. M3 nuts are reached below.
    carrier_holes = []
    for cx, cy, w, d in [(80, 157, 80, 54), (48, 105, 40, 34),
                         (104, 105, 40, 34), (76, 200, 130, 25)]:
        carrier_holes.extend((cx - 76 + x, cy - 140 + y)
                             for x in (-w / 2 + 5, w / 2 - 5)
                             for y in (-d / 2 + 5, d / 2 - 5))
    add("logic_shelf", holes_z(logic_deck, carrier_holes, z=39, depth=6), (76, 140, 0))
    # This plate stays behind the speaker base beam; notches clear rear feet.
    add("power_conversion_floor_carrier", power_conversion_carrier(), (-76, 165, 0))
    for x in (-35, 35):
        for y in (-25, 25):
            add("pcb_insulating_spacer_5mm", insulating_spacer(), (-76 + x, 165 + y, 7))
    add("powerbank_open_cradle", powerbank_cradle(p), (-76, 140, 44))
    hardware("UGREEN PB720 fit envelope", block(width, length, height),
             (-76, 140, 47), "#344754")

    add("pca9685_universal_carrier", universal_carrier(80, 54), (80, 157, 44))
    for x in (48, 104):
        add("amplifier_universal_carrier", universal_carrier(40, 34), (x, 105, 44))
    add("power_safety_service_carrier", universal_carrier(130, 25),
        (76, 200, 44))

    speaker_width, speaker_height, speaker_depth = p["speaker_body_xyz_mm"]
    for index, x in enumerate((-76, 76), start=1):
        add("speaker_open_tower", speaker_tower(p), (x, 82, 0), "#738795")
        hardware(f"Waveshare 14595 speaker {index} fit envelope",
                 block(speaker_width, speaker_depth, speaker_height),
                 (x, 82, 88), "#33444b")

    # Platform coordinates become local to its print part at the lower-left post.
    pod = controller_camera_platform(p).translate((-23, -183, 0))
    add("controller_camera_platform", pod, (23, 183, 0), "#8199b1")
    add("camera_20deg_strap_mount", camera_aim_mount(p), (0, 145, 155), "#93abb2")
    cw, cd, ch = p["controller_body_xyz_mm"]
    hardware("ESP32-S3 controller fit envelope (unmeasured)", block(cw, cd, ch),
             (23, 188, 160), "#497463")
    cam_width, cam_height, cam_depth = p["camera_body_xyz_mm"]
    cam = block(cam_width, cam_depth, cam_height, (0, -cam_depth / 2, 5))
    cam = cam.fuse(cylinder(p["camera_lens_diameter_mm"] / 2, 3,
                           (0, -cam_depth, 5 + cam_height / 2), (0, -1, 0)))
    cam = cam.rotate((0, 0, 0), (1, 0, 0), -20)
    hardware("OV5640 camera fit envelope (unmeasured)", cam, (0, 145, 158), "#526b61")

    add("microphone_height_tower", microphone_tower(), (0, -214, 0), "#7298a4")
    add("front_control_tower", front_control_tower(), (103, -214, 0), "#879aaf")
    mw, mh, md = p["microphone_body_xyz_mm"]
    add("microphone_strap_slider", microphone_slider(p), (0, -217.5, 142))
    hardware("ICS43434 microphone fit envelope (unmeasured)", block(mw, md, mh),
             (0, -221.5, 147), "#497463")

    microphone_port = (0, -224, 155)
    speaker_centres = [(x, 82, 88 + speaker_height / 2) for x in (-76, 76)]
    camera_connector = (0, 151, 175)
    controller_connector = (0, 172, 166)
    fastener_heads = []

    def head(owner, at, axis=(0, 0, 1)):
        # Conservative M3 pan-head plus washer reservation, not a bolt standard.
        fastener_heads.append(dict(owner=owner, at=at, axis=axis, radius_mm=3, height_mm=3))

    for cx, width in [(-76, 150), (76, 144)]:
        for x in (-(width / 2 - 3), width / 2 - 3):
            for y in (-72, 72):
                head("shelf floor fixing", (cx + x, 140 + y, 4))
    for x, y in cradle_holes:
        head("powerbank cradle fixing", (-76 + x, 140 + y, 47))
    for cx, cy, w, d in [(80, 157, 80, 54), (48, 105, 40, 34),
                         (104, 105, 40, 34), (76, 200, 130, 25)]:
        for x in (-w / 2 + 5, w / 2 - 5):
            for y in (-d / 2 + 5, d / 2 - 5):
                head("right carrier fixing", (cx + x, cy + y, 47))
    for cx in (-76, 76):
        for x in (-speaker_width / 2 + 2, speaker_width / 2 - 2):
            for y in (10, 26):
                head("speaker foot fixing", (cx + x, 82 + y, 4))
    for cx in (-34, 75):
        for x in (-8, 8):
            head("camera platform foot fixing", (cx + x, 218, 4))
    for x in (-22, 22):
        for y in (150, 167):
            head("camera wedge fixing", (x, y, 158))
    for x in (-35, 35):
        for y in (-40, 40):
            head("power floor carrier fixing", (-76 + x, 165 + y, 7))
        for y in (-25, 25):
            head("PCB spacer bolt underside", (-76 + x, 165 + y, 1))
    for cx, xs in [(0, (-15, 15)), (103, (-40, 40))]:
        for x in xs:
            for y in (-6, 6):
                head("front tower foot fixing", (cx + x, -214 + y, 4))
    for x in (-14, 14):
        head("microphone slider fixing", (x, -222, 155), (0, 1, 0))

    metadata = {
        "units": "mm",
        "rear_min_y_mm": 65,
        "shelf_deck_top_z_mm": 44,
        "camera_platform_post_centres_mm": [[-34, 218], [75, 218]],
        "camera_platform_nominal_rear_case_edge_clearance_mm": 3,
        "powerbank_body_center_mm": [-76, 140, 47 + height / 2],
        "powerbank_body_positive_y_end_mm": 140 + length / 2,
        "powerbank_positive_y_cable_space_mm": p["case_inside_mm"][1] / 2 - (140 + length / 2),
        "powerbank_access": "Both short ends and top open; rear gap needs measured/right-angle USB plug",
        "speaker_face_direction": [0, -1, 0],
        "speaker_body_centres_mm": speaker_centres,
        "speaker_mount": "Open strap shelf; no factory holes or acoustic enclosure claimed",
        "speaker_to_microphone_distance_mm": [round(math.dist(c, microphone_port), 2) for c in speaker_centres],
        "microphone_port_design_mm": microphone_port,
        "microphone_port_height_adjustment_mm": [130, 165],
        "microphone_rim_clearance": "Unknown: set height from measured front rim; nominal port Z155",
        "controller_body_center_mm": [23, 188, 160 + ch / 2],
        "camera_body_center_mm": [0, 145 - cam_depth / 2 * math.cos(math.radians(20)) +
                                    (5 + cam_height / 2) * math.sin(math.radians(20)),
                                   158 + cam_depth / 2 * math.sin(math.radians(20)) +
                                    (5 + cam_height / 2) * math.cos(math.radians(20))],
        "camera_aim_up_deg": 20,
        "camera_aim_adjustment": "Replaceable M3-mounted printed wedge; regenerate angle after field-of-view test",
        "camera_lens_direction": [0, -math.cos(math.radians(20)), math.sin(math.radians(20))],
        "camera_connector_design_mm": camera_connector,
        "controller_connector_design_mm": controller_connector,
        "connector_design_distance_mm": round(math.dist(camera_connector, controller_connector), 2),
        "fpc_constraint": "Original paired FPC only; connector points/length/bend radius unmeasured, no fit acceptance",
        "controller_access": "Open top/short ends for USB, buttons and FPC; rear antenna free of printed cover",
        "print_orientation": {
            "shelves": "Deck down; use supports for mounting foot pads",
            "controller_camera_platform": "Deck down, support the 5 mm controller pads; posts grow upward",
            "speaker_open_tower": "Lay on its rear face with supports for the small offsets, not a 100 mm bridge",
            "microphone_height_tower": "Lay on rear face with supports under base offset, or brace tall print",
            "front_control_tower": "Lay on rear face with supports under base offset",
            "other_parts": "Flat base down; inspect camera gusset overhangs in slicer",
        },
        "service_reservations_mm": {"PCA9685_plate": [80, 54], "amplifier_plates": [40, 34, 2],
                                    "power_safety_plate": [130, 25],
                                    "power_conversion_floor_plate": [125, 100],
                                    "floor_plate_to_shelf_clear_height": 33,
                                    "floor_PCB_max_height_above_5mm_spacers": 25},
        "floor_service_access": "Remove left shelf for service; space includes insulating spacers, wiring and thermal clearance",
        "floor_service_mounting": "Four M3 floor holes at global X-111/-41,Y125/205; case floor fixing requires measurement",
        "pcb_insulating_spacer_height_mm": 5,
        "pcb_spacer_pattern": "Four generic M3 positions X-111/-41,Y140/190; adapt to measured board holes or straps",
        "inventory_mounting": {
            "UGREEN PB720": "Left open cradle, two straps",
            "Waveshare 14595 pair": "Two open speaker towers, independent straps/gaskets",
            "ESP32-S3 N16R8": "Common rear pod, 5mm pads and straps; USB/buttons/antenna/FPC access",
            "OV5640": "Common rear pod, replaceable20deg strap mount, original paired FPC only",
            "ICS43434": "Front height slider with isolation foam; port faces negativeY",
            "PCA9685": "Right80x54 universal carrier, measured spacers/straps",
            "DFRobot DFR0954 MAX98357A pair": "Two40x34 universal carriers; second amp missing from current purchase",
            "Joy-IT SBC-Buck5V5A": "Bottom125x100 power carrier; dimensions/terminals/height and thermal clearance unmeasured",
            "USB-C PD trigger": "Bottom power carrier, short20V connection to Joy-IT, insulated routing",
            "fuse, switch, terminals, capacitors": "Bottom power carrier; exact selected mounting dimensions unmeasured",
            "watchdog, load switch, enable logic": "Bottom power carrier; parts/PCB/insulation still require selection and fit",
            "MCP23017 Adafruit5346 and Pixel Shifter6066": "Right130x25 service plate, measured spacers/straps",
            "PTT, main switch, PHYSICAL_ENABLE": "Removable front panel; assumed apertures, no case wall cuts modelled",
        },
        "control_panel": {"centre_xy_mm": [103, -214], "panel_z_mm": [155, 191],
                          "PTT_aperture_diameter_mm": 12.4, "rocker_apertures_xz_mm": [20, 13],
                          "rocker_count": 2, "dimensions_confirmed": False,
                          "body_clearance": "17mm behind panel before tray region; actual switch bodies/terminals must be measured",
                          "wood_cutouts": "None; confirm rim height and operate as removable inner panel before any wood drilling"},
        "cable_paths": ["Powerbank short ends open; positiveY clearance15.5mm requires measured/right-angle USB plug",
                        "RoutePD20V vertically down left rear short end to bottom carrier; insulate and strain-relieve",
                        "5V/safety loom uses open shelf underside and side exits; keep clear of mechanism Y<=45",
                        "Microphone/control wires follow front rail outside tray Y>=-195, with strain relief",
                        "CameraFPC stays inside common pod; no long extension assumed"],
        "fastener_head_reservations": fastener_heads,
        "fastener_head_note": "6mm diameter / 3mm high clearance envelope for M3 pan heads and washers; actual hardware must be selected",
        "not_factory_mounts": ["PCA9685", "MAX98357A", "power/safety PCB", "ESP32-S3", "OV5640", "ICS43434", "speakers"],
        "hardware_omissions": ["second amplifier purchase", "straps", "M3 bolts/nuts/washers",
                               "PCB insulating spacers", "microphone isolation foam", "speaker gasket/isolation",
                               "measured USB plug bend", "selected safety PCB fit"],
        "confirmation": {"case_internal": p["case_dimensions_confirmed_internal"],
                         "electronics": p["electronics_dimensions_confirmed"],
                         "powerbank": "Published conservative envelope; received unit unmeasured",
                         "speakers": "Published body envelope only; flange/terminals unmeasured"},
        "acceptance_limits": ["CAD only; no print, load, thermal, acoustic or mounted hardware acceptance",
                              "Case floor fastening and lid/rim/wiring clearance require measurement"],
    }
    return parts, placements, purchased, metadata
