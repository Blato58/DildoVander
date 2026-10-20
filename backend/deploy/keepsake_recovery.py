#!/usr/bin/env python3
"""Offline DV10 snapshot and isolated restore verification; never starts a service."""

import argparse
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import tarfile
import tempfile


EVIDENCE_FIELDS = {
    "schemaVersion", "retentionMode", "storeId", "revision", "capturedAtUtc",
    "deletedKeepsakeIds", "revocationEvidenceHashes", "deletionEvidenceHashes", "unresolvedAttemptIds",
    "archiveSha256", "keyRingSha256",
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def require_private_output(path):
    resolved = path.resolve()
    lower = resolved.as_posix().lower()
    if path.is_symlink() or "wwwroot" in [part.lower() for part in resolved.parts] or "releases" in [part.lower() for part in resolved.parts] or lower == "/var/www" or lower.startswith("/var/www/"):
        raise ValueError("Recovery output must not be a public webroot or code release")


def read_evidence(path):
    evidence = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(evidence, dict) or set(evidence) != EVIDENCE_FIELDS:
        raise ValueError("Recovery evidence has an unexpected schema")
    if evidence["schemaVersion"] != 1 or evidence["retentionMode"] != "manual_only":
        raise ValueError("Recovery requires manual_only evidence version 1")
    if not isinstance(evidence["revision"], int) or evidence["revision"] < 0:
        raise ValueError("Invalid evidence revision")
    if not all(isinstance(evidence[name], str) and evidence[name] for name in ("storeId", "capturedAtUtc")):
        raise ValueError("Evidence identity is missing")
    for name in ("deletedKeepsakeIds", "revocationEvidenceHashes", "deletionEvidenceHashes", "unresolvedAttemptIds"):
        values = evidence[name]
        if not isinstance(values, list) or not all(isinstance(value, str) and value for value in values):
            raise ValueError("Evidence entries must contain opaque identifiers, never content")
        if len(values) != len(set(values)):
            raise ValueError("Duplicate evidence entries")
    for name in ("archiveSha256", "keyRingSha256"):
        if not isinstance(evidence[name], dict) or not all(isinstance(path, str) and isinstance(value, str) and len(value) == 64 for path, value in evidence[name].items()):
            raise ValueError("Invalid retained hash inventory")
    return evidence


def file_inventory(root):
    return {path.relative_to(root).as_posix(): digest(path) for path in checked_files(root)}


def row_hashes(rows):
    return sorted(hashlib.sha256(json.dumps(list(row), separators=(",", ":")).encode("utf-8")).hexdigest() for row in rows)


def authority(connection):
    states = connection.execute("SELECT store_id,revision FROM dv10_recovery_state").fetchall()
    if len(states) != 1:
        raise ValueError("Recovery requires exactly one authoritative store identity")
    store_id, revision = states[0]
    revocations = connection.execute("SELECT id,keepsake_id,session_hash,kind,revision,created FROM dv10_revocations ORDER BY revision,id").fetchall()
    if not isinstance(revision, int) or revision < 0 or [row[4] for row in revocations] != list(range(1, revision + 1)):
        raise ValueError("Append-only revocation evidence has a revision gap")
    deletions = connection.execute("SELECT id,keepsake_id,scope,local_state,requested,session_hash FROM dv10_deletions ORDER BY id").fetchall()
    deleted = [row[0] for row in connection.execute("SELECT id FROM dv10_keepsakes WHERE deleted<>0 ORDER BY id")]
    # A cancelled leased attempt may have crossed the dispatch boundary before a crash.
    uncertain = [row[0] for row in connection.execute("SELECT id FROM dv10_jobs WHERE state IN ('dispatched','awaiting_reconciliation') OR (state='cancelled' AND fence>0) ORDER BY id")]
    return {"storeId": store_id, "revision": revision, "deletedKeepsakeIds": deleted,
            "revocationEvidenceHashes": row_hashes(revocations), "deletionEvidenceHashes": row_hashes(deletions),
            "unresolvedAttemptIds": uncertain}


def export_evidence(database, archive, keys, output, quiesced):
    require_private_output(output)
    if not quiesced or output.exists():
        raise ValueError("Evidence export requires quiesced writers and a new output")
    if any(output.resolve().is_relative_to(root.resolve()) for root in (archive, keys)) or output.resolve().parent == database.resolve().parent:
        raise ValueError("Evidence output must stay outside primary archive/database/key paths")
    with closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True)) as connection:
        connection.execute("BEGIN")
        value = authority(connection)
        value.update(schemaVersion=1, retentionMode="manual_only", capturedAtUtc=datetime.now(timezone.utc).isoformat(),
                     archiveSha256=file_inventory(archive), keyRingSha256=file_inventory(keys))
    output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with output.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(value, sort_keys=True) + "\n")
    os.chmod(output, 0o600)
    return value


