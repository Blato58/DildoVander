#!/usr/bin/env python3
"""Bounded observer-only export from technical JSONL; never sends a command."""
import argparse
import collections
import json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("output")
    args = parser.parse_args()
    records = collections.deque()
    total = dropped = 0
    with open(args.source, encoding="utf-8") as source:
        for line in source:
            if len(line) > 32768:
                raise SystemExit("Oversized technical record")
            value = json.loads(line)
            # Recorded time drives replay; these are historical receipt envelopes.
            value.update(ageMs=0, status="online")
            encoded = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
            size = len(encoded.encode("utf-8")) + 1
            records.append((value, size))
            total += size
            while len(records) > 2048 or total > 8 * 1024 * 1024 - 4096:
                total -= records.popleft()[1]
                dropped += 1
    with open(args.output, "w", encoding="utf-8") as output:
        json.dump({"schemaVersion": 1, "bounded": True, "omittedEarlierSnapshots": dropped,
                   "snapshots": [value for value, _ in records]}, output, ensure_ascii=False)
    print(f"Exported {len(records)} historical snapshots; omitted earlier={dropped}; no commands sent")


if __name__ == "__main__":
    main()
