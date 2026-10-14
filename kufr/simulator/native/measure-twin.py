#!/usr/bin/env python3
"""Measured simulation-only transport/Stop proof; credentials stay in a private file."""
import argparse
import json
from pathlib import Path
import statistics
import threading
import time
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--credentials", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    credentials = json.loads(args.credentials.read_text())
    origin = args.origin.rstrip("/")

    def login(credential):
        request = urllib.request.Request(origin + "/v1/twin/session", data=json.dumps({"credential": credential}).encode(),
            headers={"Content-Type": "application/json", "Origin": origin})
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.headers["Set-Cookie"].split(";")[0]

    observer, control = login(credentials["observe"]), login(credentials["control"])
    url = origin + "/v1/twin/instances/sim-development"

    def read():
        with urllib.request.urlopen(urllib.request.Request(url, headers={"Cookie": observer}), timeout=5) as response:
            return json.load(response)

    def command(body):
        request = urllib.request.Request(url + "/commands", data=json.dumps(body).encode(),
            headers={"Content-Type": "application/json", "Origin": origin, "Cookie": control})
        with urllib.request.urlopen(request, timeout=5) as response:
            result = json.load(response)
            assert result["accepted"], result["code"]
            return result

    command({"action": "stop"})
    command({"action": "inspect"})
    command({"action": "clock", "paused": False, "speed": 1})
    before = read()
    time.sleep(1)
    after = read()
    assert before["snapshot"]["source"]["bootId"] == after["snapshot"]["source"]["bootId"]
    assert int(after["snapshot"]["timeMs"]) - int(before["snapshot"]["timeMs"]) >= 500
    command({"action": "text", "slot": 6, "variant": "main", "operationId": str(uuid.uuid4()), "text": "synthetic transport measurement"})
    slow_started = threading.Event()

    def slow_reader():
        with urllib.request.urlopen(urllib.request.Request(url, headers={"Cookie": observer}), timeout=5) as response:
            slow_started.set()
            for _ in range(20):
                response.read(1)
                time.sleep(.1)

    slow = threading.Thread(target=slow_reader)
    slow.start()
    slow_started.wait(5)
    durations, updates = [], []
    for _ in range(20):
        started = time.perf_counter()
        value = read()
        durations.append((time.perf_counter() - started) * 1000)
        updates.append((time.perf_counter(), value["snapshot"]["sequence"]))
        time.sleep(.05)
    started = time.perf_counter()
    stopped = command({"action": "stop"})
    stop_ms = (time.perf_counter() - started) * 1000
    assert stopped["snapshot"]["reported"]["outputsEnabled"] is False
    assert stopped["snapshot"]["reported"]["physicalVerification"] == "unavailable"
    slow.join(timeout=5)
    command({"action": "release"})
    changes = sum(first[1] != second[1] for first, second in zip(updates, updates[1:]))
    result = {"environment": "VPS HTTPS client using Python urllib; 20 polls at 50ms with one 10B/s slow reader",
        "httpMedianMs": statistics.median(durations), "httpP95Ms": sorted(durations)[18],
        "simulationStopRoundTripMs": stop_ms, "observedSnapshotUpdatesPerSecond": changes / (updates[-1][0] - updates[0][0]),
        "viewerAbsentTimeAdvanceMs": int(after["snapshot"]["timeMs"]) - int(before["snapshot"]["timeMs"]),
        "bootPreserved": True, "slowReaderBlockedControl": False, "physicalVerification": "unavailable"}
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result))


if __name__ == "__main__":
    main()