def checked_files(root):
    if root.is_symlink() or not root.is_dir():
        raise ValueError("Archive must be a real directory")
    files = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError("Symlinks are forbidden in snapshots")
        if path.is_file():
            files.append(path)
        elif not path.is_dir():
            raise ValueError("Special files are forbidden in snapshots")
    return files


def snapshot(database, archive, evidence_path, output, quiesced, keys=None):
    """Caller must pause all DB/archive publishers, deletes and provider ingest."""
    if not quiesced:
        raise ValueError("Pause all writers and acknowledge --writers-quiesced before snapshot")
    require_private_output(output)
    read_evidence(evidence_path)
    if archive.is_symlink():
        raise ValueError("Archive root must not be a symlink")
    database, archive, output = database.resolve(), archive.resolve(), output.resolve()
    if output.exists() or output.is_relative_to(archive) or output.parent == database.parent:
        raise ValueError("Snapshot output must be new and outside persistent data")
    source_files = checked_files(archive)
    key_files = checked_files(keys) if keys is not None else []
    evidence = read_evidence(evidence_path)
    if evidence["archiveSha256"] != file_inventory(archive) or keys is None or evidence["keyRingSha256"] != file_inventory(keys):
        raise ValueError("Evidence does not match retained archive/key ring inventory")
    output.mkdir(parents=True, mode=0o700)
    try:
        with closing(sqlite3.connect(database.as_uri() + "?mode=ro", uri=True)) as source:
            with closing(sqlite3.connect(output / "journal.sqlite")) as destination:
                source.backup(destination)
                if destination.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
                    raise ValueError("SQLite snapshot integrity check failed")
                current_authority = authority(destination)
                if current_authority != {key: evidence[key] for key in current_authority}:
                    raise ValueError("Evidence does not match current SQLite authority")
        for path in source_files:
            target = output / "archive" / path.relative_to(archive)
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            shutil.copyfile(path, target)
        (output / "archive").mkdir(exist_ok=True, mode=0o700)
        if keys is not None:
            (output / "keyring").mkdir(mode=0o700)
            for path in key_files:
                target = output / "keyring" / path.relative_to(keys)
                target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                shutil.copyfile(path, target)
        if evidence["archiveSha256"] != file_inventory(output / "archive") or evidence["keyRingSha256"] != file_inventory(output / "keyring"):
            raise ValueError("Retained bytes changed during snapshot; writer quiescence is broken")
        shutil.copyfile(evidence_path, output / "recovery-evidence.json")
        hashes = {str(path.relative_to(output)).replace("\\", "/"): digest(path) for path in checked_files(output)}
        (output / "snapshot-manifest.json").write_text(json.dumps({"schemaVersion": 1, "keyRingIncluded": keys is not None, "sha256": hashes}, sort_keys=True), encoding="utf-8")
        for path in checked_files(output):
            os.chmod(path, 0o600)
    except Exception:
        shutil.rmtree(output)
        raise


def verify(snapshot_root, latest_evidence):
    root = snapshot_root.resolve()
    files = checked_files(root)
    manifest = json.loads((root / "snapshot-manifest.json").read_text(encoding="utf-8"))
    expected = manifest.get("sha256", {})
    actual = {str(path.relative_to(root)).replace("\\", "/"): digest(path) for path in files if path != root / "snapshot-manifest.json"}
    if manifest.get("schemaVersion") != 1 or actual != expected:
        raise ValueError("Snapshot retained bytes/hash inventory does not match")
    saved = read_evidence(root / "recovery-evidence.json")
    latest = read_evidence(latest_evidence)
    # This verifier cannot prove reconciliation of newer tombstones. Fail closed.
    if {key: value for key, value in saved.items() if key != "capturedAtUtc"} != {key: value for key, value in latest.items() if key != "capturedAtUtc"}:
        raise ValueError("Latest deletion/revocation evidence differs: offline reconciliation required")
    if latest["unresolvedAttemptIds"]:
        raise ValueError("Ambiguous provider attempts require offline adjudication")
    with closing(sqlite3.connect((root / "journal.sqlite").as_uri() + "?mode=ro", uri=True)) as database:
        if database.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise ValueError("Restored SQLite integrity check failed")
        current_authority = authority(database)
        if current_authority != {key: saved[key] for key in current_authority}:
            raise ValueError("Restored SQLite authority differs from exported evidence")
    if saved["archiveSha256"] != file_inventory(root / "archive") or saved["keyRingSha256"] != file_inventory(root / "keyring"):
        raise ValueError("Retained archive/key ring differs from authoritative inventory")
    return {"result": "verified_isolated_only", "fileCount": len(actual), "storeId": latest["storeId"], "revision": latest["revision"], "publicStartAuthorized": False, "providerStartAuthorized": False}


