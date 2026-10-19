"""Embed the real offline viewer sources; independent of CAD tessellation."""
from pathlib import Path
import hashlib
import json

from viewer_style import snapshot_styles
from viewer_runtime import bundle_ui_runtime

ROOT = Path(__file__).resolve().parent


def render_document(payload):
    payload = dict(payload)
    for key, filename in (("showPlan", "effects.plan.json"), ("showManifest", "effects.manifest.json")):
        payload[key] = json.loads((ROOT / filename).read_text(encoding="utf-8"))
    identity = dict(payload["showManifest"])
    declared_bundle = identity.pop("bundleHash", None)
    identity_bytes = (json.dumps(identity, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    if declared_bundle != hashlib.sha256(identity_bytes).hexdigest():
        raise ValueError("Invalid DV02 bundle manifest hash; run generate_effects.py")
    for field, filename in (("compiledHash", "effects.plan.json"), ("sourceHash", "effects.json"),
                            ("schemaHash", "effects.schema.json"), ("compilerHash", "generate_effects.py")):
        actual = hashlib.sha256((ROOT / filename).read_bytes()).hexdigest()
        if payload["showManifest"].get(field) != actual:
            raise ValueError(f"Stale DV02 viewer input: {filename}; run generate_effects.py")
    document = (ROOT / "viewer-template.html").read_text(encoding="utf-8")
    replacements = {
        "__WORKSHOP_DESIGN_SYSTEM__": snapshot_styles().read_text(encoding="utf-8"),
        "__WORKSHOP_VIEWER_STYLE__": (ROOT / "viewer-workshop.css").read_text(encoding="utf-8-sig"),
        "__WORKSHOP_UI_RUNTIME__": bundle_ui_runtime(),
        "__SHOW_TIMELINE_SCRIPT__": (ROOT / "show_timeline.js").read_text(encoding="utf-8"),
        "__VIEWER_SCRIPT__": (ROOT / "viewer.js").read_text(encoding="utf-8"),
        "__MODEL_DATA__": json.dumps(payload, separators=(",", ":")).replace("<", "\\u003c"),
    }
    for placeholder, content in replacements.items():
        document = document.replace(placeholder, content)
    document = document.replace("</head>", "<style>" + (ROOT / "twin-viewer.css").read_text(encoding="utf-8") + "</style></head>")
    document = document.replace("</head>", "<style>" + (ROOT / "twin_media.css").read_text(encoding="utf-8") + "</style></head>")
    # Load before the main renderer so its optional runtime hooks exist at mount.
    twin_script = (ROOT / "twin_media.js").read_text(encoding="utf-8") + "\n" + (ROOT / "twin_viewer.js").read_text(encoding="utf-8")
    document = document.replace("<script>const model=", "<script>" + twin_script + "</script>\n<script>const model=")
    return document
