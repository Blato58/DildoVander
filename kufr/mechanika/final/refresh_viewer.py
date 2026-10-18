"""Re-embed current viewer UI around an existing CAD payload, without running CAD.

This output is an explicitly labelled UI preview, never a verified F2 export.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path

from viewer_document import render_document


def refresh_preview(source: Path, output: Path):
    if source.resolve() == output.resolve():
        raise ValueError("UI preview must not overwrite the source CAD export")
    document = source.read_text(encoding="utf-8-sig")
    marker = "<script>const model="
    if marker not in document:
        raise ValueError("Source does not contain the offline F2 model payload")
    payload, _ = json.JSONDecoder().raw_decode(document.split(marker, 1)[1])
    if not isinstance(payload, dict) or not isinstance(payload.get("meshes"), list) or not payload["meshes"]:
        raise ValueError("Source model must contain real CAD meshes")
    payload["viewerPreview"] = {
        "sourceName": source.name,
        "dataDate": datetime.fromtimestamp(source.stat().st_mtime, timezone.utc).strftime("%Y-%m-%d UTC"),
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(render_document(payload), encoding="utf-8")
    return len(payload["meshes"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    count = refresh_preview(arguments.source, arguments.output)
    print(f"UI preview: {arguments.output.resolve()} ({count} existing CAD meshes; no geometry verification)")
