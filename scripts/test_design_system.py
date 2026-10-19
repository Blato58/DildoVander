import tempfile
import unittest
from pathlib import Path

from check_design_system import GUIDANCE, PALETTE, inspect


class IntegrityTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.palette = self.root / "kufr/design-system/tokens/palette.css"
        self.palette.parent.mkdir(parents=True)
        self.palette.write_text(":root {" + "".join(
            f"--aw-{name}: {value};" for name, value in PALETTE.items()
        ) + '} :root[data-theme="light"] {} :root[data-theme="dark"] {}', encoding="utf-8")
        for relative in GUIDANCE:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("Vapour Chrome", encoding="utf-8")

    def test_valid_fixture(self):
        self.assertEqual(inspect(self.root), [])

    def test_palette_drift_is_detected(self):
        self.palette.write_text(self.palette.read_text().replace("#EEF1F5", "#FFFFFF"))
        self.assertTrue(any("--aw-mist" in issue for issue in inspect(self.root)))

    def test_missing_token_and_old_guidance_are_detected(self):
        (self.root / "kufr/design-system/ui.css").write_text("a { color: var(--aw-missing); }")
        (self.root / "DESIGN.md").write_text("Arcane Workshop")
        issues = inspect(self.root)
        self.assertTrue(any("Undefined --aw-missing" in issue for issue in issues))
        self.assertTrue(any("Obsolete active" in issue for issue in issues))

    def test_generated_and_material_colors_are_excluded(self):
        export = self.root / "kufr/mechanika/final/exports"
        export.mkdir(parents=True)
        (export / "viewer.html").write_text("<style>a{color:var(--aw-unknown)}</style>")
        (export.parent / "viewer.js").write_text("const material = '#D9B777';")
        self.assertEqual(inspect(self.root), [])

    def test_export_palette_mirror_is_checked(self):
        path = self.root / "kufr/backend/Features/Keepsakes/KeepsakeExports.cs"
        path.parent.mkdir(parents=True)
        path.write_text("\n".join(f'const string Card{name.title()} = "{color}";'
                                  for name, color in PALETTE.items()))
        self.assertEqual(inspect(self.root), [])
        path.write_text(path.read_text().replace("#B8C0FF", "#D9B777"))
        self.assertTrue(any("CardPeriwinkle" in issue for issue in inspect(self.root)))


if __name__ == "__main__":
    unittest.main()
