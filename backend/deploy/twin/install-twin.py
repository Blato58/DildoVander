#!/usr/bin/env python3
"""Install an already verified Twin release. Root-only; no provider or hardware calls."""
import argparse
import copy
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
    mandatory = {"backend", "backend.deps.json", "backend.runtimeconfig.json", "libe_sqlite3.so", "native-twin", "viewer.html", "twin.manifest.json", "kufr-twin.service", "twin-locations.conf", "install-twin.py"}
    actual = {str(path.relative_to(release)) for path in release.rglob("*") if path.is_file() and path.name != "twin-release.json"}
    if not mandatory.issubset(manifest["files"]) or actual != set(manifest["files"]) or any(path.is_symlink() for path in release.rglob("*")):
        raise ValueError("Release inventory must be complete, exact and free of symlinks")
    for name, expected in manifest["files"].items():
        path = release / name
        if path.is_symlink() or not path.is_file() or release not in path.resolve().parents or digest(path) != expected:
            raise ValueError("Invalid release bytes: " + name)
    return manifest


def read_provisioned_devices(root):
    # Match the existing backend's environment-file precedence without importing
    # unrelated secrets or treating an unrequested identity as an allowlist.
    environment = {}
    for name in ("backend.env", "device-identities.env"):
        path = root / name
        if not path.exists():
            continue
        for line in path.read_text().splitlines():
            if "=" not in line or line.lstrip().startswith("#"):
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            if re.fullmatch(r"Devices__\d+__(DeviceId|CredentialSha256)", key):
                environment[key] = value.strip().strip('"').strip("'")
    return [{"DeviceId": device_id,
             "CredentialSha256": environment.get(key.removesuffix("DeviceId") + "CredentialSha256", "")}
            for key, device_id in environment.items() if key.endswith("__DeviceId")]


