"""Executable schema and drift checks for the canonical token generator."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("generate_design_tokens", ROOT / "scripts/generate_design_tokens.py")
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)


class DesignTokenTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT / "kufr/design-system/tokens.json").read_text(encoding="utf-8"))

    def test_committed_outputs_are_current(self):
        self.assertEqual([], GENERATOR.check(ROOT))

    def test_rejects_malformed_and_injected_values(self):
        mutations = [
            lambda value: value["primitives"].update(mist="#zzzzzz"),
            lambda value: value["themes"]["light"].update(accent="red; display: none"),
            lambda value: value["themes"]["light"].update(accent="var(--aw-missing)"),
            lambda value: value["themes"]["dark"].update(accent="var(--aw-brass)"),
            lambda value: value["layout"].update(touch="44seconds"),
            lambda value: value["typography"].update({"text-body": "url(evil)"}),
            lambda value: value["motion"].update({"motion-fast": "150px"}),
            lambda value: value["motion"].update({"ease-out": "cubic-bezier(2, 1, .3, 1)"}),
            lambda value: value["themes"]["dark"].update(mist="#000000"),
            lambda value: value["fonts"][0].update(file="../../secret.ttf"),
            lambda value: value.update(unknown={}),
            lambda value: value["themes"]["dark"].pop("danger"),
        ]
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                value = copy.deepcopy(self.data)
                mutate(value)
                with self.assertRaises(ValueError):
                    GENERATOR.outputs(value)

    def test_generation_and_drift_work_in_an_isolated_checkout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "kufr/design-system/tokens.json"
            source.parent.mkdir(parents=True)
            source.write_text(json.dumps(self.data), encoding="utf-8")
            for path, content in GENERATOR.outputs(self.data, root).items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            self.assertEqual([], GENERATOR.check(root))
            css = root / "kufr/design-system/tokens/palette.css"
            generated = css.read_text(encoding="utf-8")
            self.assertIn("[data-theme='system']", generated)
            self.assertIn("@media (prefers-color-scheme: light)", generated)
            css.write_text(generated.replace("#EEF1F5", "#000000"), encoding="utf-8")
            self.assertEqual([str(css.relative_to(root))], GENERATOR.check(root))


if __name__ == "__main__":
    unittest.main()
