"""The standalone package must preserve styles, font bytes and source fingerprints."""
import base64
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import viewer_style


class ViewerStyleTests(unittest.TestCase):
    def test_nested_import_keeps_each_files_asset_origin(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "tokens").mkdir()
            (root / "fonts").mkdir()
            (root / "fonts" / "sample.ttf").write_bytes(b"example-font")
            (root / "entry.css").write_text('@import url("tokens/type.css");\n:root { --aw-void: #0D1218; }')
            (root / "tokens" / "type.css").write_text('@font-face { src: url("../fonts/sample.ttf") }')
            flattened = viewer_style.flatten_stylesheet(root / "entry.css", root)
            self.assertNotIn("@import", flattened)
            self.assertIn("--aw-void", flattened)
            self.assertIn("data:font/ttf;base64," + base64.b64encode(b"example-font").decode(), flattened)

    def test_snapshot_preserves_mtime_and_standalone_copy_without_shared_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            entry, snapshot = root / "entry.css", root / "snapshot.css"
            entry.write_text(":root { --aw-brass: #D9B777; }")
            with patch.object(viewer_style, "SHARED_ENTRY", entry), patch.object(viewer_style, "SNAPSHOT", snapshot):
                viewer_style.snapshot_styles()
                content, timestamp = snapshot.read_bytes(), snapshot.stat().st_mtime_ns
                viewer_style.snapshot_styles()
                self.assertEqual(snapshot.stat().st_mtime_ns, timestamp)
                entry.unlink()
                viewer_style.snapshot_styles()
                self.assertEqual(snapshot.read_bytes(), content)

    def test_circular_or_external_asset_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            entry = root / "entry.css"
            entry.write_text('@import "entry.css";')
            with self.assertRaisesRegex(ValueError, "Circular"):
                viewer_style.flatten_stylesheet(entry, root)
            entry.write_text('div { background: url("../outside.png") }')
            with self.assertRaisesRegex(ValueError, "escapes"):
                viewer_style.flatten_stylesheet(entry, root)

    def test_font_licenses_are_kept_in_offline_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "fonts").mkdir()
            (root / "fonts" / "sample-OFL.txt").write_text("Copyright Example. SIL OPEN FONT LICENSE.")
            entry, snapshot = root / "entry.css", root / "snapshot.css"
            entry.write_text(":root { color: ivory }")
            with patch.object(viewer_style, "SHARED_ENTRY", entry), patch.object(viewer_style, "SNAPSHOT", snapshot):
                viewer_style.snapshot_styles()
                self.assertIn("Copyright Example. SIL OPEN FONT LICENSE.", snapshot.read_text())


if __name__ == "__main__":
    unittest.main()
