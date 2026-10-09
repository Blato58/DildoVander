"""Human-readable inspection and documented wiring for the generated offline viewer.

Only the Python generator reads the Markdown sources; the resulting viewer embeds
these strings and needs neither a server nor access to the source documents.
"""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parent
KUFR = ROOT.parents[1]
DIMENSIONS = KUFR / "mechanika/rozmery-nakoupenych-soucastek.md"
WIRING = KUFR / "docs/zapojeni-potvrzeneho-hardwaru.md"
SNAPSHOT = ROOT / "reference/inspect-sources.json"


def _source_paths():
    return (DIMENSIONS, WIRING)


def _source_record(path):
    content = path.read_bytes()
    return {"name": path.relative_to(KUFR).as_posix(),
            "sha256": hashlib.sha256(content).hexdigest(),
            "text": content.decode("utf-8")}


def _snapshot_records():
    """Validate embedded source bytes before trusting an extracted package."""
    try:
        payload = json.loads(SNAPSHOT.read_text(encoding="utf-8"))
        if payload.get("schema") != 1:
            raise ValueError("Unsupported source snapshot schema")
        records = payload["sources"]
        expected = [path.relative_to(KUFR).as_posix() for path in _source_paths()]
        if not isinstance(records, list) or [row["name"] for row in records] != expected:
            raise ValueError("Source snapshot provenance does not match the required documents")
        for row in records:
            if not isinstance(row["text"], str):
                raise ValueError("Source snapshot text must be UTF-8 text")
            if hashlib.sha256(row["text"].encode("utf-8")).hexdigest() != row["sha256"]:
                raise ValueError("Source snapshot digest mismatch: " + row["name"])
        return records
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        raise ValueError(f"Invalid or missing inspection source snapshot: {SNAPSHOT}: {error}") from error


def _source_records():
    paths = _source_paths()
    snapshot = None if all(path.is_file() for path in paths) else _snapshot_records()
    return [_source_record(path) if path.is_file() else snapshot[index]
            for index, path in enumerate(paths)]


def snapshot_sources():
    """Capture current authoritative docs, or validate the extracted snapshot."""
    if not all(path.is_file() for path in _source_paths()):
        assert_snapshot_current()
        return
    payload = {"schema": 1, "sources": [_source_record(path) for path in _source_paths()]}
    content = (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    if not SNAPSHOT.is_file() or SNAPSHOT.read_bytes() != content:
        SNAPSHOT.parent.mkdir(parents=True, exist_ok=True)
        SNAPSHOT.write_bytes(content)


def assert_snapshot_current():
    """Read-only freshness check used before claiming generated CAD is current."""
    records = _snapshot_records()
    for path, record in zip(_source_paths(), records):
        if path.is_file() and _source_record(path)["sha256"] != record["sha256"]:
            raise ValueError("Stale inspection source snapshot: " + record["name"])


def _plain(markdown):
    markdown = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", markdown)
    return markdown.replace("**", "").replace("`", "").strip()


def _purchased_rows():
    rows = {}
    for line in _source_records()[0]["text"].splitlines():
        if not line.startswith("|"):
            continue
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) in (3, 4) and not cells[0].startswith("---"):
            rows[_plain(cells[0])] = [_plain(cell) for cell in cells]
    return rows


