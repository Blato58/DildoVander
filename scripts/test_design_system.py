import tempfile
import unittest
import json
from pathlib import Path

from check_design_system import GUIDANCE, PALETTE, inspect
from generate_design_tokens import outputs


class IntegrityTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.palette = self.root / "kufr/design-system/tokens/palette.css"
        self.palette.parent.mkdir(parents=True)
        self.tokens = json.loads((Path(__file__).resolve().parents[1]
                                  / "kufr/design-system/tokens.json").read_text(encoding="utf-8"))
        (self.palette.parent.parent / "tokens.json").write_text(json.dumps(self.tokens), encoding="utf-8")
        for path, content in outputs(self.tokens, root=self.root).items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
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

    def test_export_uses_generated_tokens_and_rejects_copied_palette(self):
        path = self.root / "kufr/backend/Features/Keepsakes/KeepsakeExports.cs"
        path.parent.mkdir(parents=True)
        path.write_text("\n".join(f'const string Card{name} = DesignTokens.{token};'
                                  for name, token in {"Mist": "VapourMist", "Midnight": "ChromeMidnight",
                                                      "Steel": "SteelGrey", "Periwinkle": "DigitalPeriwinkle"}.items()))
        self.assertEqual(inspect(self.root), [])
        path.write_text(path.read_text().replace("DesignTokens.DigitalPeriwinkle", '"#B8C0FF"'))
        self.assertTrue(any("CardPeriwinkle" in issue for issue in inspect(self.root)))

    def test_generated_native_token_drift_is_detected(self):
        path = self.root / "kufr/backend/Shared/DesignTokens.g.cs"
        path.write_text(path.read_text().replace("#B8C0FF", "#FFFFFF"))
        self.assertTrue(any("DesignTokens.g.cs" in issue for issue in inspect(self.root)))

    def test_shared_adapter_imports_and_duplicate_helpers_are_checked(self):
        adapter = self.root / "kufr/simulator/dashboard-ui.js"
        adapter.parent.mkdir(parents=True)
        adapter.write_text("import {button} from '../design-system/missing.js';\nfunction escapeHtml(value) {}")
        issues = inspect(self.root)
        self.assertTrue(any("Broken UI import" in issue for issue in issues))
        self.assertTrue(any("Duplicated shared UI helper" in issue for issue in issues))

    def test_shared_runtime_import_cycles_are_rejected(self):
        runtime = self.root / 'kufr/design-system/ui.js'
        adapter = self.root / 'kufr/simulator/dashboard-ui.js'
        adapter.parent.mkdir(parents=True)
        runtime.write_text("import '../simulator/dashboard-ui.js';")
        adapter.write_text("import '../design-system/ui.js';")
        self.assertTrue(any('Circular shared UI import' in issue for issue in inspect(self.root)))


if __name__ == "__main__":
    unittest.main()
