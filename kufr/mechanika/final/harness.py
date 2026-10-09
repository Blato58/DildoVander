"""F1 assembly harness: electrical topology and estimated orthogonal cable routes.

No CAD dependency. Coordinates are routing anchors, never measured connector pins.
Power fanouts are insulated copper wire joints, not PCB current-carrying tracks.
"""
from __future__ import annotations

import csv
import json
import math
from copy import deepcopy
from pathlib import Path


ROOT = Path(__file__).resolve().parent
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
ANCHORS = {
    "powerbank": (32, 107, 84), "pd_trigger": (49, 79, 29),
    "buck": (40, 80, 100), "breaker": (39, 84, 74),
    "red_main": (22, 40.5, 80), "green_drives": (56, 40.5, 80),
    "white_ptt": (38, 19, 82), "degson_1": (191.5, 195, 31),
    "degson_2": (243.5, 195, 31), "degson_3": (291.5, 195, 31),
    "pca9685": (209, 20, 23), "mcp23017": (139, 19, 19),
    "pixel_shifter": (275, 17, 19), "esp32": (411, 50, 86),
    "esp_usb_supply": (390, 97, 85), "microphone": (301, 11, 83),
    "amplifier_left": (51, 111, 89), "amplifier_right": (389, 120, 81),
    "speaker_left": (39, 100, 40), "speaker_right": (403, 100, 40),
    "perfboard_1": (395, 210, 22), "perfboard_2": (395, 210, 62),
    "F_LED+": (86, 212, 45), "F_LED-": (97, 212, 45),
    "F_AUDIO+": (235, 212, 45), "F_AUDIO_PANEL-": (246, 212, 45),
    "F_PANEL+": (86, 212, 45), "F_U123+": (164, 212, 45),
    "F_U456+": (260, 212, 45), "F_U-": (275, 212, 45),
    "F_LOGIC-": (365, 27, 38), "F_LOGIC3": (355, 27, 38),
}
for _index in range(1, 7):
    _x = 77 + 48 * (_index - 1)
    ANCHORS[f"servo_{_index}"] = (_x + 14, 145, 45)
    ANCHORS[f"u059_{_index}"] = (_x + 23, 65, 66)
    ANCHORS[f"led_{_index}"] = (_x + 47, 58, 70)
for _index, _point in enumerate(((45, 111, 52), (58, 111, 52), (174, 55, 17),
                               (187, 55, 17), (235, 218, 15), (246, 218, 15)), 1):
    ANCHORS[f"capacitor_{_index}"] = _point

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
LIFT_MM = 16
MOTION_MM = 2 * LIFT_MM + 32
MISSING_ACCEPTANCE = [
    "ESP USB-UART power adapter: purchased USB-A socket is not confirmed; keep ESP power branch unconnected until a suitable existing adapter is verified.",
    "Dry-fit actual plug directions, stock cable quantities, solder joint insulation, wire bends, moving loops and closed-case clearance.",
    "Measure voltage, current, capacitor discharge, OE/reset states and complete individual actuator calibration.",
]


def _component(endpoint):
    return endpoint.split(":", 1)[0]