# Ordered prefixes keep the spare and hardware descriptions ahead of generic parts.
_HARDWARE = {
    "spare_pca": ("Rezervní PCA9685", "PCA9685"),
    "spare_red_rocker": ("Rezervní červená kolébka", "Červeně podsvícená kolébka"),
    "spare_degson_": ("Rezervní svorka Degson", "Degson 5pin"),
    "spare_capacitor_": ("Rezervní kondenzátor", "Kondenzátor 470"),
    "esp32": ("Řídicí deska ESP32-S3", "ESP32-S3 N16R8"),
    "pca9685": ("Řadič serv a vibrací PCA9685", "PCA9685"),
    "mcp23017": ("Vstupní expandér MCP23017", "MCP23017"),
    "pixel_shifter": ("Převodník úrovně LED Pixel Shifter", "Adafruit Pixel Shifter"),
    "speaker_": ("Reproduktor Waveshare", "Waveshare 14595"),
    "amplifier_": ("Zesilovač DFR0954", "DFRobot DFR0954"),
    "breaker": ("Vratný jistič 5 A", "Tepelný vratný jistič"),
    "buck": ("Měnič Joy-IT na 5 V", "Joy-IT SBC-Buck02"),
    "pd_trigger": ("USB-C PD trigger na 12 V", "USB-C PD modul"),
    "perfboard_": ("Univerzální pájecí deska", "Univerzální deska"),
    "camera": ("Objednaná kamera", "Kamera pro OV5640"),
    "microphone": ("I2S mikrofon ICS43434", "Adafruit ICS43434"),
    "powerbank": ("Vyjímatelná powerbanka UGREEN PB720", "UGREEN PB720"),
    "degson_": ("Svorka Degson pro samostatnou síť", "Degson 5pin"),
    "capacitor_": ("Kondenzátor 470 µF", "Kondenzátor 470"),
    "red_main": ("Červený hlavní vypínač", "Červeně podsvícená kolébka"),
    "green_drives": ("Zelený vypínač pohonů", "Zeleně podsvícená kolébka"),
    "white_ptt": ("Bílé momentové tlačítko PTT", "Bíle podsvícené momentové PTT"),
    "servo_": ("Polohovací mikroservo", "Polohovací mikroservo"),
    "stock_horn_": ("Koupená oboustranná servopáka", "Polohovací mikroservo"),
    "u059_": ("Vibrační modul M5Stack U059", "M5Stack U059"),
    "led_": ("Úsek adresovatelných LED WS2812B", "WS2812B"),
}

