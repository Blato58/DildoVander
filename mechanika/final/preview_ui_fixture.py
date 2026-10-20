"""Render a clearly synthetic offline UI fixture; never import or generate CAD."""
import argparse
import json
from pathlib import Path

from viewer_document import render_document

ROOT = Path(__file__).resolve().parent


def fixture_payload():
    return {
        "viewerPreview": {"sourceName": "synthetic-ui-fixture", "dataDate": "syntetická UI fixture", "synthetic": True},
        "meshes": [{
            "name": "ui_fixture_block", "group": "fixed", "color": [0.54, 0.58, 0.65],
            "bounds": [130, 90, 0, 300, 180, 80],
            "vertices": [130, 90, 0, 300, 90, 0, 300, 180, 0, 130, 180, 0,
                         130, 90, 80, 300, 90, 80, 300, 180, 80, 130, 180, 80],
            "triangles": [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                          1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7],
            "info": {"title": "Syntetický UI blok", "description": "Pouze test ovládání a offline balení UI.",
                     "details": ["Není to díl kufru ani CAD podklad."], "source": "preview_ui_fixture.py"},
        }],
        "routes": [], "sources": [], "hinge": [0, 0, 0],
        "assemblyGuide": {"steps": [
            {"id": "synthetic-inspect", "title": "Syntetická ukázka kontroly",
             "parts": [], "caseParts": [], "benchParts": [],
             "installation": {"kind": "inspection"},
             "instruction": "Pouze UI fixture: toto není skutečný montážní postup kufru.",
             "location": "Testovací pracovní plocha", "direction": "Bez montážního pohybu",
             "partSummary": "Syntetický blok se zobrazí v následující ukázce."},
            {"id": "synthetic-bench", "title": "Syntetická ukázka práce na stole",
             "parts": ["ui_fixture_block"], "caseParts": [], "benchParts": ["ui_fixture_block"],
             "installation": {"kind": "bench"}, "camera": "top",
             "instruction": "Sbalte instrukce a ověřte trvalý název kroku, kontext a výběr bloku.",
             "location": "Testovací pracovní plocha", "direction": "Shora; bez montážního pohybu",
             "partSummary": "Jeden syntetický blok; není to výrobní díl.",
             "check": "Pouze ovládání rozhraní; žádná geometrická nebo fyzická přejímka."},
        ]},
        "mechanism": {"count": 6, "origin_y": 21, "origin_z": 12, "cam_y": 112,
                      "cam_z": 36, "dwell_degrees": 20, "travel_degrees": 160, "lift": 16},
        "effects": json.loads((ROOT / "effects.json").read_text(encoding="utf-8")),
        "twinManifest": {"revision": "synthetic-ui-fixture", "slots": []},
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(render_document(fixture_payload()), encoding="utf-8")
    print(f"Synthetic UI fixture only: {arguments.output.resolve()}")
