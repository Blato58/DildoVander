"""Read-only Vapour Chrome integrity checks; rendered accessibility remains separate."""

import argparse
import json
import re
from pathlib import Path

from generate_design_tokens import outputs, read_output


PALETTE = {
    "mist": "#EEF1F5", "midnight": "#1B2430",
    "steel": "#8A94A6", "periwinkle": "#B8C0FF",
}
UI_ROOTS = (
    "kufr/design-system", "kufr/web", "kufr/mobilni-aplikace/src",
    "kufr/simulator", "kufr/mechanika/final",
    "kufr/firmware/diagnostics/servo-bench/web",
)
GUIDANCE = (
    "AGENTS.md", "DESIGN.md", ".agents/skills/dildovander-ui/SKILL.md",
    ".agents/skills/dildovander-keepsake-design/SKILL.md",
    "kufr/design-system/README.md", "kufr/design-system/style-guide.md",
    "kufr/design-system/components.md", "kufr/design-system/screens.md",
)
IGNORED = {"node_modules", "dist", "exports", "__pycache__", ".pio"}
OLD_UI_COLORS = re.compile(r"#(?:D9B777|F3ECE0|0D1218)\b", re.I)
RUNTIME_CONSUMERS = (
    "kufr/mobilni-aplikace/src/main.ts",
    "kufr/web/keepsake/app.js", "kufr/web/keepsake/evening/evening.js",
    "kufr/simulator/dashboard-ui.js",
    "kufr/firmware/diagnostics/servo-bench/web/panel-ui.mjs",
    "kufr/mechanika/final/viewer_ui.js",
)


def inspect_runtime_imports(root):
    """Check the actual framework adapters, not arbitrary domain imports."""
    problems = []
    framework = root / "kufr/design-system/ui.js"
    paths = [root / relative for relative in RUNTIME_CONSUMERS]
    if framework.exists():
        paths.append(framework)
    graph = {}
    for path in paths:
        if not path.exists():
            continue
        source = path.read_text(encoding="utf-8")
        if path != framework and re.search(r"(?:function|const)\s+(?:escapeHtml|escapeText|bindTabs|bindDialog)\b", source):
            problems.append(f"Duplicated shared UI helper: {path.relative_to(root).as_posix()}")
        dependencies = []
        for relative in re.findall(r"import\s+(?:[\s\S]*?\s+from\s+)?['\"](\.[^'\"]+)['\"]", source):
            dependency = (path.parent / relative).resolve()
            if not dependency.is_file():
                # TypeScript uses extensionless local imports in the mobile app.
                dependency = dependency.with_suffix(".ts") if not dependency.suffix else dependency
            if not dependency.is_file():
                problems.append(f"Broken UI import {relative}: {path.relative_to(root).as_posix()}")
            dependencies.append(dependency)
        graph[path.resolve()] = dependencies

    def visit(path, chain):
        if path in chain:
            problems.append(f"Circular shared UI import: {path.relative_to(root).as_posix()}")
            return
        for dependency in graph.get(path, []):
            visit(dependency, chain | {path})

    for path in graph:
        visit(path, set())
    return problems


def inspect(root):
    root = root.resolve()
    problems = inspect_runtime_imports(root)
    palette_path = root / "kufr/design-system/tokens/palette.css"
    if not palette_path.exists():
        return ["Missing canonical palette: kufr/design-system/tokens/palette.css"]
    palette = palette_path.read_text(encoding="utf-8")
    for name, color in PALETTE.items():
        if not re.search(rf"--aw-{name}\s*:\s*{color}\s*;", palette, re.I):
            problems.append(f"Canonical --aw-{name} must equal {color}")
    for theme in ("light", "dark"):
        if not re.search(rf"data-theme\s*=\s*[\"']?{theme}", palette):
            problems.append(f"Missing explicit {theme} theme")
    # Offline exports consume generated C# constants, never a copied palette.
    exports = root / "kufr/backend/Features/Keepsakes/KeepsakeExports.cs"
    if exports.exists():
        export_source = exports.read_text(encoding="utf-8")
        for name, generated in {"Midnight": "ChromeMidnight", "Mist": "VapourMist",
                                "Steel": "SteelGrey", "Periwinkle": "DigitalPeriwinkle"}.items():
            if not re.search(rf'Card{name}\s*=\s*DesignTokens\.{generated}\s*;', export_source):
                problems.append(f"Export Card{name} must consume generated DesignTokens.{generated}")

    token_source = root / "kufr/design-system/tokens.json"
    if not token_source.exists():
        problems.append("Missing machine-readable design tokens: kufr/design-system/tokens.json")
    else:
        try:
            data = json.loads(token_source.read_text(encoding="utf-8"))
            for path, expected in outputs(data, root=root).items():
                if not path.exists() or read_output(path) != expected:
                    problems.append(f"Generated design token drift: {path.relative_to(root).as_posix()}")
        except (ValueError, KeyError, TypeError) as error:
            problems.append(f"Invalid machine-readable design tokens: {error}")

    files = []
    for relative in UI_ROOTS:
        directory = root / relative
        if directory.exists():
            files.extend(p for p in directory.rglob("*")
                         if p.suffix in {".css", ".html"}
                         and not IGNORED.intersection(p.parts))
    definitions = set()
    uses = []
    for path in sorted(set(files)):
        text = path.read_text(encoding="utf-8")
        definitions.update(re.findall(r"(--aw-[\w-]+)\s*:", text))
        uses.extend((path, token) for token in re.findall(r"var\((--aw-[\w-]+)", text))
        # Only CSS UI chrome is constrained. CAD material/mesh payloads and art
        # are deliberately excluded; their colors encode engineering/content.
        if path.suffix == ".css" and OLD_UI_COLORS.search(text):
            problems.append(f"Obsolete UI color in {path.relative_to(root).as_posix()}")
        # Browser startup metadata cannot reference a CSS variable. Keep these
        # declarative dark-theme fallbacks bound to the canonical primitive.
        if path.suffix == ".html":
            for color in re.findall(r'<meta\b[^>]*name=[\'"]theme-color[\'"][^>]*content=[\'"]([^\'"]+)', text, re.I):
                if color.upper() != PALETTE["midnight"]:
                    problems.append(f"Noncanonical startup theme-color in {path.relative_to(root).as_posix()}")
    for path, token in uses:
        if token not in definitions:
            problems.append(f"Undefined {token} in {path.relative_to(root).as_posix()}")
    for relative in GUIDANCE:
        path = root / relative
        if not path.exists():
            problems.append(f"Missing active guidance: {relative}")
            continue
        text = path.read_text(encoding="utf-8")
        if re.search(r"Arcane Workshop|80/15/5|mosazn[ýéí]", text, re.I):
            problems.append(f"Obsolete active visual guidance: {relative}")
    return sorted(set(problems))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    problems = inspect(args.root.resolve())
    for problem in problems:
        print(problem)
    print(f"Vapour Chrome integrity: {'FAIL' if problems else 'PASS'}")
    print("Scope: generated platform tokens, export binding, explicit themes, CSS/HTML aw-token references, active guidance.")
    print("Limits: no rendered contrast, interaction, native/hardware or plugin-invocation proof.")
    return bool(problems)


if __name__ == "__main__":
    raise SystemExit(main())
