#!/usr/bin/env python3
"""Committed inventory checks with the same pinned cJSON bytes as firmware CI."""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_show_tests import CORE, ROOT, compiler_environment

CJSON_HASHES = {
    "cJSON.c": "298581a04a36c0165da4b0aade235c23088cb2faa58651d720ea2f3706ed0b0d",
    "cJSON.h": "25b0145150d500498e4d209cec69c18c42cf818bffcc54690be3b895a2a16dee",
}
CHECKS = {
    "evidence_storage_owner_test": ["device_control/PersistentCommandJournal",
        "device_control/JournalStorageOwner", "device_control/EvidenceStorageOwner",
        "conversation/ConversationStartCodec", "keepsake/KeepsakeCommittedInventory",
        "offline_show/OfflineCommittedInventory", "offline_show/OfflineOutboxCodec"],
    "device_storage_runtime_test": CORE + ["device_control/PersistentCommandJournal",
        "device_control/JournalIdentityOwner", "device_control/JournalReconciliation",
        "device_control/JournalStorageOwner", "device_control/AsyncCommandJournal",
        "device_control/EvidenceStorageOwner", "device_control/DeviceStorageRuntime",
        "conversation/ConversationStartCodec", "keepsake/KeepsakeCommittedInventory",
        "offline_show/OfflineCommittedInventory"],
    "keepsake_persistence_codec_test": ["conversation/ConversationStartCodec"],
    "epoch_evidence_test": ["device_control/PersistentCommandJournal", "conversation/ConversationStartCodec"],
    "keepsake_inventory_test": ["device_control/PersistentCommandJournal",
        "conversation/ConversationStartCodec", "keepsake/KeepsakeCommittedInventory"],
    "keepsake_inventory_storage_test": ["device_control/PersistentCommandJournal",
        "conversation/ConversationStartCodec", "keepsake/KeepsakeCommittedInventory",
        "keepsake/EspIdfKeepsakeInventoryStorage"],
    "journal_committed_consumers_test": CORE + ["device_control/PersistentCommandJournal",
        "device_control/JournalIdentityOwner", "device_control/JournalReconciliation",
        "conversation/ConversationStartCodec", "keepsake/KeepsakeCommittedInventory",
        "offline_show/OfflineCommittedInventory"],
    "journal_service_establishment_test": ["device_control/PersistentCommandJournal",
        "device_control/JournalIdentityOwner", "device_control/JournalReconciliation",
        "device_control/JournalServiceEstablishment", "conversation/ConversationStartCodec",
        "keepsake/KeepsakeCommittedInventory", "offline_show/OfflineOutboxCodec",
        "offline_show/OfflineCommittedInventory", "lib/device_configuration/DeviceIdentity"],
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cjson-dir", required=True, type=Path)
    parser.add_argument("--only", action="append", choices=CHECKS)
    args = parser.parse_args()
    cjson = args.cjson_dir.resolve()
    for name, expected in CJSON_HASHES.items():
        if hashlib.sha256((cjson / name).read_bytes()).hexdigest() != expected:
            raise SystemExit(f"Pinned cJSON hash mismatch: {name}")
    compiler, env, msvc = compiler_environment()
    with tempfile.TemporaryDirectory(prefix="inventory-host-", ignore_cleanup_errors=True) as temp:
        output = Path(temp)
        obj = output / ("cJSON.obj" if msvc else "cJSON.o")
        if msvc:
            command = [compiler, "/nologo", "/TC", "/c", "/W0", "/Fo:" + str(obj), str(cjson / "cJSON.c")]
        else:
            cc = shutil.which("gcc", path=env.get("PATH"))
            if not cc:
                raise SystemExit("gcc is required to compile pinned cJSON as C")
            command = [cc, "-std=c99", "-Wall", "-Wextra", "-I", str(cjson), "-c", str(cjson / "cJSON.c"), "-o", str(obj)]
        subprocess.run(command, cwd=output, env=env, check=True)
        completed = 0
        for name, sources in CHECKS.items():
            if args.only and name not in args.only:
                continue
            executable = output / (name + (".exe" if msvc else ""))
            if msvc:
                flags = ["/nologo", "/std:c++14", "/utf-8", "/EHsc", "/W4", "/WX", "/wd4244",
                    "/I" + str(cjson), "/Fe:" + str(executable)]
            else:
                flags = ["-std=c++14", "-Wall", "-Wextra", "-Werror", "-pthread", "-I", str(cjson), "-o", str(executable)]
            if name == "keepsake_inventory_storage_test":
                stub = str(ROOT / "tests/journal_storage_stubs")
                flags += ["/I" + stub] if msvc else ["-I", stub]
            if name == "journal_service_establishment_test":
                include = str(ROOT / "lib/device_configuration/include")
                flags += ["/I" + include] if msvc else ["-I", include]
            files = [str(ROOT / ((source + ".cpp") if source.startswith("lib/")
                else "src/features/" + source + ".cpp")) for source in sources]
            files += [str(ROOT / "tests" / (name + ".cpp")), str(obj)]
            subprocess.run([compiler, *flags, *files], cwd=output, env=env, check=True)
            subprocess.run([str(executable)], cwd=output, env=env, check=True)
            print(f"PASS {name}", flush=True)
            completed += 1
        print(f"PASS {completed} committed inventory/codec checks", flush=True)


if __name__ == "__main__":
    main()
