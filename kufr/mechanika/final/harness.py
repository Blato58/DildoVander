"""F2 assembly harness: preserved topology and parameter-based physical routes.

No CAD dependency. Coordinates are routing anchors, never measured connector pins.
Power fanouts are insulated copper wire joints, not PCB current-carrying tracks.
"""
from __future__ import annotations

import csv
import json
import math
import flex_routing
from copy import deepcopy
from pathlib import Path


ROOT = Path(__file__).resolve().parent
PARAMS = json.loads((ROOT / "parameters.json").read_text(encoding="utf-8"))
NETS = {"degson_1": "MAIN5", "degson_2": "DRIVE5", "degson_3": "GND"}
NET_ALIASES = {"PD_RETURN": "GND"}
TERMINAL_ALIASES = {"buck:IN-": "buck:GND", "buck:OUT-": "buck:GND"}
BUCK_GROUND_JOINT = {
    "physical_terminal": "buck:GND",
    "construction": "One 1.5mm2 lead in common GND clamp, then an insulated soldered fork to PD OUT- and Degson3 port1; no stacked wires in clamp.",
    "note": "Joy-IT three-terminal module has IN+, common GND, OUT+; IN-/OUT- labels are logical aliases, not two physical terminals.",
}
TERMINALS = {
    "degson_1": {1: "red_main:OUT", 2: "green_drives:IN", 3: "F_LED+",
                 4: "F_AUDIO+", 5: "F_PANEL+"},
    "degson_2": {1: "green_drives:OUT", 2: "pca9685:V+", 3: "F_U123+",
                 4: "F_U456+", 5: None},
    "degson_3": {1: "buck:OUT-", 2: "pca9685:power_GND", 3: "F_LED-",
                 4: "F_U-", 5: "F_AUDIO_PANEL-"},
}
FANOUTS = {
    "F_LED+": {"net": "MAIN5", "loads": ["pixel_shifter:5V"] + [f"led_{i}:+5V" for i in range(1, 7)]},
    "F_AUDIO+": {"net": "MAIN5", "loads": ["amplifier_left:VIN", "amplifier_right:VIN", "esp_usb_supply:5V"]},
    "F_PANEL+": {"net": "MAIN5", "loads": ["white_ptt:LED+"]},
    "F_U123+": {"net": "DRIVE5", "loads": [f"u059_{i}:red" for i in range(1, 4)]},
    "F_U456+": {"net": "DRIVE5", "loads": [f"u059_{i}:red" for i in range(4, 7)]},
    "F_LED-": {"net": "GND", "loads": ["pixel_shifter:GND"] + [f"led_{i}:GND" for i in range(1, 7)]},
    "F_U-": {"net": "GND", "loads": [f"u059_{i}:black" for i in range(1, 7)]},
    "F_AUDIO_PANEL-": {"net": "GND", "loads": ["amplifier_left:GND", "amplifier_right:GND", "esp_usb_supply:GND",
        "F_LOGIC-", "red_main:LED-GND", "green_drives:LED-GND", "white_ptt:LED-"]},
    "F_LOGIC-": {"net": "GND", "loads": ["mcp23017:GND", "microphone:GND"]},
    "F_LOGIC3": {"net": "LOGIC3", "loads": ["pca9685:VCC", "mcp23017:VIN", "microphone:3V3"]},
}
# Pin positions are deliberately provisional: board envelopes are known, header
# orientation is not measured. Every terminal has its own anchor and evidence.
M = PARAMS["mechanism"]
FRAME_TOP = PARAMS["frame"]["origin"][2] + PARAMS["frame"]["size"][2]
REST_TRAY_Z = M["origin_z"] + M["cam_z"] + M["cam_low_radius"] + M["follower_drop"]
CORRIDORS = {
    "power": {"y": M["origin_y"] + M["length"] - 9, "z": FRAME_TOP + 28},
    "signal": {"y": M["origin_y"] + M["length"] - 9, "z": FRAME_TOP + 24},
}
MOVING_LOOP_Y = M["origin_y"] + 58
RIGHT_FRONT_Y = PARAMS["components"]["degson_1"]["origin"][1] - 22
RIGHT_LOWER_X = M["origin_x"] + M["pitch"]*M["count"] - 1
ANCHORS = {name: tuple(row["origin"]) for name, row in PARAMS["components"].items()}
ANCHORS.update({name: tuple(point) for name, point in PARAMS["panel"].items()})
# Supply is a passive socket on the fixed rear carrier, never on the powerbank.
_esp = PARAMS["components"]["esp32"]
ANCHORS["esp_usb_supply"] = (_esp["origin"][0] - 17, _esp["origin"][1], _esp["origin"][2] + 13)
for name, offset in {
    "F_LED+": (10, 6, 18), "F_LED-": (10, 10, 18),
    "F_AUDIO+": (16, 6, 18), "F_AUDIO_PANEL-": (16, 10, 18),
    "F_PANEL+": (22, 6, 18), "F_U123+": (10, 6, 18),
    "F_U456+": (16, 6, 18), "F_U-": (10, 10, 18),
    "F_LOGIC-": (0, -5, -5), "F_LOGIC3": (5, -5, -5),
}.items():
    parent = "esp32" if name.startswith("F_LOGIC") else "degson_2" if name.startswith("F_U") else "degson_1"
    if name in ("F_LED-", "F_U-", "F_AUDIO_PANEL-"):
        parent = "degson_3"
    if not name.startswith("F_LOGIC"):
        row = PARAMS["components"][parent]
        joint_y = row["origin"][1] + {"F_LED+":3,"F_LED-":3,"F_AUDIO+":9,
            "F_AUDIO_PANEL-":9,"F_PANEL+":15,"F_U123+":3,"F_U456+":10,"F_U-":16}[name]
        ANCHORS[name] = (365, joint_y, row["origin"][2]+20)
    else:
        ANCHORS[name] = tuple(a+b for a,b in zip(ANCHORS[parent], offset))
for index in range(1, M["count"] + 1):
    x = M["origin_x"] + M["pitch"] * (index - 1)
    ANCHORS[f"servo_{index}"] = (x + 14, M["origin_y"] + 128.5, M["origin_z"] + 36)
    ANCHORS[f"u059_{index}"] = (x + 23, M["origin_y"] + 48, REST_TRAY_Z - 6)
    ANCHORS[f"led_{index}"] = (x + 46.5, M["origin_y"] + 6, REST_TRAY_Z - 2)
for index, point in enumerate(PARAMS["capacitors"], 1):
    ANCHORS[f"capacitor_{index}"] = tuple(point)

