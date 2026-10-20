#!/usr/bin/env python3
"""Root-owned, narrowly scoped activation helper for the Ubuntu runner."""

import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path


BASE = Path("/opt/kufr-backend")
RELEASES = BASE / "releases"
CURRENT = BASE / "current"
ENV_FILE = Path("/etc/kufr-backend/backend.env")
SERVICE = "kufr-backend.service"
HEALTH_URL = "http://127.0.0.1:5080/healthz"
RELEASE_PATTERN = re.compile(r"[0-9a-f]{40}-[0-9]+-[0-9]+\Z")
DEVICE_PATTERN = re.compile(r"[A-Za-z0-9._-]{1,128}\Z")
KEY_PATTERN = re.compile(r"sk-[A-Za-z0-9_-]+\Z")


def checked_text(value, pattern, name):
    if not isinstance(value, str) or not pattern.fullmatch(value):
        raise ValueError(f"Invalid {name}")
    return value


def make_environment(payload):
    if not isinstance(payload, dict):
        raise ValueError("Invalid deployment configuration")

    device_id = payload.get("deviceId", "")
    credential_hash = payload.get("credentialSha256", "")
    api_key = payload.get("openAiApiKey", "")
    if bool(device_id) != bool(credential_hash):
        raise ValueError("Device ID and credential hash must be supplied together")
    if device_id:
        checked_text(device_id, DEVICE_PATTERN, "device ID")
        checked_text(credential_hash, re.compile(r"[0-9a-fA-F]{64}\Z"), "credential hash")
    if api_key:
        checked_text(api_key, KEY_PATTERN, "OpenAI API key")

    lines = [
        "ASPNETCORE_URLS=http://127.0.0.1:5080",
        "Backend__DatabasePath=/var/lib/kufr-backend/journal.sqlite",
        "Backend__Firmware__Directory=/opt/kufr-firmware",
    ]
    if device_id:
        lines.extend((f"Devices__0__DeviceId={device_id}",
                      f"Devices__0__CredentialSha256={credential_hash}"))
    if api_key:
        lines.append(f"OPENAI_API_KEY={api_key}")

    # Legacy software monetary caps are intentionally ignored. OpenAI owns spending limits.
    return ("\n".join(lines) + "\n").encode("ascii")


def write_environment(contents):
    import grp

    ENV_FILE.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=ENV_FILE.parent, delete=False) as temporary:
        temporary.write(contents)
        temporary_path = Path(temporary.name)
    try:
        os.chown(temporary_path, 0, grp.getgrnam("kufr-backend").gr_gid)
        os.chmod(temporary_path, 0o640)
        os.replace(temporary_path, ENV_FILE)
    finally:
        temporary_path.unlink(missing_ok=True)


def switch_release(target):
    temporary_link = BASE / f".current-{os.getpid()}"
    try:
        temporary_link.symlink_to(target)
        os.replace(temporary_link, CURRENT)
    finally:
        temporary_link.unlink(missing_ok=True)


def wait_for_health():
    for _ in range(40):
        if subprocess.run(("systemctl", "is-active", "--quiet", SERVICE), check=False).returncode != 0:
            time.sleep(0.5)
            continue
        try:
            with urllib.request.urlopen(HEALTH_URL, timeout=1) as response:
                if response.status == 200:
                    return True
        except (OSError, urllib.error.URLError):
            pass
        time.sleep(0.5)
    return False


def activate(release_id):
    if not RELEASE_PATTERN.fullmatch(release_id):
        raise ValueError("Invalid release ID")
    release = RELEASES / release_id
    if not release.is_dir() or release.resolve().parent != RELEASES:
        raise ValueError("Release directory is missing or invalid")
    if not (release / "backend").is_file() or not os.access(release / "backend", os.X_OK):
        raise ValueError("Release executable is missing")

    payload = json.loads(sys.stdin.read(4097))
    new_environment = make_environment(payload)
    if CURRENT.exists() and not CURRENT.is_symlink():
        raise ValueError("Current release is not a symlink")
    previous_release = CURRENT.resolve() if CURRENT.is_symlink() else None
    previous_environment = ENV_FILE.read_bytes() if ENV_FILE.exists() else None

    try:
        write_environment(new_environment)
        switch_release(release)
        subprocess.run(("systemctl", "restart", SERVICE), check=True)
        if not wait_for_health():
            raise RuntimeError("Backend health check failed")
    except Exception:
        if previous_environment is None:
            ENV_FILE.unlink(missing_ok=True)
        else:
            write_environment(previous_environment)
        if previous_release is None:
            CURRENT.unlink(missing_ok=True)
            subprocess.run(("systemctl", "stop", SERVICE), check=False)
        else:
            switch_release(previous_release)
            subprocess.run(("systemctl", "restart", SERVICE), check=False)
            wait_for_health()
        raise
    print(f"Activated {release_id}; loopback health check passed")


if __name__ == "__main__":
    try:
        if len(sys.argv) != 3 or sys.argv[1] != "activate":
            raise ValueError("Usage: kufr-backend-deploy activate RELEASE_ID")
        activate(sys.argv[2])
    except Exception as exception:
        print(f"Deployment failed: {exception}", file=sys.stderr)
        sys.exit(1)