_MECHANICAL = {
    "body_": ("Díl spodní skořepiny", "Tištěný díl s přeplátovanými šroubovanými spoji."),
    "lid_lining": ("Textilní výstelka prázdného víka", "Referenční vnitřní textilní výstelka víka."),
    "lid_": ("Díl prázdného víka", "Tištěný díl víka; víko nenese elektroniku."),
    "hinge_lower_": ("Spodní list závěsu víka", "Referenční list ocelového závěsu připevněný ke spodní skořepině; skutečné otvory přizpůsobit koupenému kusu."),
    "hinge_upper_": ("Horní list závěsu víka", "Referenční list ocelového závěsu připevněný k víku; skutečné otvory přizpůsobit koupenému kusu."),
    "latch_": ("Uzávěr kufru", "Referenční obálka uzávěru; upevnění ověřit při sestavení."),
    "keeper_": ("Protikus uzávěru kufru", "Referenční obálka protikusu, do kterého se uzávěr zachytí."),
    "hinge_pin_": ("Ocelový závěs víka", "Referenční obálka závěsu; skutečnou rozteč otvorů přizpůsobit koupenému kusu."),
    "rack_": ("Boční nosič elektroniky", "Tištěná základna s podporami krytů a otvory pro pásky."),
    "left_service_table": ("Levá servisní plošina", "Odnímatelná plošina nad spodními součástkami."),
    "right_front_table": ("Pravá přední servisní plošina", "Odnímatelná plošina pro řídicí desku a audio."),
    "right_back_table": ("Pravá zadní servisní plošina", "Odnímatelná plošina pro měnič; ponechat chlazení."),
    "switch_shelf": ("Montážní police vypínačů", "Nese kolébky pod odnímatelnými textilními membránami."),
    "camera_angle": ("Nastavitelný držák kamery", "Drážkovaný držák; přesná kamera a její upevnění čekají na doručení."),
    "control_cover": ("Kryt ovládacích prvků", "Neprůhledný odnímatelný kryt s předním lemem."),
    "bank_cover": ("Kryt powerbanky", "Odnímatelný kryt nad vyjímatelnou powerbankou."),
    "right_front_cover": ("Pravý přední kryt", "Neprůhledný kryt elektroniky s vnitřní sukní."),
    "right_back_cover": ("Pravý zadní větraný kryt", "Mřížka nad měničem zůstává bez sametu."),
    "red_membrane": ("Textilní membrána hlavního vypínače", "Volný samet s mezerou nad kolébkou; jde o textilní referenci."),
    "green_membrane": ("Textilní membrána vypínače pohonů", "Volný samet umožní stisk obou polovin kolébky."),
    "base_": ("Pevná základna zdvihu", "Tištěné vedení, servo lůžko a kotevní body transportního pásku."),
    "tray_": ("Pohyblivé lůžko hůlky", "Tištěné lůžko s vodicími pouzdry, dorazy U059 a nastavitelnou plstěnou brzdou."),
    "cam_": ("Drážková zdvihová vačka", "Zachycený váleček přenáší zdvih i návrat; kruhové úseky drží koncové polohy."),
    "roller_": ("Nylonový sledovací váleček", "Váleček v drážce vačky se otáčí na mosazném pouzdře."),
    "follower_head_": ("Hlava šroubu sledovače", "Obálka hlavy M3 ISO7380 u vačky."),
    "follower_sleeve_": ("Mosazné ložiskové pouzdro", "Přenáší sevření šroubu a nechává váleček volně otáčet."),
    "follower_spacer_": ("Distanční podložka válečku", "Vymezuje axiální vůli válečku."),
    "follower_washer_": ("Podložka sledovače", "Podložka pod maticí upevnění sledovače."),
    "follower_nut_": ("Matice sledovače M3", "Kovová matice zajišťuje závitový spoj sledovače."),
    "follower_pin_": ("Šroub sledovače M3", "Osa sledovače prochází pouzdrem, válečkem a lůžkem."),
    "coupler_head_": ("Hlava šroubu spojky servopáky", "Šroub M2 spojuje koupenou páku s vačkou v nastavitelných drážkách."),
    "coupler_nut_": ("Matice spojky servopáky", "Matice M2 leží v zapuštěné kapse vačky."),
    "coupler_shaft_": ("Šroub spojky servopáky", "Šroub M2 × 10; skutečné otvory páky je nutné ověřit při sestavení."),
    "wand_": ("Hůlka v přihrádce", "Konzervativní obálka hůlky; nejde o rekonstrukci jejího ornamentu."),
    "guide_bolt_": ("Vodicí šroub M4", "Kovový spoj vedení pohyblivého lůžka."),
    "guide_washer_": ("Podložka vodicího šroubu", "Široká podložka vodicího spoje."),
    "fit_coupon": ("Zkušební tisk vůlí", "Ověření posuvu a otvorů před tiskem celé sestavy."),
    "bank_removal": ("Prostor pro vyjmutí powerbanky", "Volná cesta vzhůru pro powerbanku a prsty po uvolnění pásků."),
    "esp_usb": ("Rezerva servisního USB ESP32", "Prostor konektoru a ohybu kabelu za deskou."),
    "pd_usb": ("Rezerva USB-C PD triggeru", "Prostor kabelu; přesná obálka zástrčky není změřená."),
    "bank_usb": ("Rezerva kabelu powerbanky", "Předpokládaný vývod C1 vyžaduje zkoušku usazení."),
    "camera_fpc": ("Rezerva kamerového FPC", "Skutečný konektor a délka FPC nejsou doložené."),
    "buck_air": ("Vzduch pro chlazení měniče", "Volný vzduch pod nepotaženou větrací mřížkou."),
    "power_trunk": ("Trasa hlavního napájení", "Rezerva pro dvojlinku 1,5 mm² přes pevná lůžka; není to elektrický díl."),
    "front_bus": ("Trasa přední signálové sběrnice", "Rezerva nad deskami pod předním krytem; není to elektrický díl."),
}


