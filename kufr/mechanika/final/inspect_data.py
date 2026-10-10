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
# connection_data() was reviewed against these exact source bytes. Refreshing
# provenance must never imply that its manually encoded topology was updated.
REVIEWED_WIRING_SHA256 = "0691867c74d7d25d176176f636bbd898ebc8874418bb90a16543328bf71302e5"


def _assert_reviewed_wiring(record):
    if record["sha256"] != REVIEWED_WIRING_SHA256:
        raise ValueError(
            "Unreviewed wiring source: " + record["name"] + ". "
            "Review and update connection_data() against the changed wiring document, "
            "run its regression tests, then deliberately update REVIEWED_WIRING_SHA256 "
            "in inspect_data.py before refreshing the snapshot or packaging the viewer."
        )


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
        _assert_reviewed_wiring(records[1])
        return records
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        raise ValueError(f"Invalid or missing inspection source snapshot: {SNAPSHOT}: {error}") from error


def _source_records():
    paths = _source_paths()
    snapshot = None if all(path.is_file() for path in paths) else _snapshot_records()
    records = [_source_record(path) if path.is_file() else snapshot[index]
               for index, path in enumerate(paths)]
    _assert_reviewed_wiring(records[1])
    return records


def snapshot_sources():
    """Capture current authoritative docs, or validate the extracted snapshot."""
    if not all(path.is_file() for path in _source_paths()):
        assert_snapshot_current()
        return
    payload = {"schema": 1, "sources": _source_records()}
    content = (json.dumps(payload, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    if not SNAPSHOT.is_file() or SNAPSHOT.read_bytes() != content:
        SNAPSHOT.parent.mkdir(parents=True, exist_ok=True)
        SNAPSHOT.write_bytes(content)


def assert_snapshot_current():
    """Read-only freshness check used before claiming generated CAD is current."""
    records = _snapshot_records()
    if WIRING.is_file():
        _assert_reviewed_wiring(_source_record(WIRING))
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


# Active hardware only. Inventory spares are outside the physical assembly.
_HARDWARE = {
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
    "wooden_case": ("Skutečný dřevěný kufr", "Referenční celistvý plášť; není určen k tisku ani vrtání."),
    "wooden_lid": ("Prázdné víko kufru", "Referenční prázdné víko; detaily pantů nejsou změřené."),
    "floor_brace": ("Výztuha dna", "Potvrzená výška 5 mm; poloha XY je předpoklad, rám ji překračuje."),
    "frame_panel_": ("Díl montážního rámu", "PETG rám s integrovanými výztuhami; moduly podpírají samostatně lepené podlahové kotvy."),
    "floor_anchor_": ("Tištěná podlahová kotva", "PETG opora s bočním vložením matic pod souvislý strop a tvarovými dutinami pro epoxid."),
    "wood_bond_": ("Epoxidový spoj na dno", "Film na holém dřevě a odlité tvarové zámky v kotvě; vytvrzení a únosnost vyžadují fyzickou zkoušku."),
    "anchor_": ("Kotevní spoj rámu", "M4 × 10 do zachycené matice tištěné podlahové kotvy; rám drží i bez zdvihových modulů."),
    "frame_seam_": ("Spoj dílů rámu", "Samostatný M3 spoj přeplátovaných dílů."),
    "carrier_": ("Nosič zakoupeného dílu", "Pevné dorazy a dva pásky; upevnění k rámu nezávisle na bance."),
    "component_tie_": ("Uvolnitelný pásek komponenty", "Pásek drží součástku v nosiči s pevnými dorazy."),
    "esp_usb_supply": ("Pasivní USB-A napájecí zásuvka", "Doplnit jednu zásuvku nebo pigtail ≥3 A; pouze MAIN5/GND, datové kontakty izolované."),
    "usb_park_clip": ("Parkovací příchytka USB", "Drží odpojenou zástrčku po vyjmutí powerbanky."),
    "usb_coil": ("Rezerva metrového USB kabelu", "Svislá smyčka vedle banky, mimo pohyb a vyjímání; ohyby ověřit."),
    "moving_loop_": ("Nezávislá pohybová smyčka", "LED a U059 mají samostatnou rezervu každého lůžka; pružný pohyb ověřit fyzicky."),
    "module_": ("Montážní spoj modulu", "Samostatné M3 spoje; sejmutí modulu neuvolní sousední."),
    "transport_": ("Transportní aretace", "Vyjímatelný 8mm pásek prázdného lůžka; odstranit před provozem."),
    "retention_": ("Ochranná vložka lůžka", "Měkká boční ochrana a založené hrany pohyblivého lůžka."),
    "hinge_lower_": ("Spodní list závěsu víka", "Pouze reference původního kování kufru; žádné nové otvory ani montáž závěsu."),
    "hinge_upper_": ("Horní list závěsu víka", "Pouze reference původního kování víka; žádné nové otvory ani montáž závěsu."),
    "latch_": ("Uzávěr kufru", "Referenční obálka uzávěru; upevnění ověřit při sestavení."),
    "keeper_": ("Protikus uzávěru kufru", "Referenční obálka protikusu, do kterého se uzávěr zachytí."),
    "hinge_pin_": ("Ocelový závěs víka", "Referenční obálka původního závěsu; kování kufru se neupravuje."),
    "camera_angle": ("Nastavitelný držák kamery", "Drážkovaný držák; přesná kamera a její upevnění čekají na doručení."),
    "control_cover": ("Kryt ovládacích prvků", "Neprůhledný odnímatelný kryt s předním lemem."),
    "bank_cover": ("Kryt powerbanky", "Odnímatelný kryt nad vyjímatelnou powerbankou."),
    "red_membrane": ("Textilní membrána hlavního vypínače", "Volný samet s mezerou nad kolébkou; jde o textilní referenci."),
    "green_membrane": ("Textilní membrána vypínače pohonů", "Volný samet umožní stisk obou polovin kolébky."),
    "base_": ("Pevná základna zdvihu", "Tištěné vedení, servo lůžko a kotevní body transportního pásku."),
    "tray_": ("Pohyblivé lůžko hůlky", "Jeden výtisk včetně kluzného čepu Ø6; vodicí pouzdra, dorazy U059 a samostatná transportní aretace."),
    "cam_": ("Drážková zdvihová vačka", "Integrovaný kluzný čep lůžka přenáší zdvih i návrat; tření a opotřebení ověřit na výtisku."),
    "coupler_head_": ("Hlava šroubu spojky servopáky", "Šroub M2 spojuje koupenou páku s vačkou v nastavitelných drážkách."),
    "coupler_nut_": ("Matice spojky servopáky", "Matice M2 leží v zapuštěné kapse vačky."),
    "coupler_shaft_": ("Šroub spojky servopáky", "Šroub M2 × 10; skutečné otvory páky je nutné ověřit při sestavení."),
    "wand_": ("Hůlka v přihrádce", "Konzervativní obálka hůlky; nejde o rekonstrukci jejího ornamentu."),
    "guide_bolt_": ("Vodicí šroub M4", "Kovový spoj vedení pohyblivého lůžka."),
    "guide_washer_": ("Podložka vodicího šroubu", "Široká podložka vodicího spoje."),
    "fit_coupon": ("Zkušební tisk vůlí", "Ověření otvorů, vedení a kluzného čepu před tiskem celé sestavy; nevyhovující tisk se přetiskne."),
    "follower_groove_coupon": ("Měrka kluzné drážky", "Samostatný protikus k čepu měrky; ověřit volný kluz bez pilování."),
    "bank_removal": ("Prostor pro vyjmutí powerbanky", "Volná cesta vzhůru pro powerbanku a prsty po uvolnění pásků."),
    "esp_usb": ("Rezerva servisního USB ESP32", "Prostor konektoru a ohybu kabelu za deskou."),
    "pd_usb": ("Rezerva USB-C PD triggeru", "Prostor kabelu; přesná obálka zástrčky není změřená."),
    "bank_usb": ("Rezerva kabelu powerbanky", "Předpokládaný vývod C1 vyžaduje zkoušku usazení."),
    "camera_fpc": ("Rezerva kamerového FPC", "Skutečný konektor a délka FPC nejsou doložené."),
}


def _matches(name, prefix):
    return name.startswith(prefix) if prefix.endswith("_") else name == prefix


def describe(item):
    """Describe a geometry Item without importing CadQuery or changing geometry."""
    details = []
    title, description = item.name, "Díl sestavy F2."
    document = "Návrh F2: parameters.json / layout.py / mechanism.py / chassis.py"
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
    if item.name.startswith("degson_"):
        net = {"degson_1":"MAIN5 (+5 V)", "degson_2":"DRIVE5 (+5 V za zelenou)",
               "degson_3":"GND (nevypínat)"}.get(item.name)
        if net:
            details.append("Montážní návrh KABELAZ.md: " + net + "; jeden vodič na port, izolované měděné odbočky. Nejde o měřené zapojení.")
    if item.name.startswith("capacitor_"):
        branch = {"capacitor_1":"LED", "capacitor_2":"LED", "capacitor_3":"PCA V+",
                  "capacitor_4":"PCA V+", "capacitor_5":"audio/elektronika", "capacitor_6":"audio/elektronika"}.get(item.name)
        if branch:
            details.append("Montážní návrh KABELAZ.md: dvojice paralelně u větve " + branch + "; ověřit polaritu a izolaci.")
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

    This preserves the source pinout independently of the assembly fan-outs
    in harness.py. Their explicit nets are added only to the generated viewer.
    """
    _source_records()
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
