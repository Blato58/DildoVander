"""Per-part support-free manufacturing contracts and reproducible print profiles."""
from __future__ import annotations

import csv
import hashlib
import json
from pathlib import Path

from printability import analyze
from slicer_validation import profile_values


ANALYTICAL_PROFILE = {"layer_height_mm": .2, "max_overhang_degrees": 45,
                      "max_bridge_mm": 12, "min_wall_mm": .4,
                      "bed_size_mm": (220, 220, 250)}
BASE_PROFILE = Path(__file__).with_name("support-free-petg.ini")


def _contract(name):
    if name.startswith("frame_panel_"):
        return ("REDESIGN", "Flat frame underside; uprights grow upward", "moderate",
                "Planar dovetails + M3 screw clamp; bolted to bonded floor anchors",
                "Frame panels, floor anchors, lift bases and electronics",
                "Shared frame total: 8 M3x6 + 8 M3 nuts; 4 M4x10 + 4 captive M4 nuts")
    if name.startswith("floor_anchor_") and name != "floor_anchor_coupon":
        return ("REDESIGN", "Flat top down; curved wood-facing surface upward", "moderate",
                "Epoxy bond and cast tapered undercut keys; captive M3/M4 nuts",
                "Wood floor and independently bolted frame/module bases",
                "Seat all captive nuts against roof using temporary screws before bonding; see mechanical BOM")
    if name == "lift_base":
        return ("REDESIGN", "Flat underside down; servo cradle upward", "moderate",
                "Four M3 floor screws, two M4 guides; servo retained in cradle by two ties",
                "Floor anchors, frame, servo and both bed guides",
                "Per module: 4 M3x14, 2 M4x100 guides with nuts, two original servo ties and transport wrap")
    if name in ("wand_bed_left", "wand_bed_right"):
        return ("SPLIT", "Left outer X edge down" if name.endswith("left") else "Right outer X edge down",
                "moderate", "Two keyed crossbeams; 0.25 mm mating clearance; transverse M3 clamp",
                "Other bed half, guides, cam peg (right), LED/U059 ties and removable wand",
                "Per assembled pair: 2 M3x45 ISO7380-1 + 2 thin M3 DIN439 nuts; original component ties")
    if name == "dwell_cam":
        return ("REDESIGN", "Horn-side face down; cam groove opens upward", "moderate",
                "Original two M2 horn screws; captured integral peg",
                "Servo horn and right bed peg; same 0–160 degree law and 16 mm lift",
                "Original horn fasteners only; permanent bridge pilots need no removal")
    if name == "floor_anchor_coupon":
        return ("SPLIT", "Flat anchor top down; pockets and curved-side sample upward", "moderate",
                "Separate production-orientation gauge; not installed in suitcase",
                "Production anchor nut tunnels and tapered epoxy-key chamber",
                "Sample M3/M4 nuts and temporary screws; no assembly hardware")
    if name == "fit_coupon":
        return ("REDESIGN", "Broad coupon underside down; peg upward", "moderate",
                "Hand fit only; not installed in suitcase", "Groove coupon and sample nuts/screws",
                "Test existing M3/M4 nuts and screws; reprint to correct fit")
    if name == "follower_groove_coupon":
        return ("KEEP", "Groove bottom down; groove open upward", "easy",
                "Hand fit only", "Fit coupon integral peg", "None")
    if name in ("bank_cover", "control_cover", "right_front_cover", "right_back_cover"):
        return ("ROTATE", "Broad outer roof on bed; skirt upward", "easy",
                "Removable M3 roof screws; nonstructural textile panel",
                "Corresponding carrier uprights; original switches remain accessible",
                "2 original M3 roof screws per cover; textile installed after fitting")
    if name == "usb_park_clip":
        return ("ROTATE", "Outer clip side on bed; open jaws rise sideways", "easy",
                "Original frame M3 screw; removable USB plug", "Bank service cable and frame", "1 original M3x6 + M3 nut")
    if name.startswith("capacitor_holder_"):
        return ("REDESIGN", "Reinforced foot flat on bed; lead reliefs vertical", "moderate",
                "M3 foot screw; ramped snap lips and original tie", "Frame and unchanged D10/H17 capacitor",
                "1 original M3 mounting screw/nut and capacitor tie per holder")
    if name.startswith(("left_service_carrier_", "right_service_carrier_")):
        from layout import service_components
        joints=[joint for joint in service_components()[1] if name in (joint["frame"],joint["tier"])]
        hardware="; ".join(f"{joint['bolt']} + {joint['nut']} ({joint['id']})" for joint in joints)
        return ("SPLIT", "Side-frame foot or tier plinth flat on bed", "moderate",
                "Two rectangular alignment keys per clamp; 0.25 mm clearance; roofed nut bay and screw clamp",
                "Other cassette frame/tiers, original boards, removable cover and open service passages",
                hardware+"; original deck screws and component ties remain; left tier 2 clamp before closing tier 1, remove tier 1 for middle-clamp service")
    if name=="camera_angle" or name.startswith("camera_post_"):
        return ("SPLIT", "Outer X side face down" if name.startswith("camera_post_") else "Flat platform floor down",
                "moderate", "Two broad boss/tab seats; vertical M3 clamps; nuts bench-loaded from inboard sides",
                "Other camera parts, unchanged camera pose and original rear frame mount axes",
                "Per camera assembly: 2 new M3x10 ISO7380 + 2 M3 ISO4032 nuts; original rear mounting screws and ties")
    if name == "switch_shelf" or name.startswith("switch_frame_"):
        contact="Flat controls plate underside down" if name=="switch_shelf" else (
            "Outer left X15.9 face down" if name.endswith("left") else "Outer right X70.5 face down")
        return ("SPLIT",contact,"moderate",
                "Four bolted corner seats; rear-right nut mouth intentionally open; removable plate",
                "Other two controls parts, original switches, control cover and independent frame mounting",
                "Per controls assembly: 2 new M3x10 ISO7380 + 2 M3x10 DIN7991 countersunk + 4 M3 ISO4032 nuts; original two deck mounts and cover screws")
    if name in ("bank_holder", "usb_coil_cradle") or name.startswith("carrier_"):
        return ("REDESIGN", "Flat carrier feet/underside down", "moderate",
                "Independent original frame screws; positive component stops and ties",
                "Unchanged purchased component envelope, wiring, frame and removable cover",
                "Original component mounting screws, nuts and ties; see mechanical BOM and guide")
    raise ValueError(f"Missing explicit manufacturing contract for {name}")


