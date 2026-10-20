"""Fail fast if application/viewer build entry points invoke manufacturing work."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
ENTRY_POINTS = (
    '.github/workflows/kufr-site.yml', '.github/workflows/kufr-web.yml',
    '.github/workflows/kufr-ios.yml', 'web/build-site.py',
    'mobilni-aplikace/package.json',
    'mobilni-aplikace/vite.config.mjs',
    'mobilni-aplikace/scripts/validate-model-bundle.mjs',
    'mobilni-aplikace/scripts/package-offline-viewer.mjs',
    'mechanika/final/export_model_bundle.py',
    'mechanika/final/twin_core.js', 'mechanika/final/timeline_core.js',
    'mechanika/final/twin_media_core.js', 'mechanika/final/twin_media_controller.js',
    'mechanika/final/viewer_engine_core.js',
)
# Deliberately inspect the whole reached file, including optional branches: a
# future UI dispatch must not hide mechanical work behind a conditional step.
FORBIDDEN = re.compile(
    r'verified-cad|cad_cache\.py|install-cad-slicer|'
    r'(?:mechanika/final/|\b)(?:generate|verify)\.py|'
    r'\b(?:cadquery|build123d|FreeCAD|OCP)\b|verified_exports\s*\(',
    re.IGNORECASE,
)
LOCAL_ACTION = re.compile(r'uses:\s*[\'"]?\./([^\s\'"#]+)')


def validate(root=ROOT):
    pending = list(ENTRY_POINTS)
    seen = set()
    while pending:
        relative = pending.pop()
        if relative in seen:
            continue
        seen.add(relative)
        path = root / relative
        if not path.resolve().is_relative_to(root.resolve()):
            raise ValueError('UI entry point escapes checkout: ' + relative)
        source = path.read_text(encoding='utf-8-sig')
        match = FORBIDDEN.search(source)
        if match:
            raise ValueError(f'UI/manufacturing boundary violated in {relative}: {match.group(0)}')
        for reference in LOCAL_ACTION.findall(source):
            target = root / reference
            if target.is_dir():
                reference += '/action.yml'
            pending.append(reference)
    return seen


if __name__ == '__main__':
    print(f'Application/viewer boundary passed: {len(validate())} entry points; no CAD toolchain required')