def _endpoint_path(endpoint, corridor):
    """From nominal endpoint outward to its fixed shared corridor."""
    part = _component(endpoint)
    a = ANCHORS[part]
    y, z = (212, 45) if corridor == "power" else (27, 38)
    if part.startswith(("u059_", "led_")):
        index = int(part.split("_")[-1])
        origin_x = 77 + 48 * (index - 1)
        loop_x, outer_x = origin_x + 29, origin_x + 44
        points = [a, (a[0], a[1], 59), (a[0], 99, 59),
                  (loop_x, 99, 59), (loop_x, 99, 45)]
        if corridor == "power":
            return points + [(outer_x, 99, 45), (outer_x, y, z)]
        return points + [(loop_x, 99, z), (loop_x, y, z)]
    if part in ("esp32", "amplifier_right", "esp_usb_supply"):
        # Descend through the shelf slot, then leave its leg rows via the front.
        return [a, (380, a[1], a[2]), (380, 50, a[2]), (376, 50, a[2]),
                (376, 50, 38), (380, 50, 38), (380, 27, 38), (365, 27, 38),
                (365, 27, z), (365, y, z)]
    if part.startswith("servo_"):
        origin_x = 77 + 48 * (int(part.split("_")[-1]) - 1)
        return [a, (a[0], a[1], 55), (origin_x + 26, a[1], 55),
                (origin_x + 26, 30, 55), (origin_x + 26, 30, z),
                (origin_x + 26, 27, z)]
    if part == "amplifier_left":
        return [a, (65, a[1], a[2]), (65, 93, a[2]), (65, 93, z),
                (60, 93, z), (60, y, z)]
    if part in ("buck", "breaker"):
        return [a, (a[0], 93, a[2]), (65, 93, a[2]), (65, 93, z),
                (60, 93, z), (60, y, z)]
    if part in ("red_main", "green_drives", "white_ptt"):
        return [a, (a[0], 62, a[2]), (a[0], 62, 84), (65, 62, 84),
                (65, 93, 84), (65, 93, z), (60, 93, z), (60, y, z)]
    if corridor == "power" and part in ("pca9685", "pixel_shifter", "mcp23017", "microphone", "F_LOGIC-"):
        return [a, (a[0], a[1], 38), (a[0], 27, 38), (365, 27, 38),
                (365, 27, z), (365, y, z)]
    route_x = 60 if corridor == "power" and a[0] < 71 else a[0]
    return [a, (a[0], a[1], z), (route_x, a[1], z), (route_x, y, z)]


def _service_path(endpoint):
    """Local source/load tails terminating on the applicable shelf passage."""
    part = _component(endpoint)
    a = ANCHORS[part]
    if part == "speaker_left":
        return [a, (60, a[1], a[2]), (60, 93, a[2]), (65, 93, a[2])]
    if part == "amplifier_left":
        return [a, (65, a[1], a[2]), (65, 93, a[2])]
    if part in ("buck", "breaker"):
        return [a, (a[0], 93, a[2]), (65, 93, a[2])]
    if part == "pd_trigger":
        return [a, (60, a[1], a[2]), (60, 93, a[2]), (65, 93, a[2])]
    if part in ("red_main", "green_drives", "white_ptt"):
        return [a, (a[0], 62, a[2]), (a[0], 62, 84), (65, 62, 84), (65, 93, 84)]
    if part == "powerbank":
        return [a, (a[0], 99, 84), (65, 99, 84), (65, 93, 84)]
    if part in ("esp32", "amplifier_right", "esp_usb_supply", "speaker_right"):
        return [a, (380, a[1], a[2]), (380, 50, a[2]), (376, 50, a[2])]
    return None


def _route(start, end, corridor):
    if corridor == "local":
        a, b = ANCHORS[_component(start)], ANCHORS[_component(end)]
        start_path, end_path = _service_path(start), _service_path(end)
        if _component(end) in ("capacitor_3", "capacitor_4"):
            points = [a, (a[0], 35, a[2]), (b[0], 35, a[2]),
                      (b[0], b[1], a[2]), b]
        elif start_path and end_path and start_path[-1][:2] == end_path[-1][:2]:
            points = start_path + list(reversed(end_path))
        else:
            points = [a, (a[0], b[1], a[2]), (b[0], b[1], a[2]), b]
    else:
        points = _endpoint_path(start, corridor) + list(reversed(_endpoint_path(end, corridor)))
    return [list(point) for i, point in enumerate(points) if i == 0 or point != points[i - 1]]


def route_length(points):
    """Manhattan length of staging anchors; includes no implicit bend proof."""
    return sum(sum(abs(a - b) for a, b in zip(start, end))
               for start, end in zip(points, points[1:]))


def _wire(identifier, start, end, net, area=0.5, corridor="power", native=0,
          moving=False, channel=None, stock=None, note="", construction="solder + heatshrink"):
    points = _route(start, end, corridor)
    required = math.ceil((route_length(points) + SERVICE_MM + TERMINATION_MM + (MOTION_MM if moving else 0)) / 10) * 10
    return {
        "id": identifier, "from": start, "to": end, "net": net,
        "kind": "ground" if net in ("GND", "PD_RETURN") else "power" if net in ("MAIN5", "DRIVE5", "LOGIC3", "PD12", "BUCK5", "USB5") else "signal",
        "area_mm2": area, "points": points, "route_mm": round(route_length(points), 1),
        "service_mm": SERVICE_MM, "termination_mm": TERMINATION_MM,
        "motion_mm": MOTION_MM if moving else 0, "lift_mm": LIFT_MM if moving else 0,
        "native_lead_mm": native, "stock_mm": stock,
        "estimated_total_mm": required, "estimated_cut_mm": max(0, required - (stock or native)),
        "channel": channel, "label_from": f"{identifier} A / {net} / {start}",
        "label_to": f"{identifier} B / {net} / {end}", "construction": construction,
        "note": note, "status": "estimate; cut after dry-fit",
        "fixed_anchor": "fixed base tie outside cam/guide sweep" if moving else None,
        "moving_anchor": "tray tie before module; solder joint outside flexing span" if moving else None,
    }