def is_structural(name):
    return (name.startswith(("frame_panel_", "floor_anchor_", "wand_bed_"))
            or name in ("lift_base", "dwell_cam", "fit_coupon", "floor_anchor_coupon", "follower_groove_coupon"))


def validate_profile(text, contract):
    expected={"printer_technology":"FFF","bed_shape":"0x0,220x0,220x220,0x220",
              "max_print_height":"250","nozzle_diameter":"0.4","layer_height":"0.2",
              "first_layer_height":"0.2","filament_type":"PETG",
              "support_material":"0","support_material_auto":"0",
              "support_material_enforce_layers":"0","raft_layers":"0","brim_width":str(contract.get("brim_mm",0)),"skirts":"0",
              "perimeters":str(contract["perimeters"]),"fill_density":contract["infill"],
              "fill_pattern":contract["infill_pattern"],"top_solid_layers":"5","bottom_solid_layers":"5",
              "start_filament_gcode":'""',"end_filament_gcode":'""'}
    expected["bridge_angle"]=str(contract.get("bridge_angle_degrees",0))
    actual=profile_values(text)
    if any(actual.get(key)!=value for key,value in expected.items()):
        raise ValueError("Actual print profile contradicts manufacturing contract")
    if is_structural(contract["id"]) and (contract["infill"]!="100%" or contract["perimeters"]<6):
        raise ValueError("Structural print profile must retain full CAD sections")


