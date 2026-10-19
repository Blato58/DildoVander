"""Compatibility notice for the retired standalone dialogue workflow.

This entry point performs no authentication, provider calls or archive mutations.
Use the client voice panel with GPT-Live or Realtime 2.1. An explicitly authorized
Realtime provider smoke remains available in realtime_live_smoke.py.
"""
import json
import os
from pathlib import Path
import sys
from uuid import UUID, uuid4

UPGRADE_CODE = "conversation_engine_upgrade_required"

def run(environment=None, client_factory=None):
    environment = os.environ if environment is None else environment
    try:
        request_id = str(UUID(environment.get("INPUT_REQUEST_ID", "")))
    except (ValueError, TypeError, AttributeError):
        request_id = ""
    action = environment.get("INPUT_ACTION", "")
    report = {
        "requestId": request_id,
        "action": action if action in {"start", "reply", "finish", "status", "clear"} else "",
        "status": "upgrade_required", "error": UPGRADE_CODE, "state": None,
        "supportedEngines": ["gpt-live", "realtime"],
        "nextAction": "Update the client and use GPT-Live or Realtime 2.1 in the voice panel.",
        "remoteRequests": 0,
    }
    report_path = Path(environment.get("RUNNER_TEMP", ".")) / "interactive-dialogue.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if environment.get("GITHUB_OUTPUT"):
        delimiter = str(uuid4())
        with Path(environment["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
            output.write(f"report_path={report_path}\nreport_json<<{delimiter}\n")
            output.write(json.dumps(report) + f"\n{delimiter}\n")
    print(json.dumps(report))
    return report

def main():
    run()
    return 1

if __name__ == "__main__":
    sys.exit(main())
