"""Private physical USB service client. Secrets never enter CLI arguments or logs."""
import argparse
import getpass
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import time


RESULTS = {"saved", "invalid-request", "invalid-identity", "invalid-ota", "nvs-failed", "pop-exists", "identity-exists", "window-closed"}


def host_valid(host):
    return isinstance(host, str) and 0 < len(host) <= 253 and all(
        re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label)
        for label in host.split("."))


def make_request(pop, backend_host=None, device_id=None, credential=None, ota=None,
                 replace_pop=False, replace_identity=False):
    if not isinstance(pop, str) or not re.fullmatch(r"[0-9a-f]{32}", pop):
        raise ValueError("PoP must be 32 lowercase hex characters")
    request = {"physicalConfirmation": "ACTUATORS_OFF_USB_SERVICE", "pop": pop,
               "replacePoP": replace_pop, "replaceIdentity": replace_identity}
    if backend_host is not None:
        if not host_valid(backend_host):
            raise ValueError("Invalid backend hostname")
        request["backendHost"] = backend_host
    if (device_id is None) != (credential is None):
        raise ValueError("Device ID and backend credential must be supplied together")
    if device_id is not None:
        if not isinstance(device_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", device_id):
            raise ValueError("Invalid device ID")
        if not isinstance(credential, str) or not re.fullmatch(r"[0-9a-f]{64}", credential) or set(credential) == {"0"}:
            raise ValueError("Invalid backend credential")
        request.update(deviceId=device_id, credential=credential)
    if ota is not None:
        if not isinstance(ota, dict) or set(ota) != {"origin", "channel", "trustPem", "automaticInstall", "checkIntervalSeconds"}:
            raise ValueError("Invalid OTA fields")
        origin = ota["origin"]
        if not isinstance(origin, str) or not origin.startswith("https://") or len(origin) > 267:
            raise ValueError("OTA requires HTTPS origin")
        hostname = origin[8:]
        if hostname.endswith(":443"):
            hostname = hostname[:-4]
        if not host_valid(hostname) or ota["channel"] not in {"stable", "development"}:
            raise ValueError("Invalid OTA origin or channel")
        pem = ota["trustPem"]
        if not isinstance(pem, str) or len(pem.encode("utf-8")) > 1024 or "PRIVATE KEY" in pem or not pem.startswith("-----BEGIN PUBLIC KEY-----"):
            raise ValueError("OTA trust must be a public PEM; device verifies P-256 type")
        if type(ota["automaticInstall"]) is not bool or type(ota["checkIntervalSeconds"]) is not int or not 60 <= ota["checkIntervalSeconds"] <= 86400:
            raise ValueError("Invalid OTA policy")
        request["ota"] = ota
    if type(replace_pop) is not bool or type(replace_identity) is not bool:
        raise ValueError("Replacement flags must be booleans")
    frame = json.dumps(request, separators=(",", ":"), ensure_ascii=True).encode("ascii") + b"\n"
    if len(frame) > 4096:
        raise ValueError("Service frame exceeds limit")
    return frame


def private_receipt(path, content):
    path = Path(path).resolve()
    repository = Path(__file__).resolve().parents[4]
    if path.is_relative_to(repository):
        raise ValueError("Private receipt must be outside the repository")
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        if os.name == "nt":
            principal = f'{os.environ.get("USERDOMAIN", ".")}\\{os.environ["USERNAME"]}'
            result = subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r", f"{principal}:(F)"],
                                    capture_output=True, check=False)
            if result.returncode != 0:
                raise RuntimeError("Cannot set private receipt permissions")
        with os.fdopen(descriptor, "w", encoding="utf-8") as receipt:
            descriptor = None
            json.dump(content, receipt, indent=2)
            receipt.write("\n")
    except BaseException:
        if descriptor is not None:
            os.close(descriptor)
        path.unlink(missing_ok=True)
        raise


def exchange(connection, frame, clock=time.monotonic):
    deadline = clock() + 15
    ready = False
    while clock() < deadline:
        line = connection.readline(128)
        if line == b"KUFR_SERVICE_READY\n":
            ready = True
            break
    if not ready:
        raise RuntimeError("Physical USB service image did not become ready")
    if connection.write(frame) != len(frame):
        raise RuntimeError("USB service frame could not be sent")
    connection.flush()
    deadline = clock() + 30
    while clock() < deadline:
        line = connection.readline(128)
        if line.startswith(b"KUFR_SERVICE_RESULT "):
            result = line.removeprefix(b"KUFR_SERVICE_RESULT ").strip().decode("ascii", errors="replace")
            if result not in RESULTS:
                raise RuntimeError("Invalid service result; response suppressed")
            return result
    raise RuntimeError("USB service result timed out; configuration outcome unknown")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, help="Physical native USB serial port, e.g. COM8")
    parser.add_argument("--receipt", type=Path, required=True, help="New private receipt file outside the repository")
    args = parser.parse_args()
    if not sys.stdin.isatty():
        raise SystemExit("Interactive terminal required to keep secret entry private")
    if input("Actuator supply physically OFF, service image installed and USB trusted? Type SERVICE: ") != "SERVICE":
        raise SystemExit("No configuration sent")
    pop = getpass.getpass("32 hex PoP (blank generates new random PoP stored privately): ") or secrets.token_hex(16)
    backend = input("Backend hostname (blank keeps existing): ").strip() or None
    device_id = input("Backend device ID (blank keeps existing identity): ").strip() or None
    credential = getpass.getpass("Existing backend-provisioned 64 hex device credential: ") if device_id else None
    replace_pop = input("Replace an existing BLE PoP? Type REPLACE (otherwise preserve): ") == "REPLACE"
    replace_identity = bool(device_id) and input("Replace an existing backend identity? Type REPLACE: ") == "REPLACE"
    ota = None
    if input("Configure OTA public trust/origin? Type OTA: ") == "OTA":
        origin = input("HTTPS origin without path: ").strip()
        channel = input("Channel stable/development [stable]: ").strip() or "stable"
        trust = Path(input("P-256 PUBLIC key PEM file: ")).read_text(encoding="utf-8")
        automatic = input("Enable automatic install at safe idle? Type ENABLE: ") == "ENABLE"
        interval = int(input("Check interval seconds [3600]: ").strip() or "3600")
        ota = dict(origin=origin, channel=channel, trustPem=trust, automaticInstall=automatic, checkIntervalSeconds=interval)
    frame = make_request(pop, backend, device_id, credential, ota, replace_pop, replace_identity)
    # Prepare the owner-only receipt before sending; a crash must not lose new PoP.
    private_receipt(args.receipt, {"deviceId": device_id, "proofOfPossession": pop,
                                 "serviceUuid": "b97a7f10-70e8-4b98-9dcc-40d66395c821",
                                 "note": "Private setup credential; confirm saved USB result before use"})
    import serial  # Installed by the existing PlatformIO Python environment.
    connection = serial.Serial(port=None, baudrate=115200, timeout=1, write_timeout=5)
    connection.dtr = False
    connection.rts = False
    connection.port = args.port
    try:
        connection.open()
        result = exchange(connection, frame)
    finally:
        connection.close()
        del frame, credential, pop
    if result != "saved":
        raise SystemExit(f"Service returned {result}; private receipt retained. Some requested fields may have been saved; inspect before retry.")
    print("Configuration saved. Restore the main application image; use the private receipt for iPhone PoP.")


if __name__ == "__main__":
    main()
