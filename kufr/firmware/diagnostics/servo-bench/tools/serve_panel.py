"""Serve only the bench panel and shared design assets on the loopback interface."""

import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit


BENCH_ROOT = Path(__file__).resolve().parents[1]
WEB_ROOT = BENCH_ROOT / "web"
DESIGN_ROOT = BENCH_ROOT.parents[2] / "design-system"


class PanelHandler(SimpleHTTPRequestHandler):
    extensions_map = {**SimpleHTTPRequestHandler.extensions_map, ".mjs": "text/javascript"}

    def translate_path(self, path):
        path = unquote(urlsplit(path).path)
        if path.startswith("/design-system/"):
            root = DESIGN_ROOT
            relative = path.removeprefix("/design-system/")
        else:
            root = WEB_ROOT
            relative = path.lstrip("/") or "index.html"
        try:
            target = (root / relative).resolve()
        except (OSError, ValueError):
            return str(WEB_ROOT / "__rejected_path__")
        # Resolve symlinks, encoded separators and Windows drive paths before
        # confinement. A rejected path points to a non-file under the web root.
        if not target.is_relative_to(root.resolve()):
            return str(WEB_ROOT / "__rejected_path__")
        return str(target)

    def list_directory(self, path):
        self.send_error(404, "Directory listing is disabled")
        return None

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'")
        self.send_header("Permissions-Policy", "serial=(self)")
        super().end_headers()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("port must be between 1 and 65535")
    with ThreadingHTTPServer(("127.0.0.1", args.port), partial(PanelHandler, directory=str(WEB_ROOT))) as server:
        print(f"Servo bench: http://127.0.0.1:{args.port}/", flush=True)
        print("Chrome/Edge; Ctrl+C stops the server. No serial port is opened by this server.", flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