# Exact pin names match the existing electrical profile. Offsets express an
# assembly anchor on the corresponding envelope, not a measured header layout.
PIN_OFFSETS = {
    "esp32": {"3V3": (2, -2, 8), "GPIO1": (5, -2, 8), "GPIO14": (8, -2, 8),
              "GPIO2": (11, -2, 8), "GPIO48": (14, -2, 8), "GPIO21": (17, -2, 8),
              "GPIO41": (20, -2, 8), "GPIO42": (23, -2, 8), "GPIO47": (26, -2, 8),
              "USB-UART": (0, 6.5, 14), "OV5640": (28.5, 6.5, 28)},
    "powerbank": {"C1": (13.25, 0, 28)},
    "pd_trigger": {"USB-C": (7, 0, 5.5), "OUT+": (9, 28, 6), "OUT-": (12, 28, 6)},
    "buck": {"IN+": (5, -2, 8), "GND": (23, -2, 8), "OUT+": (41, -2, 8)},
    "breaker": {"IN": (6, -2, 6.5), "OUT": (39, -2, 6.5)},
    "pca9685": {"V+": (5, -2.5, 4), "power_GND": (9, -2.5, 4), "GND": (9, -2.5, 4),
                "VCC": (13, -2, 8), "SDA": (17, -2, 8), "SCL": (21, -2, 8), "OE": (25, -2, 8)},
    "mcp23017": {"VIN": (2, -2, 8), "GND": (5, -2, 8), "SDA": (8, -2, 8),
                 "SCL": (11, -2, 8), "GPA0": (14, -2, 8),
                 "A0": (30, -2, 8), "A1": (33, -2, 8), "A2": (36, -2, 8)},
    "pixel_shifter": {"5V": (3, 18, 4), "GND": (7, 18, 4), "IN": (11, 18, 4),
                      "OUT_330R": (19, 18, 4)},
    "microphone": {"3V3": (1, 2, 15.5), "GND": (3.5, 2, 15.5), "SCK": (6, 2, 15.5),
                   "WS": (8.5, 2, 15.5), "SD": (11, 2, 15.5), "L/R": (13.5, 2, 15.5)},
    "esp_usb_supply": {"5V": (0, 0, 0), "GND": (3, 0, 0), "USB-A": (6, 0, 0)},
    "camera": {"FPC": (12, 6, 0)},
}
for side in ("left", "right"):
    PIN_OFFSETS[f"amplifier_{side}"] = {pin: ((2+i*2.5,21.3,4) if side=="left" else (23,2+i*2,4)) for i,pin in
        enumerate(("VIN", "GND", "BCLK", "LRC", "DIN", "SPK+", "SPK-"))}
    PIN_OFFSETS[f"speaker_{side}"] = ({"SPK+":(40,100,6.5),"SPK-":(43,100,6.5)} if side=="left" else
                                     {"SPK+":(43,96,7),"SPK-":(43,99,7)})
for name in ("red_main", "green_drives"):
    PIN_OFFSETS[name] = {"IN": (-5, 0, 0), "OUT": (0, 0, 0), "LED-GND": (5, 0, 0)}
PIN_OFFSETS["white_ptt"] = {"CONTACT": (-4, 0, 0), "CONTACT_GND": (-1, 0, 0),
                            "LED+": (2, 0, 0), "LED-": (5, 0, 0)}
for block in NETS:
    PIN_OFFSETS[block] = {str(port): (3 + 7*(port-1), -2, 8) for port in range(1,6)}
for index in range(12):
    PIN_OFFSETS["pca9685"][f"CH{index}"] = (3+index*5, -2, 20)
for index in range(1,7):
    PIN_OFFSETS[f"servo_{index}"] = {"+": (-2, 0, 0), "-": (0, 0, 0), "S": (2, 0, 0)}
    PIN_OFFSETS[f"u059_{index}"] = {"red": (-2, 0, 0), "black": (0, 0, 0), "yellow": (2, 0, 0)}
    PIN_OFFSETS[f"led_{index}"] = {"+5V": (0, 0, -2), "GND": (0, 0, 0),
                                     "DIN": (0, 0, 2), "DOUT": (0, 200, 2)}
    PIN_OFFSETS[f"capacitor_{index}"] = {"+": (-2, 0, 0), "-": (2, 0, 0)}
PIN_OFFSETS["perfboard_1"] = {"SDA": (10, 3, 5), "SCL": (20, 3, 5)}
PIN_OFFSETS["perfboard_2"] = {"BCLK": (10, 3, 5), "WS": (20, 3, 5), "DIN": (30, 3, 5)}
for name in FANOUTS:
    PIN_OFFSETS[name] = {"joint": (0,0,0)}


def canonical_endpoint(endpoint):
    return endpoint + ":joint" if endpoint in FANOUTS else endpoint


def endpoint_anchor(endpoint):
    endpoint = TERMINAL_ALIASES.get(endpoint, canonical_endpoint(endpoint))
    component, pin = endpoint.split(":", 1)
    return tuple(round(a+b, 3) for a,b in zip(ANCHORS[component], PIN_OFFSETS[component][pin]))


def endpoint_registry():
    result = {}
    for component, pins in PIN_OFFSETS.items():
        for pin in pins:
            endpoint = f"{component}:{pin}"
            result[endpoint] = {"component": component, "pin": pin,
                "point": list(endpoint_anchor(endpoint)), "position_status": "provisional envelope-relative anchor; verify actual pin/plug direction",
                "physical_terminal": TERMINAL_ALIASES.get(endpoint, endpoint)}
    for alias, terminal in TERMINAL_ALIASES.items():
        result[alias] = dict(result[terminal], pin=alias.split(":",1)[1], physical_terminal=terminal)
    return result


USB_PARK_POINT = (24.6,103,FRAME_TOP+31)
USB_COIL = {**deepcopy(PARAMS["usb_coil"]), "bend_radius_mm": PARAMS["usb_coil"]["minimum_bend_radius"],
            "status": "provisional vertical YZ coil beside bank; three continuous spiral turns OD<=4.5mm; independently strapped, dry-fit required"}
SUPPLY_CONNECTOR = {"id": "esp_usb_supply", "type": "passive USB-A female breakout or pigtail",
    "quantity": 1, "rating": "at least 3A / 5V", "procurement": "ADD; not confirmed in stock",
    "supply": "regulated MAIN5 only via F_AUDIO+-03 and F_AUDIO_PANEL--03",
    "termination": "VBUS +5V, GND only; D+/D- unconnected and insulated; mount on fixed rear carrier",
    "backfeed": "disconnect USB-A before attaching a PC to USB-UART until protection is measured"}
CAMERA_FPC = {"id": "CAMERA-FPC", "from": "esp32:OV5640", "to": "camera:FPC",
    "net": "CAMERA", "bundle": "CAMERA-FPC", "points": [list(endpoint_anchor("esp32:OV5640")),
        [endpoint_anchor("esp32:OV5640")[0], endpoint_anchor("esp32:OV5640")[1], endpoint_anchor("camera:FPC")[2]],
        list(endpoint_anchor("camera:FPC"))],
    "status": "logical adjacency only; delivered camera connector, FPC type/length/bend limits unconfirmed",
    "gpio": "4 SIOD; 5 SIOC; 6 VSYNC; 7 HREF; 8 Y4; 9 Y3; 10 Y5; 11 Y2; 12 Y6; 13 PCLK; 15 XCLK; 16 Y9; 17 Y8; 18 Y7"}

PASSIVES = {
    "OE": "two purchased 22k resistors in parallel from PCA OE to 3V3, local insulated splice",
    "U059": "one purchased 100k from each yellow DIN to black GND, at module side of native cable",
    "PTT": "one purchased 15k from MCP GPA0 to 3V3, local insulated splice",
    "RGB": "one purchased 330R from Pixel Shifter OUT to first LED DIN, local insulated splice",
    "caps": "470uF pairs 1/2 MAIN5 LED, 3/4 DRIVE5 PCA, 5/6 MAIN5 audio; six total; verify physical polarity before energizing",
    "logic": "MCP A0/A1/A2 to GND; microphone L/R to GND (left slot); I2C pull-ups only to 3V3",
}

SERVICE_MM = 40
TERMINATION_MM = 20
LIFT_MM = M["lift"]
MOTION_MM = 2 * LIFT_MM + 32
MISSING_ACCEPTANCE = [
    "ADD one passive USB-A female adapter/breakout or pigtail >=3A; verify polarity/backfeed before connection.",
    "Dry-fit actual plug directions, stock cable quantities, insulation, finite conductor envelopes, bends and moving loops.",
    "Measure voltage, current, capacitor discharge, OE/reset states and individual actuator calibration.",
    "Camera FPC connector/type/length remains pending delivered variant; do not substitute loose GPIO wires.",
]


def _component(endpoint):
    return endpoint.split(":", 1)[0]


def _moving_end_count(start, end):
    return sum(_component(endpoint).startswith(("led_", "u059_"))
               for endpoint in (start, end))


def loop_reserve(index):
    return flex_routing.loop_reservation(index, M['origin_x'], M['pitch'])


