"""Offline DV01 receipts for the existing evaluator. Never opens a provider connection."""

import hashlib
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def validate_inputs():
    corpus = json.loads((HERE / "dv01-scenarios.json").read_text(encoding="utf-8"))
    if [r["id"] for r in corpus["requirements"]] != [f"R{i:02}" for i in range(1, 35)]:
        raise ValueError("Incomplete requirement identifiers")
    if [t["id"] for t in corpus["scenarios"]] != [f"T{i:02}" for i in range(1, 81)]:
        raise ValueError("Incomplete scenario identifiers")
    baseline = json.loads((HERE / "dv01-baseline.json").read_text(encoding="utf-8"))
    for path, identity in baseline["files"].items():
        # The evaluator is deliberately extended; the original persona, catalog
        # and baseline cases remain byte-identical.
        if path.endswith("evaluate_party.py"):
            continue
        if hashlib.sha256((ROOT / path).read_bytes()).hexdigest() != identity["sha256"]:
            raise ValueError("Changed DV01 baseline: " + path)
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
    paths = list((ROOT / "kufr/backend/Features").rglob("*.cs"))
    paths += list((ROOT / "kufr/backend-tests").glob("*.cs"))
    paths += [ROOT / "kufr/backend/backend.csproj", ROOT / "kufr/backend/Program.cs",
              ROOT / "kufr/backend-tests/BackendTests.csproj", ROOT / "kufr/global.json"]
    paths += list((ROOT / "kufr/ai-eval").glob("*persona*.txt"))
    paths += [ROOT / "kufr/ai-eval/wand-catalog.json", ROOT / "kufr/ai-eval/prompt.txt"]
    digest = hashlib.sha256()
    for path in sorted(paths):
        name = path.relative_to(ROOT).as_posix().encode()
        content = path.read_bytes()
        digest.update(len(name).to_bytes(8, "big")); digest.update(name)
        digest.update(len(content).to_bytes(8, "big")); digest.update(content)
    return digest.hexdigest()


def run_offline(output, check_only=False):
    corpus, baseline, coverage = validate_inputs()
    report = {"schemaVersion": 1, "initiative": "DV01", "layer": "A/B", "synthetic": True,
              "recordedAt": datetime.now(timezone.utc).isoformat(), "sourceDigest": source_digest(),
              "baselineCommit": baseline["baseCommit"], "requirements": len(corpus["requirements"]),
              "scenarios": len(corpus["scenarios"]), "mapping": "dv01-coverage.json",
              "providerCalls": 0, "actuatorCommands": 0, "offline_verified": False,
              "live_behavior_verified": False, "audio_hardware_verified": False,
              "physical_reveal_verified": False, "event_ready": False,
              "visual_roast": "permission_pending", "named_finale": "blocked",
              "limited_finale": "prepared_explicit_opt_in; live acceptance pending",
              "checks": [], "director_overhead": None}
    if not check_only:
        command = ["dotnet", "run", "--no-restore", "--project", "backend-tests", "--", "--character"]
        result = subprocess.run(command, cwd=ROOT / "kufr", text=True, encoding="utf-8",
                                errors="replace", capture_output=True, timeout=180)
        report["checks"].append({"command": command, "cwd": "kufr", "exitCode": result.returncode,
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
