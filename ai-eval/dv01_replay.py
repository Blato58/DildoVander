"""Offline DV01 receipts for the existing evaluator. Never opens a provider connection."""

import argparse
import hashlib
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HERE = Path(__file__).resolve().parent
CURRENT_REFERENCE_ID = "original-personas-v1"
CURRENT_REFERENCE_NAME = "dv01-current-reference.json"
AUTHORED_PERSONAS = frozenset({
    "ai-eval/ollivander-trunk-live-persona.txt",
    "ai-eval/ollivander-trunk-persona.txt",
    "ai-eval/voice-persona.txt",
})


def text_sha256(path):
    # Git text files may use CRLF locally; reference identity is UTF-8 with LF.
    return hashlib.sha256(path.read_text(encoding="utf-8").encode("utf-8")).hexdigest()


def read_current_reference(baseline):
    reference = json.loads((HERE / CURRENT_REFERENCE_NAME).read_text(encoding="utf-8"))
    if (reference.get("schemaVersion") != 1
            or reference.get("referenceId") != CURRENT_REFERENCE_ID
            or reference.get("historicalBaseline") != "dv01-baseline.json"
            or reference.get("historicalBaseCommit") != baseline["baseCommit"]
            or reference.get("hashEncoding") != "utf8-lf"
            or reference.get("providerBehaviorVerified") is not False
            or not isinstance(reference.get("reason"), str) or not reference["reason"].strip()
            or set(reference.get("files", {})) != AUTHORED_PERSONAS):
        raise ValueError("Invalid DV01 current authored reference")
    for path, identity in reference["files"].items():
        original = baseline["files"]["kufr/" + path]
        if identity.get("historicalSha256") != original["sha256"]:
            raise ValueError("Changed DV01 historical identity: " + path)
    return reference


