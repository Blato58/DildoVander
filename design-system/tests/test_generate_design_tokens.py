"""Executable schema and drift checks for the canonical token generator."""
import copy
import ast
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("generate_design_tokens", ROOT / "scripts/generate_design_tokens.py")
GENERATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GENERATOR)

STORYBOARD = '''<?xml version="1.0" encoding="UTF-8"?>
<document type="com.apple.InterfaceBuilder3.CocoaTouch.Storyboard.XIB" launchScreen="YES" initialViewController="controller-keep">
  <scenes><scene sceneID="scene-keep"><objects>
    <viewController id="controller-keep"><view key="view" id="view-keep">
      <rect key="frame" x="0" y="0" width="375" height="667"/>
      <constraints><constraint id="constraint-keep" firstAttribute="width" constant="375"/></constraints>
      <!-- Vapour Chrome dark surface (#000000), no template image. -->
      <color key="backgroundColor" red="0.0" green="0.0" blue="0.0" alpha="1" colorSpace="custom" customColorSpace="sRGB"/>
      <color key="tintColor" red="0.2" green="0.3" blue="0.4" alpha="1" colorSpace="custom" customColorSpace="sRGB"/>
    </view></viewController>
  </objects></scene></scenes>
</document>
'''


class DesignTokenTests(unittest.TestCase):
    def copy_runtime(self, design):
        for name in ("theme-runtime.js", "theme-controls.js"):
            (design / name).write_text((ROOT / "design-system" / name).read_text(encoding="utf-8"), encoding="utf-8")

    def setUp(self):
        self.data = json.loads((ROOT / "design-system/tokens.json").read_text(encoding="utf-8"))

    def test_committed_outputs_are_current(self):
        self.assertEqual([], GENERATOR.check(ROOT))

    def test_theme_labels_accept_original_localized_names_without_relaxing_identifiers(self):
        value = copy.deepcopy(self.data)
        value["themeRegistry"]["lord-voldemort"]["label"] = "Stínový čaroděj"
        value["themeRegistry"]["olivander"]["label"] = "Mistr hůlek"
        GENERATOR.validate_tokens(value)
        value["themeRegistry"]["stínový"] = copy.deepcopy(value["themeRegistry"]["olivander"])
        with self.assertRaises(ValueError):
            GENERATOR.validate_tokens(value)

    def test_theme_labels_reject_controls_markup_and_other_punctuation(self):
        for label in ["", "a" * 81, "Čaroděj\n", "Čaroděj\t", "Čaroděj\x00", "Čaroděj<svg>", "Čaroděj_", "Čaroděj’", "Čaroděj\u202e"]:
            with self.subTest(label=label):
                value = copy.deepcopy(self.data)
                value["themeRegistry"]["olivander"]["label"] = label
                with self.assertRaises(ValueError):
                    GENERATOR.validate_tokens(value)

    def test_rejects_malformed_and_injected_values(self):
        mutations = [
            lambda value: value.update(defaultTheme="olivander"),
            lambda value: value["themeRegistry"]["olivander"].update(label="<script>"),
            lambda value: value["themeRegistry"]["olivander"].update(palette="missing"),
            lambda value: value["themeRegistry"]["olivander"]["presentation"].update(touch="1px;display:none"),
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
            source = root / "design-system/tokens.json"
            source.parent.mkdir(parents=True)
            self.copy_runtime(source.parent)
            source.write_text(json.dumps(self.data), encoding="utf-8")
            for path, content in GENERATOR.outputs(self.data, root).items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            self.assertEqual([], GENERATOR.check(root))
            css = root / "design-system/tokens/palette.css"
            generated = css.read_text(encoding="utf-8")
            self.assertIn("[data-theme='system']", generated)
            self.assertIn("@media (prefers-color-scheme: light)", generated)
            css.write_text(generated.replace("#EEF1F5", "#000000"), encoding="utf-8")
            self.assertEqual([str(css.relative_to(root))], GENERATOR.check(root))

    def test_native_background_derives_only_rgb_and_preserves_interface_builder_source(self):
        source = STORYBOARD.replace("\n", "\r\n")
        midnight = "#123456"
        actual = GENERATOR.native_launch_background(source, midnight)
        expected = source.replace('#000000', midnight).replace(
            'red="0.0" green="0.0" blue="0.0"',
            f'red="{18 / 255}" green="{52 / 255}" blue="{86 / 255}"')
        self.assertEqual(expected, actual)
        self.assertEqual(actual, GENERATOR.native_launch_background(actual, midnight))

    def test_native_background_rejects_ambiguous_or_incompatible_documents(self):
        mutations = [
            STORYBOARD.replace('launchScreen="YES"', 'launchScreen="NO"'),
            STORYBOARD.replace('initialViewController="controller-keep"', 'initialViewController="other"'),
            STORYBOARD.replace('key="tintColor"', 'key="backgroundColor"'),
            STORYBOARD.replace('customColorSpace="sRGB"', 'customColorSpace="displayP3"'),
            STORYBOARD.replace('alpha="1"', 'alpha="0.5"'),
            STORYBOARD.replace('</view>', ''),
        ]
        for source in mutations:
            with self.subTest(source=source):
                with self.assertRaises(ValueError):
                    GENERATOR.native_launch_background(source, self.data["primitives"]["midnight"])

    def test_native_background_is_optional_in_fixture_checkouts_and_its_drift_is_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            token_source = root / "design-system/tokens.json"
            token_source.parent.mkdir(parents=True)
            self.copy_runtime(token_source.parent)
            token_source.write_text(json.dumps(self.data), encoding="utf-8")
            self.assertNotIn(root / GENERATOR.LAUNCH_STORYBOARD, GENERATOR.outputs(self.data, root))
            storyboard = root / GENERATOR.LAUNCH_STORYBOARD
            storyboard.parent.mkdir(parents=True)
            storyboard.write_bytes(STORYBOARD.replace("\n", "\r\n").encode("utf-8"))
            for path, content in GENERATOR.outputs(self.data, root).items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content.encode("utf-8"))
            self.assertEqual([], GENERATOR.check(root))
            storyboard.write_bytes(storyboard.read_bytes().replace(b'red="0.058823529411764705"', b'red="0.0"'))
            self.assertEqual([str(GENERATOR.LAUNCH_STORYBOARD)], GENERATOR.check(root))

    def installer_fixture(self, root):
        design = root / "design-system"
        (design / "components").mkdir(parents=True)
        (design / "tokens").mkdir()
        self.copy_runtime(design)
        (design / "tokens.json").write_text(json.dumps(self.data), encoding="utf-8")
        (design / "arcane-workshop.css").write_text("@import url('./tokens/palette.css');\n@import url('./tokens/typography.css');\n@import url('./components/patterns.css');\n", encoding="utf-8")
        # Deliberately stale on disk: generation must inline the newly computed tokens.
        (design / "tokens/palette.css").write_text("STALE PALETTE", encoding="utf-8")
        (design / "components/patterns.css").write_text(".aw-card { border-radius: var(--aw-radius-card); }\n/* quotes: 'single' and \\\"double\\\" */\n", encoding="utf-8")
        script = root / GENERATOR.INSTALLER_SCRIPT
        script.parent.mkdir(parents=True)
        source = "# editable prefix\n" + GENERATOR.INSTALLER_BEGIN + "\nINSTALLER_THEME_COLOR = ''\nINSTALLER_DESIGN_CSS = ''\n" + GENERATOR.INSTALLER_END + "\n# editable suffix\n"
        script.write_text(source, encoding="utf-8")
        return design, script, source

    def test_installer_embeds_fresh_tokens_and_component_changes_trigger_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            design, script, source = self.installer_fixture(root)
            generated = GENERATOR.outputs(self.data, root)
            publisher = generated[script]
            self.assertTrue(publisher.startswith("# editable prefix\n" + GENERATOR.INSTALLER_BEGIN))
            self.assertTrue(publisher.endswith(GENERATOR.INSTALLER_END + "\n# editable suffix\n"))
            namespace = {}
            exec(compile(publisher, "standalone-installer", "exec"), namespace)
            css = namespace["INSTALLER_DESIGN_CSS"]
            self.assertIn("--aw-midnight: #1B2430", css)
            self.assertIn(".aw-card", css)
            self.assertIn((design / "components/patterns.css").read_text(encoding="utf-8"), css)
            self.assertNotIn("STALE PALETTE", css)
            self.assertNotIn("@font-face", css)
            self.assertNotIn("url(", css)
            self.assertEqual("#0f0f11", namespace["INSTALLER_THEME_COLOR"])
            ast.parse(publisher)
            for path, content in generated.items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(content, encoding="utf-8")
            self.assertEqual([], GENERATOR.check(root))
            with (design / "components/patterns.css").open("a", encoding="utf-8") as stylesheet:
                stylesheet.write(".aw-new-state { color: var(--aw-accent); }\n")
            self.assertEqual([str(GENERATOR.INSTALLER_SCRIPT)], GENERATOR.check(root))

    def test_installer_rejects_bad_markers_traversal_cycles_and_remaining_assets(self):
        for source in ["", GENERATOR.INSTALLER_BEGIN, GENERATOR.INSTALLER_END + "\n" + GENERATOR.INSTALLER_BEGIN,
                       GENERATOR.INSTALLER_BEGIN + "\n" + GENERATOR.INSTALLER_BEGIN + "\n" + GENERATOR.INSTALLER_END]:
            with self.assertRaises(ValueError):
                GENERATOR.native_installer_design(source, "body {}", "#1B2430")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            design, script, source = self.installer_fixture(root)
            for css in ["@import url('../../outside.css');", "@import url('../arcane-workshop.css');", "@import url('https://example.invalid/style.css');", ".aw-card { background: url('../private.png'); }"]:
                (design / "components/patterns.css").write_text(css, encoding="utf-8")
                with self.subTest(css=css):
                    with self.assertRaises(ValueError):
                        GENERATOR.outputs(self.data, root)

    def test_generated_publisher_runs_standalone_and_retains_metadata_escaping(self):
        script = ROOT / GENERATOR.INSTALLER_SCRIPT
        namespace = {"__name__": "isolated_installer_fixture"}
        # No repository path or design folder is available to this standalone code.
        exec(compile(GENERATOR.read_output(script), "standalone-publish-ios-install.py", "exec"), namespace)
        page = namespace["render_page"]('<script>alert("version")</script>', '<b>build</b>', 'https://example.test/manifest.plist', 'https://example.test/app.ipa')
        self.assertIn("--aw-midnight: #1B2430", page)
        self.assertIn("&lt;script&gt;", page)
        self.assertIn("&lt;b&gt;build&lt;/b&gt;", page)
        self.assertNotIn('<script>alert("version")</script>', page)


if __name__ == "__main__":
    unittest.main()