def _matches(name, prefix):
    return name.startswith(prefix) if prefix.endswith("_") else name == prefix


def describe(item):
    """Describe a geometry Item without importing CadQuery or changing geometry."""
    details = []
    title, description = item.name, "Díl sestavy F1."
    document = "Návrh F1: layout.py / mechanism.py / chassis.py"
    for prefix, (candidate_title, row_prefix) in _HARDWARE.items():
        if _matches(item.name, prefix):
            title = candidate_title
            row = next((value for key, value in _purchased_rows().items()
                        if key.startswith(row_prefix)), None)
            if row is None:
                raise ValueError(f"Missing purchased dimensions row: {row_prefix}")
            description = row[0]
            if len(row) == 4:
                details.extend(["Zakoupeno: " + row[1], "Rozměry a zdroj: " + row[2],
                                "Poznámka k rozvržení: " + row[3]])
            else:
                details.extend(["Stav: " + row[1], "Rozměry a omezení: " + row[2]])
            document = "mechanika/rozmery-nakoupenych-soucastek.md"
            break
    else:
        for prefix, (candidate_title, candidate_description) in _MECHANICAL.items():
            if _matches(item.name, prefix):
                title, description = candidate_title, candidate_description
                break
    if item.name.startswith("spare_"):
        details.append("Rezerva je odpojená; vývody izolovat a díl zajistit pro přepravu.")
    if item.name.startswith("degson_"):
        details.append("Konkrétní ID této obálky není přiřazeno síti +5 V, pohony ani GND; zapojení nelze odvodit z polohy v CAD.")
    if item.source and item.source != "design":
        details.append("Podklad obálky CAD: " + item.source)
    details.append("Skupina modelu: " + item.group + "; ID: " + item.name)
    return {"title": title, "description": description, "details": details,
            "source": document + "; " + (item.source or "design")}


def source_evidence():
    return [{"name": record["name"], "sha256": record["sha256"]}
            for record in _source_records()]