def capture_current_reference(reason):
    if not isinstance(reason, str) or not 10 <= len(reason.strip()) <= 500:
        raise ValueError("An explicit 10-500 character capture reason is required")
    baseline = json.loads((HERE / "dv01-baseline.json").read_text(encoding="utf-8"))
    files = {}
    for path, identity in baseline["files"].items():
        current_path = path.removeprefix("kufr/")
        if current_path == "ai-eval/evaluate_party.py":
            continue
        if current_path in AUTHORED_PERSONAS:
            files[current_path] = {"sha256": text_sha256(ROOT / current_path), "historicalSha256": identity["sha256"]}
        elif hashlib.sha256((ROOT / current_path).read_bytes()).hexdigest() != identity["sha256"]:
            raise ValueError("Changed DV01 baseline: " + path)
    if set(files) != AUTHORED_PERSONAS:
        raise ValueError("Historical baseline is missing an allowed authored persona")
    reference = {
        "schemaVersion": 1, "referenceId": CURRENT_REFERENCE_ID,
        "capturedAt": datetime.now(timezone.utc).isoformat(),
        "historicalBaseline": "dv01-baseline.json", "historicalBaseCommit": baseline["baseCommit"],
        "hashEncoding": "utf8-lf", "reason": reason.strip(),
        "providerBehaviorVerified": False, "files": files,
    }
    (HERE / CURRENT_REFERENCE_NAME).write_text(
        json.dumps(reference, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return reference


def validate_inputs():
    corpus = json.loads((HERE / "dv01-scenarios.json").read_text(encoding="utf-8"))
    if [r["id"] for r in corpus["requirements"]] != [f"R{i:02}" for i in range(1, 35)]:
        raise ValueError("Incomplete requirement identifiers")
    if [t["id"] for t in corpus["scenarios"]] != [f"T{i:02}" for i in range(1, 81)]:
        raise ValueError("Incomplete scenario identifiers")
    baseline = json.loads((HERE / "dv01-baseline.json").read_text(encoding="utf-8"))
    reference = read_current_reference(baseline)
    for path, identity in baseline["files"].items():
        # Historical measurements and their pins stay intact. Only the explicit
        # three authored replacements use a separately captured current pin.
        current_path = path.removeprefix("kufr/")
        if current_path == "ai-eval/evaluate_party.py":
            continue
        if current_path in AUTHORED_PERSONAS:
            expected = reference["files"][current_path]["sha256"]
            actual = text_sha256(ROOT / current_path)
        else:
            expected = identity["sha256"]
            actual = hashlib.sha256((ROOT / current_path).read_bytes()).hexdigest()
        if actual != expected:
            raise ValueError("Changed DV01 current source reference: " + path)
    coverage = json.loads((HERE / "dv01-coverage.json").read_text(encoding="utf-8"))
    if set(coverage["scenarios"]) != {f"T{i:02}" for i in range(1, 81)}:
        raise ValueError("Incomplete scenario evidence map")
    for scenario in coverage["scenarios"].values():
        for reference in scenario["tests"]:
            file, symbol = reference.split("#")
            if symbol not in (ROOT / file).read_text(encoding="utf-8"):
                raise ValueError("Missing actual test reference: " + reference)
        if not scenario["tests"] and not scenario["gate"]:
            raise ValueError("Scenario needs a test or a concrete gate")
    return corpus, baseline, coverage


def source_digest():
    paths = list((ROOT / "backend/Features").rglob("*.cs"))
    paths += list((ROOT / "backend-tests").glob("*.cs"))
    paths += [ROOT / "backend/backend.csproj", ROOT / "backend/Program.cs",
              ROOT / "backend-tests/BackendTests.csproj", ROOT / "global.json"]
    paths += list((ROOT / "ai-eval").glob("*persona*.txt"))
    paths += [ROOT / "ai-eval/wand-catalog.json", ROOT / "ai-eval/prompt.txt"]
    digest = hashlib.sha256()
    for path in sorted(paths):
        name = path.relative_to(ROOT).as_posix().encode()
        content = path.read_bytes()
        digest.update(len(name).to_bytes(8, "big")); digest.update(name)
        digest.update(len(content).to_bytes(8, "big")); digest.update(content)
    return digest.hexdigest()


def run_offline(output, check_only=False):
    corpus, baseline, coverage = validate_inputs()
    reference = read_current_reference(baseline)
    report = {"schemaVersion": 1, "initiative": "DV01", "layer": "A/B", "synthetic": True,
              "recordedAt": datetime.now(timezone.utc).isoformat(), "sourceDigest": source_digest(),
              "baselineCommit": baseline["baseCommit"],
              "currentReference": CURRENT_REFERENCE_NAME, "currentReferenceId": reference["referenceId"],
              "currentReferenceSha256": text_sha256(HERE / CURRENT_REFERENCE_NAME),
              "currentProviderBehaviorVerified": reference["providerBehaviorVerified"],
              "requirements": len(corpus["requirements"]),
              "scenarios": len(corpus["scenarios"]), "mapping": "dv01-coverage.json",
              "providerCalls": 0, "actuatorCommands": 0, "offline_verified": False,
              "live_behavior_verified": False, "audio_hardware_verified": False,
              "physical_reveal_verified": False, "event_ready": False,
              "visual_roast": "permission_pending", "named_finale": "blocked",
              "limited_finale": "prepared_explicit_opt_in; live acceptance pending",
              "checks": [], "director_overhead": None}
    if not check_only:
        command = ["dotnet", "run", "--no-restore", "--project", "backend-tests", "--", "--character"]
        result = subprocess.run(command, cwd=ROOT, text=True, encoding="utf-8",
                                errors="replace", capture_output=True, timeout=180)
        report["checks"].append({"command": command, "cwd": ".", "exitCode": result.returncode,
                                 "stdout": result.stdout, "stderr": result.stderr})
        for line in result.stdout.splitlines():
            if line.startswith('{"metric":"director_overhead"'):
                report["director_overhead"] = json.loads(line)
        report["offline_verified"] = result.returncode == 0
    report["input_structure_verified"] = True
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "checks"}, ensure_ascii=False, indent=2))
    if not check_only and not report["offline_verified"]:
        raise RuntimeError("Offline DV01 harness failed; inspect the recorded stdout/stderr")
    return report


def validate_skills():
    for name in ["dildovander-character-design", "dildovander-character-eval"]:
        path = ROOT / ".agents/skills" / name / "SKILL.md"
        text = path.read_text(encoding="utf-8")
        if not text.startswith("---\nname: " + name + "\ndescription: ") or "\n---\n" not in text:
            raise ValueError("Invalid skill metadata: " + name)
        for link in re.findall(r"\]\(([^)]+)\)", text):
            if not link.startswith("https://") and not (path.parent / link).resolve().is_file():
                raise ValueError("Missing skill reference: " + link)
    return True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Explicit offline capture of the current authored DV01 prompts.")
    parser.add_argument("--capture-current-reference", action="store_true", required=True)
    parser.add_argument("--reason", required=True)
    options = parser.parse_args()
    result = capture_current_reference(options.reason)
    print(json.dumps({"referenceId": result["referenceId"], "providerBehaviorVerified": False}))