def record(part, out):
    strategy, contact, difficulty, joint, neighbors, hardware = _contract(part.name)
    solid = is_structural(part.name)
    from layout import membrane_locations, print_finishing
    finishing=membrane_locations(part.name)
    brim=3 if part.name in ("carrier_microphone","left_service_carrier_frame_left","left_service_carrier_frame_right") else 0
    perimeters, density, pattern = (6, "100%", "rectilinear") if solid else (4, "40%", "gyroid")
    profile_name = f"profiles/{part.name}.ini"
    settings=profile_values(BASE_PROFILE.read_text(encoding="utf-8"))
    # Prusa auto-direction can bridge sleeves and tie tunnels along their long axis.
    # The prescribed direction must still pass the actual slicing gate.
    bridge_angle=90 if part.name.startswith("wand_bed_") or part.name in (
        "carrier_mcp23017","carrier_pca9685","carrier_pixel_shifter",
        "bank_holder","carrier_speaker_right") else 180 if (
        part.name.startswith(("floor_anchor_","capacitor_holder_"))) else 0
    settings.update({"perimeters":str(perimeters),"fill_density":density,"fill_pattern":pattern,
                     "bridge_angle":str(bridge_angle),"brim_width":str(brim)})
    text = "# Generated per-part PETG audit profile; not machine-ready G-code.\n"
    text += "\n".join(f"{key} = {value}" for key,value in settings.items())+"\n"
    validate_profile(text,{"id":part.name,"perimeters":perimeters,"infill":density,"infill_pattern":pattern,
                          "bridge_angle_degrees":bridge_angle,"brim_mm":brim})
    (out / "profiles").mkdir(exist_ok=True)
    (out / profile_name).write_bytes(text.encode("utf-8"))
    return {"id": part.name, "stl": f"stl/{part.name}.stl", "project_3mf": f"3mf/{part.name}.3mf",
            "quantity": part.quantity, "strategy": strategy, "material": "PETG",
            "orientation": "Already placed on Z=0 in STL and 3MF; do not auto-rotate",
            "cad_rotation": part.rotation, "build_plate_face": contact,
            "nozzle_mm": .4, "layer_height_mm": .2, "perimeters": perimeters,
            "infill": density, "infill_pattern": pattern, "bridge_angle_degrees":bridge_angle,"supports": "NONE",
            "brim_mm": brim, "brim_reason": "Prusa low-bed-adhesion warning for a tall narrow footprint" if brim else "Not required by selected profile",
            "raft_layers": 0, "difficulty": difficulty,
            "hardware": hardware, "connection": joint, "neighbors": neighbors,
            "postprocessing":print_finishing(part.name),"shaft_membrane_count":len(finishing),
            "shaft_membranes_native_coordinates":finishing,
            "finishing_and_purpose": part.reason, "slicer_profile": profile_name,
            "slicer_profile_sha256": hashlib.sha256(text.encode("utf-8")).hexdigest()}


def export_records(out, parts):
    rows = [part["manufacturing"] for part in parts]
    fields = list(rows[0])
    with (out / "printing-manifest.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows({key:json.dumps(value,ensure_ascii=False,separators=(',',':'))
                          if isinstance(value,(dict,list,tuple)) else value for key,value in row.items()}
                         for row in rows)
    (out / "support-free-petg.ini").write_bytes(BASE_PROFILE.read_bytes())


def analyze_parts(out, printed_parts):
    reports = []
    for part, shape in printed_parts:
        report = analyze(shape, ANALYTICAL_PROFILE)
        reports.append({"name": part.name,
                        "stl_sha256": hashlib.sha256((out / "stl" / f"{part.name}.stl").read_bytes()).hexdigest(),
                        **report})
        print(f"Printability {part.name}: {report['status']}", flush=True)
    result = {"schema": 1, "status": "PASS_ANALYTICAL" if all(r["status"] == "PASS" for r in reports) else "FAIL_ANALYTICAL",
              "manifest_sha256": hashlib.sha256((out / "manifest.json").read_bytes()).hexdigest(),
              "parts": reports, "physical_print_performed": False,
              "profile_note": "Generic 220x220x250 mm FDM, PETG, 0.4 mm nozzle; no identified owner printer preset"}
    (out / "printability.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    if result["status"] != "PASS_ANALYTICAL":
        # Staging is removed on failure; retain bounded, actionable evidence
        # in the caller's log rather than referring to a deleted directory.
        failed=[{'name':part['name'],'issues':part['issues'][:2]}
                for part in reports if part['status']!='PASS']
        raise ValueError("Support-free geometric gate failed: "+json.dumps(failed))
    return result