def connection_data(names: set[str]):
    """Logical documented nets, not cable routing or verified physical wiring.

    Degson and capacitor IDs have no documented net assignment. Deliberately
    leave them disconnected instead of treating their CAD position as evidence.
    """
    result = []

    def add(start, end, label, kind, note=""):
        if start in names and end in names:
            result.append({"from": start, "to": end, "label": label,
                           "kind": kind, "note": note})

    add("powerbank", "pd_trigger", "USB-C1 PD 12 V / 3 A", "power", "Koupený C–C kabel 60 W / 3 A; trigger nastavit na 12 V.")
    add("pd_trigger", "buck", "12 V → vstup 9–35 V", "power", "12 V vede pouze do vstupu měniče.")
    add("buck", "breaker", "5 V / max. 5 A", "power", "Výstup a polaritu nejdřív změřit.")
    add("breaker", "red_main", "5 V přes vratný jistič 5 A", "power")
    for target in ("esp32", "pixel_shifter", "amplifier_left", "amplifier_right", "green_drives", "white_ptt"):
        note = "LED tlačítka samostatně; kontakt GPA0 nesmí dostat 5 V." if target == "white_ptt" else "5V hvězdicový rozvod; ESP přes USB-UART napájení."
        add("red_main", target, "5 V hlavní větev", "power", note)
    add("green_drives", "pca9685", "5 V → PCA V+", "power", "V+ napájí serva; VCC je oddělená logika 3,3 V. Zem se nevypíná.")
    for target in ("pca9685", "mcp23017", "microphone"):
        pin = {"pca9685": "VCC", "mcp23017": "VIN/VCC", "microphone": "VDD"}[target]
        add("esp32", target, "3V3 → " + pin, "power", "Logika a pull-upy pouze 3,3 V.")
    for target in ("pca9685", "mcp23017"):
        add("esp32", target, "GPIO1 SDA + GPIO14 SCL", "signal", "Společná I2C sběrnice 100 kHz; PCA 0x40, MCP 0x20.")
    add("esp32", "pca9685", "GPIO2 → OE", "signal", "HIGH vypíná výstupy; dva 22kΩ paralelně tvoří 11kΩ pull-up na 3,3 V. OE neodpojuje 5 V.")
    add("esp32", "pixel_shifter", "GPIO48 → vstup", "signal", "Externí řetězec začíná pixelem 0.")
    add("pixel_shifter", "led_1", "5V DATA přes 330 Ω → DIN", "signal", "Pořadí led_1…led_6 je konvence vizualizace; zdroj neurčuje ID úseků CAD.")
    for index in range(1, 7):
        add("pca9685", f"servo_{index}", f"PCA kanál {index - 1} → signál serva {index}", "signal", "Individuální kalibrace pulzu a doby; poloha se neměří.")
        add("pca9685", f"servo_{index}", "V+ → napájení serva 5 V", "power", "Za zelenou kolébkou.")
        add("pca9685", f"u059_{index}", f"PCA kanál {index + 5} → DIN U059 {index}", "signal", "50 Hz, digitální FULL_ON/FULL_OFF; 100kΩ pull-down DIN, bílá NC.")
        add("green_drives", f"u059_{index}", "5 V → červený vodič U059", "power")
        add("red_main", f"led_{index}", "5 V → LED úsek", "power", "Napájení z rozvodu; šest úseků po šesti LED.")
        if index < 6:
            add(f"led_{index}", f"led_{index + 1}", "DOUT → DIN", "signal", "Pořadí led_1…led_6 je konvence vizualizace; zdroj neurčuje ID úseků CAD.")
    add("esp32", "microphone", "GPIO21 → SCK + GPIO41 → WS", "signal", "Philips I2S 32 kHz / 32bit stereo; mikrofon levý slot.")
    add("microphone", "esp32", "SD → GPIO42", "signal", "Zvuková data mikrofonu vstupují do ESP32; mikrofon levý slot.")
    for side in ("left", "right"):
        add("esp32", f"amplifier_{side}", "GPIO21 BCLK + GPIO41 LRC + GPIO47 DIN", "signal", "Stejná řeč v obou stereo slotech; SD/GAIN nezapojovat k MCP, R1 ponechat.")
        add(f"amplifier_{side}", f"speaker_{side}", "PH2 SPK+ / SPK− → vlastní 8Ω reproduktor", "signal", "SPK− není GND. Výstupy obou zesilovačů nespojovat.")
    add("mcp23017", "white_ptt", "GPA0 → momentový kontakt", "signal", "Kontakt na GND; 15kΩ pull-up na 3,3 V, LED samostatně. GPA1 a ostatní vstupy volné.")
    add("esp32", "camera", "Konektor OV5640 / kamerové GPIO", "signal", "4 SIOD, 5 SIOC, 6 VSYNC, 7 HREF, 8 Y4, 9 Y3, 10 Y5, 11 Y2, 12 Y6, 13 PCLK, 15 XCLK, 16 Y9, 17 Y8, 18 Y7; přesný modul/FPC čeká na doručení.")
    # A logical star depicts the common net. ESP is the visual hub, not an
    # instruction to carry motor current through the ESP board.
    grounded = ["powerbank", "pd_trigger", "buck", "red_main", "green_drives", "pca9685", "mcp23017", "pixel_shifter", "microphone", "amplifier_left", "amplifier_right", "white_ptt"]
    grounded += [f"{prefix}_{index}" for prefix in ("servo", "u059", "led") for index in range(1, 7)]
    for target in grounded:
        add("esp32", target, "Společná GND", "ground", "Logická společná síť, nikoli kabelová trasa přes ESP. Zem pohonů nevypínat; SPK− je oddělený výstup.")
    return result
