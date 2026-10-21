"""Digital Twin identity derived from the same inputs as the CAD assembly.

No CAD imports here: preview supplies the authoritative mechanism and mesh names.
Operational servo calibration deliberately does not participate in CAD identity.
"""
import hashlib
import json


def canonical_bytes(value):
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def geometry_digest(meshes):
    """Hash actual generated meshes by stable identity, independent of input order."""
    ordered = sorted(meshes, key=lambda mesh: mesh["name"])
    if len({mesh["name"] for mesh in ordered}) != len(ordered):
        raise ValueError("Duplicate generated CAD mesh identity")
    return hashlib.sha256(canonical_bytes(ordered)).hexdigest()


def build_manifest(parameters, mechanism, effects, component_names, geometry_digest=None):
    mechanical = parameters["mechanism"]
    count = mechanical["count"]
    leds = effects["leds_per_compartment"]
    if count != effects["count"] or count != 6 or leds != 6:
        raise ValueError("Digital Twin v1 requires the B-effects six-slot profile")
    names = set(component_names)
    slots = []
    for index in range(count):
        slot = index + 1
        origin = [mechanical["origin_x"] + mechanical["pitch"] * index,
                  mechanical["origin_y"], mechanical["origin_z"]]
        pivot = [origin[axis] + mechanical["cam_" + axis_name]
                 for axis, axis_name in enumerate(("x", "y", "z"))]
        components = {kind: f"{kind}_{slot}" for kind in ("tray", "cam", "servo", "u059", "led", "wand")}
        if not set(components.values()).issubset(names):
            raise ValueError(f"Missing CAD components for slot {slot}")
        moving = sorted(name for name in names if name.endswith(f"_{slot}") and
                        name.startswith(("tray_", "u059_", "led_", "wand_", "soft_liner_", "tray_foam_", "tray_fabric_")))
        rotating = sorted(name for name in names if name.endswith(f"_{slot}") and
                          name.startswith(("cam_", "stock_horn_", "coupler_")))
        slots.append({"slot": slot, "components": components, "liftComponents": moving,
                      "rotationComponents": rotating, "originMm": origin,
                      "servoPivotMm": pivot, "camPivotMm": pivot, "axis": [1, 0, 0],
                      "pivotEvidence": "Owner-supplied SG90 STEP shaft aligned to CAD cam; physical fit pending",
                      "channels": {"servoPca": index, "vibrationPca": index + 6,
                                   "ledStart": index * leds, "ledCount": leds}})
    samples = [{"angleDegrees": angle, "liftMm": round(mechanism.cam_law(angle)[0] - mechanical["cam_low_radius"], 9)}
               for angle in range(int(mechanical["travel_degrees"]) + 1)]
    manifest = {"schemaVersion": 1, "cadRevision": parameters["revision"], "units": "millimetres",
                "axes": {"x": "module row", "y": "front to rear", "z": "up", "handedness": "right"},
                "hardwareProfile": "B-effects", "slots": slots,
                "audio": {"speakers": sorted(name for name in names if name in parameters.get("components", {}) and name.startswith("speaker_")),
                          "amplifiers": sorted(name for name in names if name in parameters.get("components", {}) and name.startswith("amplifier_")),
                          "evidence": "enqueue/cancel/voice-busy evidence; never audible measurement"},
                "kinematics": {"modelPositionUnits": "normalized", "angleMinDegrees": 0,
                               "angleMaxDegrees": mechanical["travel_degrees"], "liftMaxMm": mechanical["lift"],
                               "samples": samples},
                "provenance": {"parametersHash": hashlib.sha256(canonical_bytes(parameters)).hexdigest(),
                               "geometryDigest": geometry_digest,
                               "effectsLayout": {"count": count, "ledsPerCompartment": leds},
                               "law": "mechanism.cam_law"}}
    manifest["revision"] = hashlib.sha256(canonical_bytes(manifest)).hexdigest()
    return manifest
