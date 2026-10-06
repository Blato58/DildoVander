"""Explicit runner CLI provisioning; persist resource IDs, never guest records.

The service's SQLite remains private to its system user. This runner journal
contains only provisioning metadata and is outside the cleaned checkout/temp.
The same IDs are read into release configuration without wider sudo rights.
"""
import argparse
import json
import os
import re
import sqlite3
import stat
import subprocess
from pathlib import Path


STORE = re.compile(r"vs_[A-Za-z0-9_-]{1,125}\Z")
SETTINGS = {"personal": "memory_personal_store", "motifs": "memory_motif_store"}
NAMESPACE = "kufr"
ENVIRONMENT = "Production"


def provisioning_directory(workspace=None):
    workspace = workspace or os.environ["GITHUB_WORKSPACE"]
    return Path(workspace).resolve().parent / ".kufr-memory-provisioning-production"


def validate_private(path, directory=False):
    info = path.lstat()
    expected = stat.S_ISDIR if directory else stat.S_ISREG
    if not expected(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise ValueError("Provisioning metadata must be private, owned and not a symlink")


def read_stores(directory):
    result = {"personal": "", "motifs": ""}
    if not directory.exists():
        if directory.is_symlink():
            raise ValueError("Invalid provisioning directory")
        return result
    validate_private(directory, directory=True)
    database = directory / "journal.sqlite"
    if not database.exists():
        if database.is_symlink():
            raise ValueError("Invalid provisioning journal")
        return result
    validate_private(database)
    # Read only the two non-secret settings. Never open the guest database.
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=5) as connection:
        for scope, setting in SETTINGS.items():
            row = connection.execute("SELECT value FROM server_settings WHERE name = ?", (setting,)).fetchone()
            if row:
                if not isinstance(row[0], str) or not STORE.fullmatch(row[0]):
                    raise ValueError("Invalid persisted vector store ID")
                result[scope] = row[0]
    if result["personal"] and result["personal"] == result["motifs"]:
        raise ValueError("Personal and anonymous stores must be separate")
    return result


def resolve_stores(directory, personal="", motifs=""):
    stores = read_stores(directory)
    for scope, supplied in (("personal", personal), ("motifs", motifs)):
        if supplied:
            if not STORE.fullmatch(supplied):
                raise ValueError("Invalid vector store ID")
            if stores[scope] and stores[scope] != supplied:
                # Repointing a populated installation needs a reviewed migration.
                raise ValueError("Store ID conflicts with persistent provisioning; use the existing ID")
            stores[scope] = supplied
    if stores["personal"] and stores["personal"] == stores["motifs"]:
        raise ValueError("Personal and anonymous stores must be separate")
    return stores


def run_provisioning(backend, directory, action, personal="", motifs="", runner=subprocess.run):
    if action not in ("dry-run", "verify", "create"):
        raise ValueError("Invalid provisioning action")
    stores = resolve_stores(directory, personal, motifs)
    if action == "verify" and not all(stores.values()):
        raise ValueError("Both existing store IDs are required for verify")
    directory.mkdir(mode=0o700, exist_ok=True)
    validate_private(directory, directory=True)
    executable = Path(backend).resolve(strict=True)
    command = [str(executable), "--memory-provision"]
    if action == "dry-run":
        command.append("--create")  # Preview creation of missing stores only.
    else:
        command.append("--execute")
        if action == "create":
            command.append("--create")
    environment = os.environ.copy()
    environment.update({
        "ASPNETCORE_ENVIRONMENT": ENVIRONMENT,
        "Backend__DatabasePath": str(directory / "journal.sqlite"),
        "Backend__Memory__AppNamespace": NAMESPACE,
        "Backend__Memory__Environment": ENVIRONMENT,
        "Backend__Memory__PersonalStoreId": stores["personal"],
        "Backend__Memory__MotifStoreId": stores["motifs"],
        "Backend__Memory__Enabled": "false",
        "Backend__Memory__CloudIndexingEnabled": "false",
        "Backend__Memory__LongTermPersonalEnabled": "false",
    })
    if action == "dry-run":
        environment.pop("OPENAI_API_KEY", None)
    elif not environment.get("OPENAI_API_KEY"):
        raise ValueError("OPENAI_API_KEY is required for execution")
    previous_mask = os.umask(0o077)
    try:
        completed = runner(command, cwd=executable.parent, env=environment,
                           capture_output=True, text=True, timeout=145, check=False)
    finally:
        os.umask(previous_mask)
    if completed.returncode:
        # Provider codes are safe; do not echo arbitrary process output/env.
        for line in completed.stderr.splitlines():
            if re.fullmatch(r"Provisioning failed: [A-Za-z0-9_. -]{1,80}", line):
                print(line)
        raise RuntimeError("Provisioning CLI failed (exit " + str(completed.returncode) +
                           "); persisted partial IDs remain available for recovery")
    documents = [json.loads(line) for line in completed.stdout.splitlines() if line.strip()]
    if action == "dry-run":
        if len(documents) != 1 or documents[0].get("mode") != "dry-run" or documents[0].get("networkRequests") != 0:
            raise ValueError("Invalid provisioning dry-run result")
        result = documents[0] | {"storeIds": stores}
    else:
        verified = {r["scope"]: r for r in documents}
        if set(verified) != {"Personal", "EveningMotif"}:
            raise ValueError("Both stores must be verified")
        saved = read_stores(directory)
        for scope, key in (("Personal", "personal"), ("EveningMotif", "motifs")):
            row = verified[scope]
            if row.get("verified") is not True or row.get("exportedRecords") != 0 or row.get("storeId") != saved[key]:
                raise ValueError("Unverified or unpersisted provisioning result")
        result = {"mode": action, "appNamespace": NAMESPACE, "environment": ENVIRONMENT,
                  "storeIds": saved, "verified": True, "exportedRecords": 0}
    print(json.dumps(result))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", required=True)
    parser.add_argument("--action", choices=("dry-run", "verify", "create"), default="dry-run")
    parser.add_argument("--personal-store-id", default="")
    parser.add_argument("--motif-store-id", default="")
    arguments = parser.parse_args()
    run_provisioning(arguments.backend, provisioning_directory(), arguments.action,
                     arguments.personal_store_id, arguments.motif_store_id)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, sqlite3.Error, subprocess.TimeoutExpired) as error:
        raise SystemExit(str(error))
