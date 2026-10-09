"""Validate measured configuration and emit an ignored build header; never flash."""
import argparse
import json
from pathlib import Path


def generate(config):
    keys = {"profile", "revision", "profile_b_ok", "servos"}
    if set(config) not in (keys, keys | {"camera_verified"}) or config["profile"] != "B-effects":
        raise ValueError("Expected the explicit B-effects profile and exact fields")
    flags = [config["profile_b_ok"], config.get("camera_verified", False)]
    if any(type(flag) is not bool for flag in flags):
        raise ValueError("Verification flags must be booleans")
    if flags[1] and not flags[0]:
        raise ValueError("Camera requires verified Profile B wiring")
    revision = config["revision"]
    if type(revision) is not int or not 1 <= revision <= 2**64 - 1:
        raise ValueError("Calibration revision must be a positive uint64")
    servos = config["servos"]
    if not isinstance(servos, list) or len(servos) != 6:
        raise ValueError("Exactly six measured servo records are required")
    values = []
    for servo in servos:
        if not isinstance(servo, dict) or set(servo) != {"rest_us", "raised_us", "travel_ms"}:
            raise ValueError("Invalid servo record")
        rest, raised, travel = (servo[key] for key in ("rest_us", "raised_us", "travel_ms"))
        if any(type(value) is not int for value in (rest, raised, travel)):
            raise ValueError("Servo values must be integers")
        valid = 500 <= rest <= 2500 and 500 <= raised <= 2500 and rest != raised and 20 <= travel <= 3000
        if not valid and not (not any(flags) and (rest, raised, travel) == (0, 0, 0)):
            raise ValueError("Use measured 500-2500us endpoints and 20-3000ms travel; zero is only for disabling")
        for value in (rest, raised, travel):
            values.extend((value & 255, value >> 8))
    return ("#pragma once\n#include <stdint.h>\n"
            + "constexpr uint8_t ServiceFlags[] = {" + ",".join(str(int(v)) for v in flags) + "};\n"
            + f"constexpr uint64_t ServiceRevision = UINT64_C({revision});\n"
            + "constexpr uint8_t ServiceServos[] = {" + ",".join(map(str, values)) + "};\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("--output", type=Path, default=Path(__file__).parent / "src/service_config.h")
    args = parser.parse_args()
    content = generate(json.loads(args.config.read_text(encoding="utf-8")))
    args.output.write_text(content, encoding="utf-8")
    print(f"Validated build header: {args.output}; no upload performed")
