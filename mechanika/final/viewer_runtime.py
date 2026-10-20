"""Bundle theme bootstrap and authored native UI modules for the self-contained file viewer."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent
SHARED_RUNTIME = ROOT.parent.parent / "design-system" / "ui.js"
SHARED_THEME = SHARED_RUNTIME.parent / "theme.js"
SHARED_CONTROLS = SHARED_RUNTIME.parent / "theme-controls.js"
SNAPSHOT = ROOT / "workshop-ui-runtime.js"
VIEWER_IMPORT = "import { button, notice, statusBadge } from '../../design-system/ui.js';"


def snapshot_runtime() -> Path:
    if SHARED_RUNTIME.is_file():
        # Retain both sources in the existing presentation snapshot so exported
        # packages can regenerate their head bootstrap without the shared tree.
        source = "\n".join(path.read_text(encoding="utf-8-sig") for path in (
            SHARED_THEME, SHARED_CONTROLS, SHARED_RUNTIME))
        if not SNAPSHOT.is_file() or SNAPSHOT.read_text(encoding="utf-8") != source:
            SNAPSHOT.write_text(source, encoding="utf-8")
    if not SNAPSHOT.is_file():
        raise FileNotFoundError("Missing standalone Vapour Chrome runtime snapshot")
    return SNAPSHOT


def inline_module(path: Path, namespace: str, import_statement: str = "") -> str:
    source = path.read_text(encoding="utf-8-sig")
    prelude = ""
    if import_statement:
        if source.count(import_statement) != 1:
            raise ValueError(f"Unexpected UI module imports: {path.name}")
        source = source.replace(import_statement, "", 1)
        prelude = "const { button, notice, statusBadge } = globalThis.VapourUI;\n"
    # This deliberately accepts only the current dependency-free module shape.
    # Future module dependencies must be packaged explicitly, never left as URLs.
    if re.search(r"^\s*import\b", source, re.MULTILINE):
        raise ValueError(f"Unbundled UI module import: {path.name}")
    exports = re.findall(r"^export function (\w+)\(", source, re.MULTILINE)
    source = re.sub(r"^export (?=function )", "", source, flags=re.MULTILINE)
    if not exports or re.search(r"^\s*export\b", source, re.MULTILINE):
        raise ValueError(f"Unsupported UI module export: {path.name}")
    # Keep inline script boundaries safe even if later copy contains HTML.
    source = source.replace("</script", "<\\/script")
    return f"globalThis.{namespace} = (() => {{\n{prelude}{source}\nreturn {{{', '.join(exports)}}};\n}})();"


def bundle_ui_runtime() -> str:
    return inline_module(snapshot_runtime(), "VapourUI") + "\n" + inline_module(
        ROOT / "viewer_ui.js", "VapourViewerUi", VIEWER_IMPORT)
