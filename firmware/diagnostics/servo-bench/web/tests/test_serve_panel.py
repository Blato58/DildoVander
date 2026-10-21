"""HTTP handler checks without starting a server or accessing a serial port."""

import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


MODULE_PATH = Path(__file__).resolve().parents[2] / "tools" / "serve_panel.py"
SPEC = importlib.util.spec_from_file_location("serve_panel", MODULE_PATH)
panel = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(panel)


class FakeSocket:
    def __init__(self, path):
        self.request = io.BytesIO(f"GET {path} HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n".encode())
        self.response = bytearray()

    def makefile(self, *args, **kwargs):
        return self.request

    def sendall(self, data):
        self.response.extend(data)


class PanelTests(unittest.TestCase):
    def get(self, path):
        socket = FakeSocket(path)
        with patch.object(panel.PanelHandler, "log_message"):
            panel.PanelHandler(socket, ("127.0.0.1", 12345), object())
        return bytes(socket.response)

    def test_real_panel_design_assets_and_security_headers(self):
        for path, expected in [
            ("/", b"Servo bench"),
            ("/controller.mjs", b"BenchController"),
            ("/panel-ui.mjs", b"mountBenchUi"),
            ("/music.mjs", b"MusicDance"),
            ("/music-panel.mjs", b"mountMusicPanel"),
            ("/design-system/ui.js", b"export function button"),
            ("/design-system/theme.js", b"DildoVanderTheme"),
            ("/design-system/arcane-workshop.css", b"palette.css"),
        ]:
            response = self.get(path)
            self.assertTrue(response.startswith(b"HTTP/1.0 200"), response[:200])
            self.assertIn(expected, response)
            self.assertIn(b"connect-src 'none'", response)
            self.assertIn(b"media-src 'self' blob:", response)
            self.assertIn(b"frame-ancestors 'none'", response)
            self.assertIn(b"X-Content-Type-Options: nosniff", response)

    def test_repository_paths_traversal_and_listings_are_rejected(self):
        for path in [
            "/README.md", "/../README.md", "/%2e%2e/README.md",
            "/%2e%2e%5cREADME.md", "/design-system/../docs/implementacni-plan.md",
            "/design-system/%2e%2e%2fdocs/implementacni-plan.md",
            "/design-system/", "/tests/", "/C:/Windows/win.ini",
        ]:
            response = self.get(path)
            self.assertTrue(response.startswith(b"HTTP/1.0 404"), (path, response[:200]))

    def test_symlinks_cannot_escape_served_roots(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            web = root / "web"
            web.mkdir()
            secret = root / "private.txt"
            secret.write_text("must never be served", encoding="utf-8")
            try:
                (web / "escape.txt").symlink_to(secret)
            except OSError as error:
                self.skipTest(f"Symlink creation unavailable: {error}")
            with patch.object(panel, "WEB_ROOT", web):
                response = self.get("/escape.txt")
            self.assertTrue(response.startswith(b"HTTP/1.0 404"))
            self.assertNotIn(b"must never be served", response)


if __name__ == "__main__":
    unittest.main()
