"""Read-only Vapour Chrome integrity checks; rendered accessibility remains separate."""

import argparse
import re
from pathlib import Path


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


def inspect(root):
    problems = []
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
    # Offline exports cannot load CSS. Guard their small native palette mirror
    # against drift without treating generated artwork colors as interface chrome.
    exports = root / "kufr/backend/Features/Keepsakes/KeepsakeExports.cs"
    if exports.exists():
        export_source = exports.read_text(encoding="utf-8")
        for name, color in PALETTE.items():
            if not re.search(rf'Card{name.title()}\s*=\s*"{color}"\s*;', export_source, re.I):
                problems.append(f"Export Card{name.title()} must equal canonical {color}")

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
    print("Scope: palette/export mirror, explicit themes, CSS/HTML aw-token references, active guidance.")
    print("Limits: no rendered contrast, interaction, native/hardware or plugin-invocation proof.")
    return bool(problems)


if __name__ == "__main__":
    raise SystemExit(main())