def merge_live_devices(config, requested, provisioned):
    """Validate the complete requested merge before changing even the input dict."""
    for device_id in requested:
        if not isinstance(device_id, str) or not re.fullmatch(r"[a-zA-Z0-9._-]{1,64}", device_id) or \
                device_id in (".", "..", "debug-server-admin") or device_id.startswith("sim-"):
            raise ValueError("Invalid requested physical device identity")
    merged = copy.deepcopy(config)
    devices = merged.get("Devices")
    instances = merged.get("DigitalTwin", {}).get("Instances")
    if not isinstance(devices, list) or not all(isinstance(device, dict) for device in devices) or \
            not isinstance(instances, dict) or not all(isinstance(instance, dict) for instance in instances.values()):
        raise ValueError("Existing device configuration must contain a Devices array and Instances object")
    for device_id in dict.fromkeys(requested):
        candidates = [device for device in provisioned if device.get("DeviceId") == device_id]
        if len(candidates) != 1:
            raise ValueError("Requested physical identity is missing or ambiguously provisioned")
        credential = candidates[0].get("CredentialSha256")
        if not isinstance(credential, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", credential):
            raise ValueError("Requested physical identity has an invalid provisioned credential hash")
        existing = [device for device in devices if device.get("DeviceId") == device_id]
        if len(existing) > 1:
            raise ValueError("Requested physical identity collides with duplicate configured devices")
        if existing and str(existing[0].get("CredentialSha256", "")).lower() != credential.lower():
            raise ValueError("Requested physical identity conflicts with its existing credential")
        if any(device.get("DeviceId") != device_id and
               str(device.get("CredentialSha256", "")).lower() == credential.lower() for device in devices):
            raise ValueError("Requested physical credential is already assigned to another device")
        if device_id in instances and (instances[device_id].get("Mode") != "live" or
                                      instances[device_id].get("DeviceId") != device_id):
            raise ValueError("Requested physical identity collides with another instance")
        if any(name != device_id and instance.get("DeviceId") == device_id for name, instance in instances.items()):
            raise ValueError("Requested physical device is already assigned to another instance")
        if not existing:
            devices.append({"DeviceId": device_id, "CredentialSha256": credential})
        if device_id not in instances:
            instances[device_id] = {"Mode": "live", "DeviceId": device_id}
    return merged


def initial_configuration(origin, data_root, build_id, cad_revision):
    credentials = {role: secrets.token_hex(32) for role in ("observe", "control", "integration")}
    config = {
        "Backend": {"DatabasePath": str(data_root / "backend.sqlite"), "Dialog": {"Mode": "fake"}, "Memory": {"Enabled": False}},
        "Devices": [{"DeviceId": "sim-integration", "CredentialSha256": hashlib.sha256(secrets.token_bytes(32)).hexdigest()}],
        "DigitalTwin": {"Origin": origin, "DataRoot": str(data_root),
            "ViewerPath": "/opt/kufr-twin/current/viewer.html", "IntegrationBackendUrl": "http://127.0.0.1:5082/",
            "Instances": {name: {"Mode": "simulation", "DeviceId": name, "Profile": profile,
                "Executable": "/opt/kufr-twin/current/native-twin", "BuildId": build_id, "CadManifestRevision": cad_revision}
                for name, profile in (("sim-development", "deterministic"), ("sim-integration", "integration"))},
            "Credentials": [{"Role": "observe", "Sha256": hashlib.sha256(credentials["observe"].encode()).hexdigest()}]
        }
    }
    for role, instance in (("control", "sim-development"), ("integration", "sim-integration")):
        config["DigitalTwin"]["Credentials"].append({"Role": "control", "InstanceId": instance,
            "Mode": "simulation", "Sha256": hashlib.sha256(credentials[role].encode()).hexdigest()})
    return config, credentials


def reuse_provider_source(config, path):
    """Select an existing source; never copy a key or production configuration."""
    merged = copy.deepcopy(config)
    if path is None:
        return merged
    if not path.is_absolute() or path.is_symlink() or not path.is_file() or path.stat().st_size > 65536:
        raise ValueError("Provider source must be a bounded existing absolute regular file")
    root = Path(merged["DigitalTwin"]["DataRoot"])
    database = Path(merged["Backend"]["DatabasePath"])
    if not root.is_absolute() or not database.is_absolute() or root.resolve() not in database.resolve().parents or \
            merged["Backend"].get("Memory", {}).get("Enabled", False):
        raise ValueError("Provider source requires isolated database and disabled guest memory")
    values = [line.split("=", 1)[1].strip().strip('"').strip("'")
              for line in path.read_text().splitlines() if line.startswith("OPENAI_API_KEY=")]
    if len(values) != 1 or not values[0] or any(ord(byte) < 32 for byte in values[0]):
        raise ValueError("Provider source does not contain one usable credential")
    merged["DigitalTwin"]["ProviderEnvFile"] = str(path)
    merged["DigitalTwin"]["ExternalServicesAuthorized"] = True
    return merged


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--live-device-id", action="append", default=[], help="Explicit physical identity allowlist; never import old simulators")
    parser.add_argument("--reuse-provider-env", type=Path, help="Explicit existing server key source; no secret copied and no API call")
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
    config_root = Path("/etc/kufr-twin")
    data_root = Path("/var/lib/kufr-twin")
    config_path = config_root / "config.json"
    managed = (config_root, data_root, config_root / "config.json", config_root / "operator-credentials.json",
               Path("/etc/systemd/system/kufr-twin.service"), Path("/etc/nginx/snippets/dildovander-twin.conf"))
    if any(path.is_symlink() for path in managed):
        raise SystemExit("Refusing symlinked managed Twin targets")
    credentials = None
    if config_path.exists():
        config = json.loads(config_path.read_text())
    else:
        config, credentials = initial_configuration(args.origin, data_root, manifest["buildId"], cad_revision)
    provisioned = read_provisioned_devices(Path("/etc/kufr-backend")) if args.live_device_id else []
    config = merge_live_devices(config, args.live_device_id, provisioned)
    config = reuse_provider_source(config, args.reuse_provider_env)
    for instance in config["DigitalTwin"]["Instances"].values():
        if instance.get("Mode") == "simulation":
            instance["BuildId"] = manifest["buildId"]
            instance["CadManifestRevision"] = cad_revision
    # Requested identities are fully validated before chmod, mkdir, backup or
    # any persistent configuration/service mutation, for fresh and upgrade paths.
    for name in ("backend", "native-twin"):
        (release / name).chmod(0o755)
    import pwd
    account = pwd.getpwnam("kufr-backend")
    config_root.mkdir(mode=0o750, exist_ok=True)
    data_root.mkdir(mode=0o750, exist_ok=True)
    os.chown(config_root, 0, account.pw_gid)
    os.chown(data_root, account.pw_uid, account.pw_gid)
    backup = Path("/var/backups") / ("kufr-twin-" + str(time.time_ns()))
    backup.mkdir(mode=0o700)
    site = Path("/etc/nginx/sites-available/kufr-ip")
    unit = Path("/etc/systemd/system/kufr-twin.service")
    snippet = Path("/etc/nginx/snippets/dildovander-twin.conf")
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
        if credentials is not None:
            private_credentials = config_root / "operator-credentials.json"
            private_credentials.write_text(json.dumps(credentials, indent=2) + "\n")
            private_credentials.chmod(0o600)
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