def _endpoint_path(endpoint, corridor):
    """Terminal -> independent flex loop if needed -> frame routing corridor."""
    part = _component(endpoint)
    a = endpoint_anchor(endpoint)
    lane = CORRIDORS["signal" if corridor == "signal" else "power"]
    y,z = lane["y"],lane["z"]
    left_x = M["origin_x"]-.8
    left_back = M["origin_x"]-1.5
    def left_lane(prefix):
        last=prefix[-1]
        return prefix+[(left_x,last[1],z),(left_x,122,z),
                       (left_back,122,z),(left_back,y,z)]
    if part in ("buck","breaker"):
        # The finite 12V input cable clears the adjacent main-switch barrel.
        escape_y=61 if endpoint=="buck:IN+" else 60
        return left_lane([a,(a[0],escape_y,a[2]),(left_x,escape_y,a[2])])
    if part=="pd_trigger" and endpoint.endswith(("OUT+","OUT-")):
        return [a,(a[0],a[1]+2,a[2]),(left_back,a[1]+2,a[2]),(left_back,a[1]+2,z),(left_back,y,z)]
    if part.startswith("degson_"):
        exterior_x = 365
        # Rise in each block's own front terminal clearance before passing
        # the other Degson blocks and the third tier's clamp tabs.
        return [a,(a[0],a[1],96),(exterior_x,a[1],96),(exterior_x,RIGHT_FRONT_Y,96),
                (exterior_x,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part.startswith("F_") and not part.startswith("F_LOGIC"):
        return [a,(a[0],RIGHT_FRONT_Y,a[2]),(a[0],RIGHT_FRONT_Y,z),
                (RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part.startswith("perfboard_"):
        portal_y=PARAMS["components"][part]["origin"][1]-4
        travel_z=34 if part=="perfboard_1" else 58
        return [a,(a[0],portal_y,a[2]),(a[0],portal_y,travel_z),
                (a[0],RIGHT_FRONT_Y,travel_z),(365,RIGHT_FRONT_Y,travel_z),
                (365,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part in ("red_main","green_drives","white_ptt"):
        escape_z = a[2]-5
        # Go inboard of the controls' continuous right leg before crossing it.
        return left_lane([a,(a[0],a[1],escape_z),(63,a[1],escape_z),
                          (63,60,escape_z),(left_x,60,escape_z)])
    if part.startswith("amplifier_"):
        exit_x = M["origin_x"]+1 if part.endswith("left") else 411
        if part.endswith("left"):
            return left_lane([a,(left_x,a[1],a[2])])
        return [a,(exit_x,a[1],a[2]),(exit_x,RIGHT_FRONT_Y,a[2]),(exit_x,RIGHT_FRONT_Y,z),
                (RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part.startswith("speaker_"):
        exit_y = PARAMS["components"][part]["origin"][1] + PARAMS["components"][part]["size"][1]+(2 if part.endswith("left") else 3)
        exit_x = M["origin_x"]+1 if part.endswith("left") else 410
        if part.endswith("left"):
            return left_lane([a,(a[0],exit_y,a[2]),(left_x,exit_y,a[2])])
        return [a,(a[0],exit_y,a[2]),(exit_x,exit_y,a[2]),(exit_x,RIGHT_FRONT_Y,a[2]),
                (exit_x,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part.startswith("capacitor_"):
        if part=='capacitor_1':
            # Separate rear rows clear the cassette clamp head and the fixed
            # C2 front exits. Corresponding short ducts preserve the PCB floor.
            rear_y=110.2 if endpoint.endswith(':+') else 112.9
            escape_x=M['origin_x']-2.6
            return left_lane([a,(a[0],a[1],a[2]-1.5),
                              (a[0],rear_y,a[2]-1.5),(escape_x,rear_y,a[2]-1.5),
                              (escape_x,rear_y,z)])
        prefix=[a,(a[0],a[1],a[2]-1.5),(a[0],a[1]-7,a[2]-1.5)]
        if a[0]<M["origin_x"]:
            # Lower the lead outside the new bed beam before entering its
            # packed lane. The original capacitor lead bores stay unchanged.
            escape_x=M["origin_x"]-2.6
            return left_lane(prefix+[(a[0],95.5,a[2]-1.5),(escape_x,95.5,a[2]-1.5),
                                     (escape_x,95.5,z),(left_x,95.5,z)])
        index=int(part.split("_")[-1])
        module=int((a[0]-M["origin_x"])/M["pitch"])
        escape_x=M["origin_x"]+module*M["pitch"]+46.65
        return prefix+[(a[0],a[1]-8.5,a[2]-1.5),(a[0],a[1]-8.5,44),
                       (escape_x,a[1]-8.5,44),(escape_x,y,44),(escape_x,y,z)]
    if part.startswith(("u059_", "led_")):
        index = int(part.split("_")[-1])
        x = M["origin_x"] + M["pitch"]*(index-1)
        loop_x,loop_y = x+29, MOVING_LOOP_Y
        exit_y = a[1]+5 if part.startswith("u059_") or endpoint.endswith(":DOUT") else a[1]-5
        if endpoint.endswith(":DOUT"):
            exit_y=a[1]+3
            # Stay outside the rear beam in X while dropping below it. Only
            # then turn forward and enter the clear front flex-loop reserve.
            return [a,(a[0],exit_y,a[2]),(a[0],exit_y,FRAME_TOP+39),
                    (a[0],loop_y,FRAME_TOP+39),(loop_x,loop_y,FRAME_TOP+39),
                    (loop_x,loop_y,FRAME_TOP+33),
                    (x+46.65,loop_y,FRAME_TOP+33),(x+46.65,y,FRAME_TOP+33),(x+46.65,y,z)]
        return [a,(a[0],exit_y,a[2]), (a[0],exit_y,FRAME_TOP+47), (a[0],loop_y,FRAME_TOP+47),
                (loop_x,loop_y,FRAME_TOP+47), (loop_x,loop_y,FRAME_TOP+33),
                (x+46.65,loop_y,FRAME_TOP+33), (x+46.65,y,FRAME_TOP+33), (x+46.65,y,z)]
    if part.startswith("servo_"):
        x = M["origin_x"]+M["pitch"]*(int(part.split("_")[-1])-1)
        return [a,(a[0],M["origin_y"]+147,a[2]),(x+46.65,M["origin_y"]+147,a[2]),
                (x+46.65,M["origin_y"]+147,44),(x+46.65,y,44),(x+46.65,y,z)]
    # Rear boards approach from the back side of the modules. The only long
    # front/rear crossings run in the right fixed service corridor.
    rear_y = y
    right_x = M["origin_x"] + M["pitch"]*M["count"] + 2
    if part in ("esp32", "esp_usb_supply", "pca9685", "mcp23017", "F_LOGIC-", "F_LOGIC3"):
        escape_y = a[1] if part=="esp_usb_supply" else PARAMS["components"][part]["origin"][1]-3.5 if part in PARAMS["components"] else a[1]-3.5
        points = [a,(a[0],escape_y,a[2]),(a[0],escape_y,z),(a[0],rear_y,z)]
        return points + [(points[-1][0],y,z)]
    if a[0] < M["origin_x"]:
        # Exit shelves alongside their right edge without crossing bank volume.
        return left_lane([a,(M["origin_x"]-.8,a[1],a[2])])
    if part == "microphone":
        front_y=M["origin_y"]-5.5
        right_x=PARAMS["frame"]["origin"][0]+PARAMS["frame"]["size"][0]-4.9
        escape_x=PARAMS["components"]["microphone"]["origin"][0]-3
        return [a,(escape_x,a[1],a[2]),(escape_x,a[1],56),(escape_x,front_y,56),
                (right_x,front_y,56),(right_x,RIGHT_FRONT_Y,56),(right_x,RIGHT_FRONT_Y,z),
                (RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    if part=="pixel_shifter":
        # The rear pin escapes behind its ties, rises between the first two
        # tiers, then joins the shared front approach above the bought boards.
        return [a,(a[0],158,a[2]),(365,158,a[2]),(365,158,34),(365,RIGHT_FRONT_Y,34),
                (365,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,RIGHT_FRONT_Y,z),(RIGHT_LOWER_X,y,z)]
    return [a, (a[0],a[1],z), (a[0],y,z)]


def _helical_coil_turns():
    """Continuous three-turn spiral with separate finite cable cross-sections.

    X advances 4.2mm and the rounded YZ loop grows 3.25mm per revolution.
    Together these provide over5.1mm turn separation inside the existing coil.
    """
    x0=USB_COIL["origin"][0]+2.65
    cy=USB_COIL["origin"][1]+40
    cz=USB_COIL["origin"][2]+32.5
    radius=20.1
    half_straight=1.5
    base=[(cy-half_straight-radius,cz,-1,0)]
    def arc(y,z,first,last):
        for angle in range(first+10,last+1,10):
            radians=math.radians(angle)
            ny,nz=math.cos(radians),math.sin(radians)
            base.append((y+radius*ny,z+radius*nz,ny,nz))
    arc(cy-half_straight,cz,180,270)
    base.append((cy+half_straight,cz-radius,0,-1))
    arc(cy+half_straight,cz,270,360)
    arc(cy+half_straight,cz,0,90)
    base.append((cy-half_straight,cz+radius,0,1))
    arc(cy-half_straight,cz,90,180)
    cumulative=[0]
    for a,b in zip(base,base[1:]):
        cumulative.append(cumulative[-1]+math.dist(a[:2],b[:2]))
    perimeter=cumulative[-1]
    result=[]
    for turn in range(3):
        for index,(y,z,ny,nz) in enumerate(base):
            if turn and index==0:
                continue
            phase=turn+cumulative[index]/perimeter
            growth=3.25*phase
            result.append([round(x0+4.2*phase,5),round(y+ny*growth,5),round(z+nz*growth,5)])
    return result


def _route(start, end, corridor):
    # Local circuits retain their own pins rather than collapsing to centres.
    if start == "powerbank:C1":
        a,b = endpoint_anchor(start),endpoint_anchor(end)
        x,y,z = USB_COIL["origin"]
        # Three continuous rounded spiral turns grow radially while X advances.
        # Separate entry/exit tails and service/end allowance fit the 1m cable.
        side_x=M["origin_x"]-2.4
        coil_side_x=USB_COIL["origin"][0]+USB_COIL["size"][0]+3.35
        coil=_helical_coil_turns()
        entry,exit=coil[0],coil[-1]
        tail=flex_routing.usb_bank_tail(a,side_x,y)
        points=tail['points']+[(coil_side_x,y,42),(coil_side_x,y+40,42),(entry[0],y+40,42),
                (entry[0],y+40,entry[2])] + coil
        points += [(exit[0],y-2,exit[2]),(side_x,y-2,exit[2]),(side_x,y-2,86),
                   (side_x,b[1]-1.85,86),(side_x,b[1]-1.85,b[2]),
                   (side_x,b[1]-18,b[2]),(b[0],b[1]-18,b[2]),b]
        return [list(point) for point in points]
    elif start == "esp_usb_supply:USB-A":
        a,b = endpoint_anchor(start),endpoint_anchor(end)
        points = [a,(b[0]-20,a[1],a[2]),(b[0]-20,a[1],b[2]),
                  (b[0]-20,b[1],b[2]),b]
    elif corridor == "local":
        a,b = endpoint_anchor(start),endpoint_anchor(end)
        if _component(start) == _component(end):
            offset=1 if _component(start)=="microphone" else 3
            points = [a,(a[0],a[1]-offset,a[2]),(b[0],a[1]-offset,a[2]),(b[0],a[1]-offset,b[2]),b]
        else:
            points = _endpoint_path(start,"power") + list(reversed(_endpoint_path(end,"power")))
    else:
        points = _endpoint_path(start,corridor) + list(reversed(_endpoint_path(end,corridor)))
    # Join corridor tails orthogonally even if their Z values differ.
    clean = []
    for point in points:
        if clean:
            previous = clean[-1]
            for axis in range(3):
                if previous[axis] != point[axis]:
                    next_point = list(previous)
                    next_point[axis] = point[axis]
                    clean.append(tuple(next_point))
                    previous = clean[-1]
        else:
            clean.append(tuple(point))
    if start != "powerbank:C1":
        # Shared approach paths can contain an exact retrace. Remove that
        # retrace rather than carrying extra metres around the same corridor.
        unique=[]
        for point in clean:
            if point in unique:
                unique=unique[:unique.index(point)+1]
            else:
                unique.append(point)
        clean=unique
    return [list(point) for point in clean]


def route_length(points):
    """Physical chord length; sampled coil bends are included, no hidden slack."""
    return sum(math.dist(start,end) for start,end in zip(points,points[1:]))


def _wire(identifier, start, end, net, area=0.5, corridor="power", native=0,
          moving=False, channel=None, stock=None, note="", construction="solder + heatshrink"):
    points = _route(start, end, corridor)
    moving_count = _moving_end_count(start, end)
    moving = moving_count > 0
    motion_mm = MOTION_MM * moving_count
    required = math.ceil((route_length(points) + SERVICE_MM + TERMINATION_MM + motion_mm) / 10) * 10
    signal_conductor = net not in ("GND","PD_RETURN","MAIN5","DRIVE5","LOGIC3","PD12","BUCK5","USB5") and not net.startswith("SPK_")
    if signal_conductor:
        area = 0.14
    diameter = 1.2 if signal_conductor else 2.7 if area == 1.5 else 2.1 if area == 0.5 else 4.5 if identifier.startswith("USB") else 2.1
    color = "black" if net in ("GND", "PD_RETURN") else "red" if net in ("MAIN5", "DRIVE5", "PD12", "BUCK5", "USB5") else "blue labelled at both ends"
    if ":yellow" in end:
        color = "yellow native; blue extension labelled DIN"
    if start.startswith("pca9685:") and end.startswith("servo_"):
        color = {"+": "red native", "-": "brown/black native", "S": "orange native"}[end.split(":")[1]]
    moving_endpoints = [{"endpoint": canonical_endpoint(endpoint),
                         "component": _component(endpoint),
                         "loop": loop_reserve(int(_component(endpoint).split("_")[-1])),
                         "fixed_point": [M["origin_x"] + M["pitch"]*(int(_component(endpoint).split("_")[-1])-1) + 29,
                                         MOVING_LOOP_Y, FRAME_TOP+33],
                         "moving_point": list(endpoint_anchor(endpoint)),
                         "axis": [0,0,1], "lift_mm": LIFT_MM}
                        for endpoint in (start,end) if _component(endpoint).startswith(("led_", "u059_"))]
    bundle = "USB-PD" if identifier == "USB-PD" else "USB-ESP" if identifier == "USB-ESP" else "PD12" if net in ("PD12", "PD_RETURN") else "I2S" if net in ("WS", "BCLK", "DIN", "MIC_SD") else "I2C" if net in ("SDA", "SCL") else f"TRAY-{int(_component(end).split('_')[-1])}" if moving_endpoints else "SERVO" if end.startswith("servo_") else net
    return {
        "id": identifier, "from": start, "to": end, "net": net,
        "kind": "ground" if net in ("GND", "PD_RETURN") else "power" if net in ("MAIN5", "DRIVE5", "LOGIC3", "PD12", "BUCK5", "USB5") else "signal",
        "area_mm2": area, "points": points, "route_mm": round(route_length(points), 1),
        "bundle": bundle, "colour": color, "outer_diameter_mm": diameter,
        "diameter_status": "CYH0.5 catalog; signal/Dupont OD1.2 design assumption; 1.5/USB/native provisional; dry-fit gate",
        "spec": "intact stocked USB cable" if identifier.startswith("USB") else f"flexible copper {area or 0.5}mm2; native terminal ends retained",
        "from_termination": terminal_termination(start), "to_termination": terminal_termination(end),
        "moving_endpoints": moving_endpoints,
        "transport_points": _transport_pd(points) if identifier == "USB-PD" else None,
        "transport_from": "usb_park_clip:unplugged_C" if identifier == "USB-PD" else None,
        "transport_connection": "disconnected; powerbank removed; insulated USB plug clipped" if identifier == "USB-PD" else "unchanged fixed wiring",
        "coil_reserve": USB_COIL if identifier == "USB-PD" else None,
        "bank_tail": flex_routing.usb_bank_tail(endpoint_anchor(start),M['origin_x']-2.4,USB_COIL['origin'][1]) if identifier=='USB-PD' else None,
        "curve_sampling_degrees": 10 if identifier == "USB-PD" else None,
        "coil_turns": 3 if identifier == "USB-PD" else None,
        "coil_pitch_x_mm": 4.2 if identifier == "USB-PD" else None,
        "coil_radial_growth_mm_per_turn": 3.25 if identifier == "USB-PD" else None,
        "excess_stock_mm": max(0, (stock or native) - required),
        "bend_radius_mm": 20 if identifier=='USB-PD' else PARAMS["clearances"]["wire_min_bend_radius"],
        "bend_status": 'exact nominal R20 bank exit; coil and PD/park approaches require physical bend/connector dry fit' if identifier=='USB-PD' else "polyline corners are routing anchors; fillet and dry-fit required, not a proven cable curve",
        "service_mm": SERVICE_MM, "termination_mm": TERMINATION_MM,
        "motion_mm": motion_mm, "lift_mm": LIFT_MM if moving else 0,
        "native_lead_mm": native, "stock_mm": stock,
        "estimated_total_mm": required, "estimated_cut_mm": max(0, required - (stock or native)),
        "channel": channel, "label_from": f"{identifier} A / {net} / {start}",
        "label_to": f"{identifier} B / {net} / {end}", "construction": construction,
        "note": note, "status": "estimate; cut after dry-fit",
        "fixed_anchor": "fixed base tie outside cam/guide sweep" if moving else None,
        "moving_anchor": "tray tie before module; solder joint outside flexing span" if moving else None,
    }


def terminal_termination(endpoint):
    component = _component(endpoint)
    if endpoint in FANOUTS:
        return "insulated copper solder fanout; stagger joints and add fixed tie"
    if component.startswith("degson_"):
        return "one 1.5mm2 conductor in labelled lever port; verify strip length on delivered block"
    if component.startswith("servo_"):
        return "native 3-pin servo plug retained; extension splice outside moving span"
    if component.startswith("u059_"):
        return "native Grove plug retained; inline splice outside flexing span; white NC separately insulated"
    if component.startswith("led_"):
        return "solder to labelled strip pad; seal IP65 cut; strain relief before moving span"
    if component.startswith("speaker_") or ":SPK" in endpoint:
        return "existing PH2 speaker pair; polarity confirmed against terminal label"
    if endpoint.endswith((":USB-A", ":USB-C", ":USB-UART", ":C1")):
        return "intact purchased USB plug into labelled socket; no stripped USB conductors"
    if component in ("buck", "breaker", "red_main", "green_drives", "white_ptt"):
        return "labelled physical terminal; matching solder/clamp termination after delivered terminal inspection"
    return "existing signal header/Dupont or local insulated solder joint; no chained Dupont power trunk"


def _transport_pd(points):
    # Exit the parking clip before bending into the tier-one USB tunnel.
    # The common tunnel, coil and PD end retain their installed route.
    park = list(USB_PARK_POINT)
    side_x=M['origin_x']-2.4
    side_join=[side_x,USB_COIL['origin'][1],42]
    join_index=points.index(side_join)
    tunnel_x=endpoint_anchor('powerbank:C1')[0]
    return [park,[tunnel_x,park[1],park[2]],[tunnel_x,park[1],42],
            [tunnel_x,97,42],[side_x,97,42]]+deepcopy(points[join_index:])


def _make_harnesses():
    wires = [
        _wire("USB-PD", "powerbank:C1", "pd_trigger:USB-C", "PD12", None, "local", stock=1000,
              construction="intact purchased USB-C/USB-C cable; do not cut"),
        _wire("P12+", "pd_trigger:OUT+", "buck:IN+", "PD12", 1.5, "local"),
        _wire("P12-", "pd_trigger:OUT-", "buck:IN-", "PD_RETURN", 1.5, "local"),
        _wire("P5-FUSE", "buck:OUT+", "breaker:IN", "BUCK5", 1.5, "local"),
        _wire("P5-MAIN", "breaker:OUT", "red_main:IN", "BUCK5", 1.5, "local"),
        _wire("USB-ESP", "esp_usb_supply:USB-A", "esp32:USB-UART", "USB5", None, "local", stock=300,
              construction="ADD intact USB-A/USB-C 300mm >=3A cable; existing 1m A/C stays outside active assembly",
              note="ADD passive USB-A female supply socket; MAIN5/GND only; verify polarity/backfeed."),
    ]
    for block, ports in TERMINALS.items():
        for port, endpoint in ports.items():
            if endpoint is None:
                continue
            wires.append(_wire(f"D{block[-1]}-{port}", f"{block}:{port}", endpoint, NETS[block], 1.5))
    for fanout, definition in FANOUTS.items():
        for index, load in enumerate(definition["loads"], 1):
            moving = _component(load).startswith(("led_", "u059_"))
            native = 200 if _component(load).startswith("u059_") else 0
            wires.append(_wire(f"{fanout}-{index:02}", fanout, load, definition["net"],
                               native=native, moving=moving,
                               corridor="signal" if fanout.startswith("F_LOGIC") else "power",
                               note="Grove plug at module retained; solder extension to native lead." if native else ""))
    wires.append(_wire("L3-FEED", "esp32:3V3", "F_LOGIC3", "LOGIC3", corridor="signal"))
    for index, plus, minus, net in ((1, "F_LED+", "F_LED-", "MAIN5"),
                                   (2, "F_LED+", "F_LED-", "MAIN5"),
                                   (3, "pca9685:V+", "pca9685:power_GND", "DRIVE5"),
                                   (4, "pca9685:V+", "pca9685:power_GND", "DRIVE5"),
                                   (5, "F_AUDIO+", "F_AUDIO_PANEL-", "MAIN5"),
                                   (6, "F_AUDIO+", "F_AUDIO_PANEL-", "MAIN5")):
        corridor = "power" if index < 3 else "local"
        wires.append(_wire(f"C{index}+", plus, f"capacitor_{index}:+", net, corridor=corridor))
        wires.append(_wire(f"C{index}-", minus, f"capacitor_{index}:-", "GND", corridor=corridor))
    for terminal in ("A0", "A1", "A2"):
        wires.append(_wire(f"MCP-{terminal}", "mcp23017:GND", f"mcp23017:{terminal}", "GND", corridor="local"))
    wires.append(_wire("MIC-LR", "microphone:GND", "microphone:L/R", "GND", corridor="local"))
    # PCA logic and servo grounds share the PCA power ground fed by D3-2.
    for index in range(1, 7):
        for suffix, terminal, net in (("+", "V+", "DRIVE5"), ("-", "GND", "GND"), ("S", f"CH{index-1}", f"SERVO{index}")):
            wires.append(_wire(f"S{index}-{suffix}", f"pca9685:{terminal}", f"servo_{index}:{suffix}", net,
                               corridor="signal", native=150, channel=index-1 if suffix == "S" else None,
                               construction="retain native 3-pin servo plug; insulated inline extension only if required"))
        wires.append(_wire(f"U{index}-DIN", f"pca9685:CH{index+5}", f"u059_{index}:yellow", f"U059DIN{index}",
                           corridor="signal", native=200, moving=True, channel=index+5,
                           note="100k pull-down between DIN and module GND at local insulated junction; white NC insulated alone."))
    # Shared low-current signal junctions on bought perfboards; no power trunk tracks.
    shared = [("SDA", "GPIO1", "perfboard_1", [("pca9685", "SDA"), ("mcp23017", "SDA")]),
              ("SCL", "GPIO14", "perfboard_1", [("pca9685", "SCL"), ("mcp23017", "SCL")]),
              ("BCLK", "GPIO21", "perfboard_2", [("microphone", "SCK"), ("amplifier_left", "BCLK"), ("amplifier_right", "BCLK")]),
              ("WS", "GPIO41", "perfboard_2", [("microphone", "WS"), ("amplifier_left", "LRC"), ("amplifier_right", "LRC")]),
              ("DIN", "GPIO47", "perfboard_2", [("amplifier_left", "DIN"), ("amplifier_right", "DIN")])]
    for name, gpio, board, loads in shared:
        wires.append(_wire(f"SIG-{name}-IN", f"esp32:{gpio}", f"{board}:{name}", name,
                           corridor="signal", construction="soldered signal junction + retained Dupont ends"))
        for module, terminal in loads:
            wires.append(_wire(f"SIG-{name}-{module}", f"{board}:{name}", f"{module}:{terminal}", name,
                               corridor="signal", construction="soldered signal junction + retained Dupont end"))
    for identifier, start, end, net in (
        ("SIG-OE", "esp32:GPIO2", "pca9685:OE", "OE"),
        ("SIG-RGB", "esp32:GPIO48", "pixel_shifter:IN", "RGB3"),
        ("SIG-MIC", "microphone:SD", "esp32:GPIO42", "MIC_SD"),
        ("SIG-PTT", "mcp23017:GPA0", "white_ptt:CONTACT", "PTT"),
        ("PTT-GND", "mcp23017:GND", "white_ptt:CONTACT_GND", "GND"),
    ):
        wires.append(_wire(identifier, start, end, net, corridor="signal",
                           construction="native Dupont ends + soldered 0.5mm2 extension; no chained headers"))
    for index in range(1, 7):
        start = "pixel_shifter:OUT_330R" if index == 1 else f"led_{index-1}:DOUT"
        wires.append(_wire(f"RGB-{index}", start, f"led_{index}:DIN", f"RGB{index}",
                           corridor="signal", moving=True,
                           note="Each end strain-relieved; adjacent trays move independently. No purchased JST-SM connectors."))
    for side in ("left", "right"):
        for polarity in ("+", "-"):
            wires.append(_wire(f"SPK-{side}-{polarity}", f"amplifier_{side}:SPK{polarity}",
                               f"speaker_{side}:SPK{polarity}", f"SPK_{side}_{polarity}",
                               area=None, corridor="local", native=100,
                               construction="purchased PH2 pair; insulated 0.5mm2 extension if required",
                               note="SPK- is not GND; do not connect either speaker output to another amplifier."))
    return tuple(wires)


def _orthogonal(points):
    result=[]
    for point in points:
        point=list(point)
        if not result:
            result.append(point)
            continue
        previous=result[-1]
        for axis in range(3):
            if previous[axis]!=point[axis]:
                previous=previous.copy()
                previous[axis]=point[axis]
                result.append(previous)
    return result


def _remove_packing_return_jogs(wire, points):
    """Shorten packing transfers that return into their own finite conductor.

    Only orthogonal near-return pairs are joined. Pin endpoints and the fixed
    flex ties are preserved; subsequent BREP auditing checks every new join.
    """
    protected=[row["fixed_point"] for row in wire["moving_endpoints"]]
    separation=wire["outer_diameter_mm"]+.6
    for _ in range(20):
        cumulative=[0]
        for a,b in zip(points,points[1:]):
            cumulative.append(cumulative[-1]+math.dist(a,b))
        replacement=None
        for i,(a,b) in enumerate(zip(points,points[1:])):
            for j in range(i+2,len(points)-1):
                if cumulative[j]-cumulative[i+1]<2*separation:
                    continue
                if any(point in protected for point in points[i+1:j+1]):
                    continue
                c,d=points[j:j+2]
                first,second=[],[]
                for axis in range(3):
                    low1,high1=sorted((a[axis],b[axis]))
                    low2,high2=sorted((c[axis],d[axis]))
                    if high1<low2:
                        p,q=high1,low2
                    elif high2<low1:
                        p,q=low1,high2
                    else:
                        p=q=(max(low1,low2)+min(high1,high2))/2
                    first.append(p)
                    second.append(q)
                if math.dist(first,second)+1e-5<separation:
                    replacement=points[:i+1]+_orthogonal([first,second])+points[j+1:]
                    break
            if replacement is not None:
                break
        if replacement is None:
            return points
        points=[point for index,point in enumerate(replacement) if not index or point!=replacement[index-1]]
    raise ValueError(f"Unresolved packing return jog: {wire['id']}")


def _pack_corridors(wires):
    """Give each overlapping rear conductor a physical cross-section slot.

    Only the declared rear cable windows and fixed left approach are packed.
    Pin escapes and the six independent movement loops remain unchanged.
    BREP verification still decides whether these candidate routes fit.
    """
    # Each tray's seven physical conductors occupy separate vertical slots,
    # including their fixed ties. Servo leads use the lower part of that lane.
    for module in range(1,M["count"]+1):
        x=M["origin_x"]+(module-1)*M["pitch"]
        members=[(wire,row) for wire in wires for row in wire["moving_endpoints"]
                 if int(row["component"].split("_")[-1])==module]
        level=FRAME_TOP+24.3
        previous=0
        for wire,row in sorted(members,key=lambda pair:(-pair[0]["outer_diameter_mm"],pair[0]["id"])):
            diameter=wire["outer_diameter_mm"]
            level+=(previous+diameter)/2+.1
            previous=diameter
            for point in wire["points"]:
                if point[2]==FRAME_TOP+33 and x+28.9<=point[0]<=x+46.8 and point[1]>=MOVING_LOOP_Y:
                    point[2]=round(level,3)
            row["fixed_point"][2]=round(level,3)
            wire.setdefault("local_corridor_slots",[]).append({"module":module,"z":round(level,3),
                       "physical_diameter_mm":diameter,"part":"moving branch"})
        level=FRAME_TOP+18.3
        previous=0
        servos=[wire for wire in wires if wire["to"].startswith(f"servo_{module}:")]
        for wire in sorted(servos,key=lambda row:(-row["outer_diameter_mm"],row["id"])):
            diameter=wire["outer_diameter_mm"]
            level+=(previous+diameter)/2+.1
            previous=diameter
            for point in wire["points"]:
                if point[0]==x+46.65 and point[2]==44:
                    point[2]=round(level,3)
            wire.setdefault("local_corridor_slots",[]).append({"module":module,"z":round(level,3),
                        "physical_diameter_mm":diameter,"part":"fixed servo branch"})
    rear_y=M["origin_y"]+M["length"]-9
    left_front=M["origin_x"]-.8
    left_back=M["origin_x"]-1.5
    left_wires=[wire for wire in wires if not wire["id"].startswith("USB") and
                any(point[0] in (left_front,left_back) for point in wire["points"])]
    level=23
    previous_diameter=0
    for wire in sorted(left_wires,key=lambda row:(-row["outer_diameter_mm"],row["id"])):
        diameter=wire["outer_diameter_mm"]
        level+=(previous_diameter+diameter)/2+.15
        # The first bed's through-shaft sweeps Z59..78 at the left approach.
        # Keep the lower bank below it and put remaining conductors above it.
        if level+diameter/2+.3>59 and level<80:
            level=80+diameter/2
        previous_diameter=diameter
        wire["left_corridor_z"]=round(level,3)
        for point in wire["points"]:
            if point[0] in (left_front,left_back) and point[2] in (36,40):
                point[2]=round(level,3)
    if level+previous_diameter/2>106:
        raise ValueError("Left bundle does not fit its available height")
    candidates=[]
    for wire in wires:
        spans=[(min(a[0],b[0]),max(a[0],b[0])) for a,b in zip(wire["points"],wire["points"][1:])
               if a[1]==b[1]==rear_y and abs(a[0]-b[0])>20]
        if spans:
            candidates.append((wire,min(a for a,b in spans)-7,max(b for a,b in spans)+7))
    # The reinforced rear bed beam starts at Z52. Pack the entire transverse
    # field below it, including the finite insulation and clearance envelope.
    slots=[(round(M["origin_y"]+198.8+column*.4,3),round(22.1+row*.4,3))
           for row in range(71) for column in range(32)]
    allocated=[]
    for wire,minimum,maximum in sorted(candidates,key=lambda row:(-row[0]["outer_diameter_mm"],-(row[2]-row[1]),row[0]["id"])):
        diameter=wire["outer_diameter_mm"]
        selected=None
        for y,z in slots:
            if any(_component(endpoint) in ("amplifier_right","speaker_right","microphone","pixel_shifter")
                   for endpoint in (wire["from"],wire["to"])) and z<34:
                continue
            if z-wire["outer_diameter_mm"]/2-.3<24 and y+wire["outer_diameter_mm"]/2+.3>233.5:
                continue
            # Rear guide sleeves end at Y(origin+199); LED strips at Y+206.
            if z>66 and y<M["origin_y"]+208:
                continue
            if diameter>2.1 and ((z>51 and y<M["origin_y"]+202) or (z<45 and y>M["origin_y"]+210)):
                continue
            if all(max(minimum,other_min)>=min(maximum,other_max) or
                   math.hypot(y-other_y,z-other_z)>=(diameter+other_diameter)/2+.1
                   for other_min,other_max,other_y,other_z,other_diameter in allocated):
                selected=(y,z)
                break
        if selected is None:
            raise ValueError(f"Rear cable windows have no physical slot for {wire['id']}")
        new_y,new_z=selected
        allocated.append((minimum,maximum,new_y,new_z,diameter))
        wire["rear_corridor_slot"]={"y":new_y,"z":new_z,"physical_diameter_mm":diameter}
        wire["rear_corridor_slot"]["x_span"]=[minimum,maximum]
        original=deepcopy(wire["points"])
        # The right board stack changes level at its clear front edge, before
        # entering the rear column window, never through a perfboard shelf.
        for point in original:
            if point[0]>=RIGHT_LOWER_X and point[2] in (36,40) and 118<=point[1]<=rear_y:
                point[2]=new_z
        adjusted=[original[0]]
        for start,end in zip(original,original[1:]):
            if start[1]==end[1]==rear_y and abs(start[0]-end[0])>20:
                def transfer_x(point):
                    for index in range(M["count"]):
                        x=M["origin_x"]+M["pitch"]*index
                        if x+43.5<point[0]<x+49.5:
                            return x+41
                    return point[0]
                sx,ex=transfer_x(start),transfer_x(end)
                adjusted += [[sx,rear_y,start[2]],[sx,new_y,start[2]],[sx,new_y,new_z],
                             [ex,new_y,new_z],[ex,new_y,end[2]],[ex,rear_y,end[2]],end]
            else:
                adjusted.append(end)
        wire["points"]=_orthogonal(adjusted)
    for wire in wires:
        points=deepcopy(wire["points"]) if wire["id"]=="USB-PD" else _orthogonal(wire["points"])
        if not wire["id"].startswith("USB"):
            points=_remove_packing_return_jogs(wire,points)
        protected=[row["fixed_point"] for row in wire["moving_endpoints"]]
        clean=[]
        for point in points:
            if len(clean)>1 and clean[-1] not in protected:
                a,b=clean[-2],clean[-1]
                changed=[i for i in range(3) if a[i]!=b[i]]
                if len(changed)==1 and all(b[i]==point[i] for i in range(3) if i!=changed[0]) and (not wire["id"].startswith("USB") or (b[changed[0]]-a[changed[0]])*(point[changed[0]]-b[changed[0]])>=0):
                    clean[-1]=point
                    continue
            clean.append(point)
        wire["points"]=clean
        wire["route_mm"]=round(route_length(wire["points"]),1)
        required=math.ceil((route_length(wire["points"])+wire["service_mm"]+
                           wire["termination_mm"]+wire["motion_mm"])/10)*10
        wire["estimated_total_mm"]=required
        wire["estimated_cut_mm"]=max(0,required-(wire["stock_mm"] or wire["native_lead_mm"]))
    return wires


def _rolling_returns(wires):
    """Replace obsolete straight flex spans after fixed-trunk slot packing."""
    if MOVING_LOOP_Y != flex_routing.FIXED_Y or LIFT_MM != flex_routing.LIFT_MM:
        raise ValueError('Cable bend contract does not match the mechanism')
    for module in range(1, M['count']+1):
        members = [(wire,row) for wire in wires for row in wire['moving_endpoints']
                   if int(row['component'].rsplit('_',1)[1]) == module]
        lane = M['origin_x']+(module-1)*M['pitch']+20.95
        for wire,row in sorted(members, key=lambda pair:(-pair[0]['outer_diameter_mm'],pair[0]['id'])):
            diameter = wire['outer_diameter_mm']
            lane += diameter/2
            points = wire['points']
            anchor = points.index(row['fixed_point'])
            is_from = row['endpoint'] == wire['from']
            top = points[anchor-1 if is_from else anchor+1]
            curve, weights = flex_routing.rolling_return(lane, row['fixed_point'][2], top[2])
            stored = wire.setdefault('point_lift_weights', {})
            if is_from:
                tail = [list(point) for point in points[:anchor-1]]
                tail[-1][1] = flex_routing.MOVING_Y
                wire['points'] = tail+list(reversed(curve))+points[anchor+1:]
                for key, values in stored.items():
                    stored[key] = values[:anchor-1]+[0.0]*len(curve)+values[anchor+1:]
                stored[str(module)] = [1.0]*len(tail)+list(reversed(weights))+[0.0]*(len(points)-anchor-1)
            else:
                tail = [list(point) for point in points[anchor+2:]]
                tail[0][1] = flex_routing.MOVING_Y
                wire['points'] = points[:anchor]+curve+tail
                for key, values in stored.items():
                    stored[key] = values[:anchor]+[0.0]*len(curve)+values[anchor+2:]
                stored[str(module)] = [0.0]*anchor+weights+[1.0]*len(tail)
            row['fixed_point'] = list(curve[0])
            row['loop'] = loop_reserve(module)
            row['return_radius_mm'] = flex_routing.RADIUS_MM
            row['return_lane_x_mm'] = lane
            wire['curve_chord_error_mm'] = flex_routing.CHORD_ERROR_MM
            wire['bend_status'] = 'R8 constant-length rolling return; terminal approach anchors and actual flexible wire require dry fit'
            lane += diameter/2+flex_routing.LANE_GAP_MM
    for wire in wires:
        wire['route_mm'] = round(route_length(wire['points']),1)
        required = math.ceil((route_length(wire['points'])+wire['service_mm']+
                              wire['termination_mm']+wire['motion_mm'])/10)*10
        wire['estimated_total_mm'] = required
        wire['estimated_cut_mm'] = max(0,required-(wire['stock_mm'] or wire['native_lead_mm']))
    return wires


HARNESSES = _rolling_returns(_pack_corridors(_make_harnesses()))


def _physical_loads(endpoint, trail=(), visited=()):
    """Resolve declared fanout loads; local capacitor edges remain explicit below."""
    if endpoint not in FANOUTS:
        return [(endpoint, trail)]
    if endpoint in visited:
        raise ValueError(f"Cyclic assembly fanout: {endpoint}")
    result = []
    for load in FANOUTS[endpoint]["loads"]:
        branches = [wire for wire in HARNESSES if wire["from"] == endpoint and wire["to"] == load]
        if len(branches) != 1:
            raise ValueError(f"Missing or ambiguous assembly fanout branch: {endpoint} -> {load}")
        result.extend(_physical_loads(load, trail + (branches[0]["id"],), visited + (endpoint,)))
    return result


def _terminal_connections():
    """Keep actual scene endpoints so preview filtering preserves every port."""
    edges = []
    for wire in HARNESSES:
        if not wire["from"].startswith("degson_"):
            continue
        for endpoint, trail in _physical_loads(wire["to"]):
            target = _component(endpoint)
            note = "Assigned F2 assembly branch; actual terminals and physical fit require verification."
            if trail:
                note += " Through insulated soldered wire fanout; no power through perfboard."
            if target == "esp_usb_supply":
                target = "esp32"
                note += " USB-UART power route via ADD passive USB-A female >=3A supply socket; procurement and backfeed remain unconfirmed."
            path = " / ".join((wire["id"],) + trail)
            edges.append({"from": _component(wire["from"]), "to": target,
                          "label": f'{path}: {wire["net"]}', "kind": wire["kind"], "note": note})
    return tuple(edges)


CONNECTIONS = _terminal_connections()
CONNECTIONS += tuple({"from": "degson_3" if polarity == "-" else "pca9685" if index in (3, 4) else "degson_1",
                      "to": f"capacitor_{index}", "label": f'C{index}{polarity}: {"GND" if polarity == "-" else "DRIVE5" if index in (3, 4) else "MAIN5"}',
                      "kind": "ground" if polarity == "-" else "power",
                      "note": "Assigned F2 assembly branch through insulated wire fanout; not physically verified; capacitor polarity must be checked."}
                     for index in range(1, 7) for polarity in ("+", "-"))


def routes():
    result = deepcopy(list(HARNESSES))
    for wire in result:
        wire["from"] = canonical_endpoint(wire["from"])
        wire["to"] = canonical_endpoint(wire["to"])
        wire["purpose"] = f'{wire["from"]} -> {wire["to"]} / {wire["net"]}'
    return result


def validate(wires=None, terminals=None):
    wires = HARNESSES if wires is None else wires
    terminals = TERMINALS if terminals is None else terminals
    errors = []
    identifiers = [wire["id"] for wire in wires]
    if len(set(identifiers)) != len(identifiers):
        errors.append("duplicate harness identifier")
    expected_blocks = {"degson_1", "degson_2", "degson_3"}
    if set(terminals) != expected_blocks or len(set(NETS.values())) != 3:
        errors.append("three distinct Degson nets required")
    for block, ports in terminals.items():
        if set(ports) != {1, 2, 3, 4, 5} or sum(value is not None for value in ports.values()) > 5:
            errors.append(f"{block}: capacity exceeded or invalid port map")
        for port, endpoint in ports.items():
            attached = [wire for wire in wires if wire["from"] == f"{block}:{port}" or wire["to"] == f"{block}:{port}"]
            if len(attached) != (0 if endpoint is None else 1):
                errors.append(f"{block}:{port}: exactly one wire per occupied port required")
            for wire in attached:
                if wire["net"] != NETS[block] or wire["area_mm2"] is None or wire["area_mm2"] < 0.2:
                    errors.append(f"{wire['id']}: wrong net or thin wire at Degson")
    for wire in wires:
        endpoints = f'{wire["from"]} {wire["to"]}'
        for endpoint, point in ((wire["from"], wire["points"][0]), (wire["to"], wire["points"][-1])):
            try:
                if list(endpoint_anchor(endpoint)) != point:
                    errors.append(f"{wire['id']}: route endpoint does not match terminal registry")
            except (KeyError, ValueError):
                errors.append(f"{wire['id']}: unknown component or pin {endpoint}")
        if not wire.get("bundle") or not wire.get("colour") or wire.get("outer_diameter_mm", 0) <= 0:
            errors.append(f"{wire['id']}: physical wire specification missing")
        if wire["id"] == "USB-PD" and (not wire.get("transport_points") or
                                       wire["transport_points"][0] != list(USB_PARK_POINT)):
            errors.append("USB-PD: removed bank requires a disconnected parked transport route")
        if "SPK" in endpoints and wire["net"] == "GND":
            errors.append(f"{wire['id']}: speaker output grounded")
        if ":white" in endpoints or ":NC" in endpoints:
            errors.append(f"{wire['id']}: NC must stay isolated")
        if wire["kind"] == "power" and "perfboard" in endpoints:
            errors.append(f"{wire['id']}: power must not pass through perfboard")
        for endpoint in (wire["from"], wire["to"]):
            if endpoint in FANOUTS and wire["net"] != FANOUTS[endpoint]["net"]:
                errors.append(f"{wire['id']}: wrong fanout net")
        if wire["estimated_total_mm"] <= 0 or wire["estimated_cut_mm"] < 0:
            errors.append(f"{wire['id']}: invalid estimated length")
        minimum_cut = max(0, wire["estimated_total_mm"] - (wire["stock_mm"] or wire["native_lead_mm"]))
        if wire["estimated_cut_mm"] < minimum_cut:
            errors.append(f"{wire['id']}: insufficient cut length after native lead allowance")
        minimum = route_length(wire["points"]) + wire["service_mm"] + wire["termination_mm"] + wire["motion_mm"]
        if wire["estimated_total_mm"] < minimum:
            errors.append(f"{wire['id']}: insufficient route/slack allowance")
        moving_count = _moving_end_count(wire["from"], wire["to"])
        if moving_count and (wire["motion_mm"] < moving_count * (2 * LIFT_MM + 32) or not wire["fixed_anchor"] or not wire["moving_anchor"]):
            errors.append(f"{wire['id']}: moving harness requires a loop allowance and both strain reliefs per moving end")
    for index in range(1, 7):
        for identifier, channel in ((f"S{index}-S", index - 1), (f"U{index}-DIN", index + 5)):
            matches = [wire for wire in wires if wire["id"] == identifier]
            if len(matches) != 1 or matches[0]["channel"] != channel or matches[0]["from"] != f"pca9685:CH{channel}":
                errors.append(f"{identifier}: wrong PCA channel")
    if errors:
        raise ValueError("; ".join(errors))
    return {"status": "PASS", "harnesses": len(wires), "degson_blocks": 3,
            "motion_allowance_mm": MOTION_MM, "physical_verification": "NOT PERFORMED"}


def export(out):
    out = Path(out)
    result = validate()
    out.mkdir(parents=True, exist_ok=True)
    payload = {"revision": PARAMS["revision"], "units": "mm", "net_assignments": NETS,
               "net_aliases": NET_ALIASES, "terminal_aliases": TERMINAL_ALIASES,
               "buck_ground_joint": BUCK_GROUND_JOINT,
               "terminal_numbers": "logical labels only; no measured connector orientation",
               "terminals": TERMINALS, "fanouts": FANOUTS, "passives": PASSIVES, "harnesses": HARNESSES,
               "endpoint_registry": endpoint_registry(), "supply_connector": SUPPLY_CONNECTOR,
               "camera_fpc": CAMERA_FPC, "usb_coil": USB_COIL,
               "missing_acceptance": MISSING_ACCEPTANCE, "validation": result}
    (out / "cable-plan.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    fields = ["id", "net", "bundle", "from", "to", "area_mm2", "colour", "outer_diameter_mm",
              "from_termination", "to_termination", "route_mm", "service_mm", "termination_mm",
              "motion_mm", "native_lead_mm", "stock_mm", "estimated_total_mm", "estimated_cut_mm",
              "label_from", "label_to", "construction", "status", "note"]
    with (out / "cable-cut-list.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(HARNESSES)
    return result
