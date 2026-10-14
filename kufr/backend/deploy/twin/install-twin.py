#!/usr/bin/env python3
"""Install an already verified Twin release. Root-only; no provider or hardware calls."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import time
import urllib.request


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command(*args):
    subprocess.run(args, check=True)


def verify_release(release):
    manifest = json.loads((release / "twin-release.json").read_text())
    mandatory = {"backend", "native-twin", "viewer.html", "twin.manifest.json", "kufr-twin.service", "twin-locations.conf", "install-twin.py"}
    actual = {str(path.relative_to(release)) for path in release.rglob("*") if path.is_file() and path.name != "twin-release.json"}
    if not mandatory.issubset(manifest["files"]) or actual != set(manifest["files"]) or any(path.is_symlink() for path in release.rglob("*")):
        raise ValueError("Release inventory must be complete, exact and free of symlinks")
    for name, expected in manifest["files"].items():
        path = release / name
        if path.is_symlink() or not path.is_file() or release not in path.resolve().parents or digest(path) != expected:
            raise ValueError("Invalid release bytes: " + name)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--live-device-id", action="append", default=[], help="Explicit physical identity allowlist; never import old simulators")
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise SystemExit("Root required")
    release = args.release.resolve(strict=True)
    if release.parent != Path("/opt/kufr-twin/releases") or not re.fullmatch(r"[a-zA-Z0-9._-]+", release.name):
        raise SystemExit("Release must be a direct immutable /opt/kufr-twin/releases child")
    manifest = verify_release(release)
    cad_revision = json.loads((release / "twin.manifest.json").read_text())["revision"]
    if not re.fullmatch(r"https://[a-zA-Z0-9.-]+", args.origin):
        raise SystemExit("Origin must be the existing HTTPS origin without a path")
    for name in ("backend", "native-twin"):
        (release / name).chmod(0o755)
    import pwd
    account = pwd.getpwnam("kufr-backend")
    config_root = Path("/etc/kufr-twin")
    data_root = Path("/var/lib/kufr-twin")
    managed = (config_root, data_root, config_root / "config.json", config_root / "operator-credentials.json",
               Path("/etc/systemd/system/kufr-twin.service"), Path("/etc/nginx/snippets/dildovander-twin.conf"))
    if any(path.is_symlink() for path in managed):
        raise SystemExit("Refusing symlinked managed Twin targets")
    config_root.mkdir(mode=0o750, exist_ok=True)
    data_root.mkdir(mode=0o750, exist_ok=True)
    os.chown(config_root, 0, account.pw_gid)
    os.chown(data_root, account.pw_uid, account.pw_gid)
    backup = Path("/var/backups") / ("kufr-twin-" + str(time.time_ns()))
    backup.mkdir(mode=0o700)
    site = Path("/etc/nginx/sites-available/kufr-ip")
    unit = Path("/etc/systemd/system/kufr-twin.service")
    snippet = Path("/etc/nginx/snippets/dildovander-twin.conf")
    config_path = config_root / "config.json"
    site_text = site.read_text()
    include = "    include /etc/nginx/snippets/dildovander-twin.con[f];"
    anchor = "    include /etc/nginx/snippets/dildovander-site.con[f];"
    if include not in site_text and site_text.count(anchor) != 1:
        raise SystemExit("Unexpected Nginx layout; no configuration changed")
    for path in (site, unit, snippet, config_path, config_root / "operator-credentials.json"):
        if path.exists():
            shutil.copy2(path, backup / path.name)
    current = Path("/opt/kufr-twin/current")
    temporary = current.with_name("current.new")
    if temporary.exists() or temporary.is_symlink():
        raise SystemExit("Existing current.new requires explicit service inspection")
    previously_active = subprocess.run(["systemctl", "is-active", "--quiet", "kufr-twin.service"]).returncode == 0
    previously_enabled = subprocess.run(["systemctl", "is-enabled", "--quiet", "kufr-twin.service"]).returncode == 0
    previous = current.resolve() if current.is_symlink() else None
    if previous is not None and previous.parent != Path("/opt/kufr-twin/releases"):
        raise SystemExit("Previous Twin release is outside the owned releases directory")
    if current.exists() and not current.is_symlink():
        raise SystemExit("Refusing non-symlink current release")
    try:
        if not config_path.exists():
            credentials = {role: secrets.token_hex(32) for role in ("observe", "control", "integration")}
            config = {
                "Backend": {"DatabasePath": str(data_root / "backend.sqlite"), "Dialog": {"Mode": "fake"}, "Memory": {"Enabled": False}},
                "Devices": [{"DeviceId": "sim-integration", "CredentialSha256": hashlib.sha256(secrets.token_bytes(32)).hexdigest()}],
                "DigitalTwin": {"Origin": args.origin, "DataRoot": str(data_root),
                    "ViewerPath": "/opt/kufr-twin/current/viewer.html", "IntegrationBackendUrl": "http://127.0.0.1:5082/",
                    "Instances": {name: {"Mode": "simulation", "DeviceId": name, "Profile": profile,
                        "Executable": "/opt/kufr-twin/current/native-twin", "BuildId": manifest["buildId"], "CadManifestRevision": cad_revision}
                        for name, profile in (("sim-development", "deterministic"), ("sim-integration", "integration"))},
                    "Credentials": [{"Role": "observe", "Sha256": hashlib.sha256(credentials["observe"].encode()).hexdigest()}]
                }
            }
            for role, instance in (("control", "sim-development"), ("integration", "sim-integration")):
                config["DigitalTwin"]["Credentials"].append({"Role": "control", "InstanceId": instance,
                    "Mode": "simulation", "Sha256": hashlib.sha256(credentials[role].encode()).hexdigest()})
            # Existing device credential hashes are reused only to mint scoped ingest
            # grants. No plaintext device secret or ordinary device token is copied.
            environment = {}
            for name in ("backend.env", "device-identities.env"):
                path = Path("/etc/kufr-backend") / name
                if not path.exists():
                    continue
                for line in path.read_text().splitlines():
                    if "=" in line and not line.lstrip().startswith("#"):
                        key, value = line.split("=", 1)
                        if re.fullmatch(r"Devices__\d+__(DeviceId|CredentialSha256)", key):
                            environment[key] = value.strip().strip('"').strip("'")
            for key, device_id in environment.items():
                if not key.endswith("__DeviceId") or device_id not in args.live_device_id:
                    continue
                hash_value = environment.get(key.removesuffix("DeviceId") + "CredentialSha256", "")
                if not re.fullmatch(r"[a-zA-Z0-9._-]{1,64}", device_id) or not re.fullmatch(r"[0-9a-fA-F]{64}", hash_value):
                    continue
                config["Devices"].append({"DeviceId": device_id, "CredentialSha256": hash_value})
                config["DigitalTwin"]["Instances"][device_id] = {"Mode": "live", "DeviceId": device_id}
            private_credentials = config_root / "operator-credentials.json"
            private_credentials.write_text(json.dumps(credentials, indent=2) + "\n")
            private_credentials.chmod(0o600)
        else:
            config = json.loads(config_path.read_text())
            for instance in config["DigitalTwin"]["Instances"].values():
                if instance["Mode"] == "simulation":
                    instance["BuildId"] = manifest["buildId"]
                    instance["CadManifestRevision"] = cad_revision
        config_path.write_text(json.dumps(config, indent=2) + "\n")
        config_path.chmod(0o640)
        os.chown(config_path, 0, account.pw_gid)
        shutil.copyfile(release / "kufr-twin.service", unit)
        shutil.copyfile(release / "twin-locations.conf", snippet)
        if include not in site_text:
            site.write_text(site_text.replace(anchor, anchor + "\n" + include))
        temporary.symlink_to(release)
        temporary.replace(current)
        command("systemctl", "daemon-reload")
        command("systemctl", "enable", "kufr-twin.service")
        command("systemctl", "restart", "kufr-twin.service")
        command("nginx", "-t")
        command("systemctl", "reload", "nginx.service")
        for attempt in range(20):
            try:
                with urllib.request.urlopen("http://127.0.0.1:5082/healthz/twin", timeout=2) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(0.25)
        else:
            raise RuntimeError("Twin health failed")
        with urllib.request.urlopen(args.origin + "/twin", timeout=10) as response:
            if response.status != 200:
                raise RuntimeError("TLS viewer readback failed")
        with urllib.request.urlopen("http://127.0.0.1:5080/healthz", timeout=5) as response:
            if response.status != 200:
                raise RuntimeError("Existing backend health failed")
    except Exception:
        temporary.unlink(missing_ok=True)
        for path in (site, unit, snippet, config_path):
            saved = backup / path.name
            if saved.exists():
                shutil.copy2(saved, path)
            elif path in (unit, snippet):
                path.unlink(missing_ok=True)
        if previous:
            temporary.symlink_to(previous)
            temporary.replace(current)
        else:
            current.unlink(missing_ok=True)
        command("systemctl", "daemon-reload")
        subprocess.run(["systemctl", "restart" if previously_active else "stop", "kufr-twin.service"], check=False)
        subprocess.run(["systemctl", "enable" if previously_enabled else "disable", "kufr-twin.service"], check=False)
        command("nginx", "-t")
        command("systemctl", "reload", "nginx.service")
        raise
    print(json.dumps({"deployed": str(release), "backup": str(backup), "health": "ok",
        "operatorCredentialFile": str(config_root / "operator-credentials.json"),
        "physicalVerification": "unavailable"}))


if __name__ == "__main__":
    main()