def _make_harnesses():
    wires = [
        _wire("USB-PD", "powerbank:C1", "pd_trigger:USB-C", "PD12", None, "local", stock=1000,
              construction="intact purchased USB-C/USB-C cable; do not cut"),
        _wire("P12+", "pd_trigger:OUT+", "buck:IN+", "PD12", 1.5, "local"),
        _wire("P12-", "pd_trigger:OUT-", "buck:IN-", "PD_RETURN", 1.5, "local"),
        _wire("P5-FUSE", "buck:OUT+", "breaker:IN", "BUCK5", 1.5, "local"),
        _wire("P5-MAIN", "breaker:OUT", "red_main:IN", "BUCK5", 1.5, "local"),
        _wire("USB-ESP", "esp_usb_supply:USB-A", "esp32:USB-UART", "USB5", None, "local", stock=1000,
              construction="intact purchased USB-A/USB-C cable; adapter unconfirmed",
              note="Do not energize until adapter exists and polarity/backfeed are verified."),
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


HARNESSES = _make_harnesses()
CONNECTIONS = tuple({"from": _component(wire["from"]), "to": _component(wire["to"]),
                     "label": f'{wire["id"]}: {wire["net"]}', "kind": wire["kind"],
                     "note": "Assembly cable route estimate; pin locations and cut lengths require dry-fit."}
                    for wire in HARNESSES if "degson_" in wire["from"])
CONNECTIONS += tuple({"from": "degson_3" if polarity == "-" else "pca9685" if index in (3, 4) else "degson_1",
                      "to": f"capacitor_{index}", "label": f'C{index}{polarity}: {"GND" if polarity == "-" else "DRIVE5" if index in (3, 4) else "MAIN5"}',
                      "kind": "ground" if polarity == "-" else "power",
                      "note": "Assigned F1 assembly branch through insulated wire fanout; not physically verified; capacitor polarity must be checked."}
                     for index in range(1, 7) for polarity in ("+", "-"))


def routes():
    return [{"id": wire["id"], "points": deepcopy(wire["points"]), "kind": wire["kind"],
             "purpose": f'{wire["from"]} -> {wire["to"]} / {wire["net"]}'} for wire in HARNESSES]


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
        minimum = route_length(wire["points"]) + wire["service_mm"] + wire["termination_mm"] + wire["motion_mm"]
        if wire["estimated_total_mm"] < minimum:
            errors.append(f"{wire['id']}: insufficient route/slack allowance")
        moving_end = any(_component(endpoint).startswith(("u059_", "led_")) for endpoint in (wire["from"], wire["to"]))
        if moving_end and (wire["motion_mm"] < 2 * LIFT_MM + 32 or not wire["fixed_anchor"] or not wire["moving_anchor"]):
            errors.append(f"{wire['id']}: moving harness requires loop and two strain reliefs")
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
    payload = {"revision": "F1", "units": "mm", "net_assignments": NETS,
               "net_aliases": NET_ALIASES, "terminal_aliases": TERMINAL_ALIASES,
               "buck_ground_joint": BUCK_GROUND_JOINT,
               "terminal_numbers": "logical labels only; no measured connector orientation",
               "terminals": TERMINALS, "fanouts": FANOUTS, "passives": PASSIVES, "harnesses": HARNESSES,
               "missing_acceptance": MISSING_ACCEPTANCE, "validation": result}
    (out / "cable-plan.json").write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    fields = ["id", "net", "from", "to", "area_mm2", "route_mm", "service_mm", "termination_mm",
              "motion_mm", "native_lead_mm", "stock_mm", "estimated_total_mm", "estimated_cut_mm",
              "label_from", "label_to", "construction", "status", "note"]
    with (out / "cable-cut-list.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(HARNESSES)
    return result
