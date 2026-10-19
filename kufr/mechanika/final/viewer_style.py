"""Snapshot the shared design system for reproducible, standalone F2 packages."""
from __future__ import annotations

import base64
import mimetypes
from pathlib import Path
import re
from viewer_runtime import snapshot_runtime


ROOT = Path(__file__).resolve().parent
SHARED_ENTRY = ROOT.parent.parent / "design-system" / "arcane-workshop.css"
SNAPSHOT = ROOT / "workshop-design-system.css"
IMPORT = re.compile(r'@import\s+(?:url\(\s*)?[\'"]([^\'"]+)[\'"]\s*\)?\s*;')
ASSET = re.compile(r'url\(\s*([\'"]?)([^)\'\"]+)\1\s*\)')


def flatten_stylesheet(path: Path, root: Path, ancestors: tuple[Path, ...] = ()) -> str:
    path = path.resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Design asset escapes the shared design-system directory")
    if path in ancestors:
        raise ValueError("Circular design-system CSS import")
    text = path.read_text(encoding="utf-8-sig")

    def embed_asset(match):
        reference = match.group(2).strip()
        if reference.startswith("data:"):
            return match.group(0)
        asset = (path.parent / reference).resolve()
        if not asset.is_relative_to(root.resolve()):
            raise ValueError("Design asset escapes the shared design-system directory")
        mime = {".ttf": "font/ttf", ".woff2": "font/woff2"}.get(asset.suffix)
        mime = mime or mimetypes.guess_type(asset.name)[0] or "application/octet-stream"
        encoded = base64.b64encode(asset.read_bytes()).decode("ascii")
        return f'url("data:{mime};base64,{encoded}")'

    # Nested files resolve their URLs before being inserted into this file.
    text = IMPORT.sub(lambda match: flatten_stylesheet(
        path.parent / match.group(1), root, (*ancestors, path)), text)
    text = ASSET.sub(embed_asset, text)
    if "@import" in text:
        raise ValueError("Unsupported CSS import in the offline design system")
    return text


def snapshot_styles() -> Path:
    # F2 source hashes call this before capture. Refresh both canonical assets
    # here so a later HTML render cannot change the accepted source snapshot.
    snapshot_runtime()
    if SHARED_ENTRY.is_file():
        content = "/* Generated shared Vapour Chrome styles; retained for standalone regeneration. */\n"
        content += flatten_stylesheet(SHARED_ENTRY, SHARED_ENTRY.parent)
        for license in sorted((SHARED_ENTRY.parent / "fonts").glob("*-OFL.txt")):
            content += "\n/* " + license.name + "\n" + license.read_text(encoding="utf-8").replace("*/", "* /") + "\n*/\n"
        if not SNAPSHOT.is_file() or SNAPSHOT.read_text(encoding="utf-8") != content:
            SNAPSHOT.write_text(content, encoding="utf-8")
    if not SNAPSHOT.is_file():
        raise FileNotFoundError("Missing standalone Vapour Chrome style snapshot")
    return SNAPSHOT


if __name__ == "__main__":
    print(snapshot_styles().name)
