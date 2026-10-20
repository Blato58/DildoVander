"""Embed the real offline viewer sources; independent of CAD tessellation."""
from pathlib import Path
import hashlib
import html
import json

from viewer_style import snapshot_styles
from viewer_runtime import bundle_ui_runtime

ROOT = Path(__file__).resolve().parent


def render_reference_document(evidence):
    """Render the offline evidence page without importing the CAD renderer."""
    sections = []
    for record, anchor, title in zip(evidence, ("inventar", "zapojeni"),
                                    ("Inventář a rozměry", "Potvrzené elektrické zapojení")):
        sections.append(f'<section id="{anchor}"><h2>{title}</h2><p>{html.escape(record["name"])}</p>'
                        f'<p>SHA-256: <code>{html.escape(record["sha256"])}</code></p><pre>{html.escape(record["text"])}</pre></section>')
    document = '<!doctype html><html lang="cs"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Podklady F2 · DildoVander</title>'
    document += '<script>' + bundle_ui_runtime() + '</script><style>' + snapshot_styles().read_text(encoding="utf-8") + '</style>'
    document += '<style>body{max-width:1100px;margin:32px auto;padding:0 20px}pre{white-space:pre-wrap;overflow-wrap:anywhere;line-height:1.5;font-family:var(--aw-font-mono)}code{overflow-wrap:anywhere}section{margin-top:40px;padding-top:24px;border-top:1px solid var(--aw-border)}nav{display:flex;flex-wrap:wrap;gap:16px}nav a{min-height:44px;display:inline-flex;align-items:center}</style></head><body class="aw-root">'
    document += '<label class="aw-theme-selector">Vzhled <select class="aw-select" data-theme-select aria-label="Vzhled aplikace"></select></label>'
    document += '<h1>Úplné zdrojové podklady F2</h1><p>Ověřený otisk zdrojových dokumentů přibalený pro samostatné použití. Text včetně původních odkazů je zachovaný; odkazy uvnitř přepisu odkazují do původního repozitáře.</p>'
    document += '<nav aria-label="Zdrojové podklady"><a href="#inventar">Inventář a rozměry</a><a href="#zapojeni">Zapojení</a><a href="viewer.html">3D sestava</a></nav>'
    return document + ''.join(sections) + '</body></html>'


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
        "__SHOW_TIMELINE_SCRIPT__": (ROOT / "timeline_core.js").read_text(encoding="utf-8") + "\n" + (ROOT / "show_timeline.js").read_text(encoding="utf-8"),
        "__VIEWER_SCRIPT__": (ROOT / "viewer_engine_core.js").read_text(encoding="utf-8") + "\n" + (ROOT / "viewer.js").read_text(encoding="utf-8"),
        "__MODEL_DATA__": json.dumps(payload, separators=(",", ":")).replace("<", "\\u003c"),
    }
    for placeholder, content in replacements.items():
        document = document.replace(placeholder, content)
    document = document.replace("</head>", "<style>" + (ROOT / "twin-viewer.css").read_text(encoding="utf-8") + "</style></head>")
    document = document.replace("</head>", "<style>" + (ROOT / "twin_media.css").read_text(encoding="utf-8") + "</style></head>")
    # Load before the main renderer so its optional runtime hooks exist at mount.
    twin_script = "\n".join((ROOT / filename).read_text(encoding="utf-8") for filename in (
        "twin_media_core.js", "twin_media_controller.js", "twin_media.js", "twin_core.js", "twin_viewer.js"))
    document = document.replace("<script>const model=", "<script>" + twin_script + "</script>\n<script>const model=")
    return document