def encrypt(database, archive, evidence, output, recipients, quiesced, keys):
    require_private_output(output)
    if not recipients or output.exists():
        raise ValueError("Require recipient(s) and a new encrypted output path")
    output.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with tempfile.TemporaryDirectory(prefix="keepsake-backup-", dir=output.parent) as temporary:
        os.chmod(temporary, 0o700)
        root = Path(temporary)
        snapshot(database, archive, evidence, root / "snapshot", quiesced, keys)
        bundle = root / "snapshot.tar"
        with tarfile.open(bundle, "w") as container:
            for path in sorted((root / "snapshot").rglob("*")):
                container.add(path, arcname=path.relative_to(root / "snapshot"), recursive=False)
        arguments = ["age"]
        for recipient in recipients:
            arguments.extend(("--recipient", recipient))
        arguments.extend(("--output", str(root / "backup.age"), str(bundle)))
        subprocess.run(arguments, check=True, capture_output=True)
        os.chmod(root / "backup.age", 0o600)
        os.replace(root / "backup.age", output)


def unpack(bundle, output):
    require_private_output(output)
    if output.exists():
        raise ValueError("Restore requires a new isolated directory")
    with tarfile.open(bundle, "r:") as container:
        members = container.getmembers()
        if len(members) > 100000:
            raise ValueError("Restore archive has too many entries")
        names = set()
        for member in members:
            path = Path(member.name)
            if not member.name or path.is_absolute() or ":" in member.name or ".." in path.parts or "\\" in member.name or member.name in names or not (member.isfile() or member.isdir()):
                raise ValueError("Unsafe restore archive entry")
            names.add(member.name)
        output.mkdir(parents=True, mode=0o700)
        try:
            for member in members:
                target = output / member.name
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True, mode=0o700)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                    with container.extractfile(member) as source, target.open("xb") as destination:
                        shutil.copyfileobj(source, destination)
                    os.chmod(target, 0o600)
        except Exception:
            shutil.rmtree(output)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("snapshot", "encrypt", "export-evidence"):
        command = commands.add_parser(name)
        for option in ("database", "archive", "output"):
            command.add_argument("--" + option, type=Path, required=True)
        if name != "export-evidence":
            command.add_argument("--evidence", type=Path, required=True)
        command.add_argument("--writers-quiesced", action="store_true")
        command.add_argument("--keys", type=Path, required=True)
        if name == "encrypt":
            command.add_argument("--recipient", action="append", required=True)
    restore = commands.add_parser("unpack")
    restore.add_argument("--bundle", type=Path, required=True)
    restore.add_argument("--output", type=Path, required=True)
    check = commands.add_parser("verify")
    check.add_argument("--snapshot", type=Path, required=True)
    check.add_argument("--latest-evidence", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "snapshot":
        snapshot(args.database, args.archive, args.evidence, args.output, args.writers_quiesced, args.keys)
    elif args.command == "encrypt":
        encrypt(args.database, args.archive, args.evidence, args.output, args.recipient, args.writers_quiesced, args.keys)
    elif args.command == "export-evidence":
        export_evidence(args.database, args.archive, args.keys, args.output, args.writers_quiesced)
    elif args.command == "unpack":
        unpack(args.bundle, args.output)
    else:
        print(json.dumps(verify(args.snapshot, args.latest_evidence), sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, sqlite3.Error, subprocess.CalledProcessError, tarfile.TarError) as error:
        raise SystemExit("Recovery gate failed (no service was started): " + type(error).__name__) from None
