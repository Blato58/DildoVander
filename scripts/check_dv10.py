"""Offline DV10 guidance, route and artifact-reference checks; no provider calls."""
from pathlib import Path
import argparse
import hashlib
import html
import re
import sys

ROOT = Path(__file__).resolve().parents[1]


def check(web_dist=None):
    checked = 0
    files = [ROOT / "kufr/docs/dv10/README.md", ROOT / "kufr/docs/dv10/VPS_HANDOFF.md",
             ROOT / "kufr/docs/dv10/FIRMWARE_OUTCOME.md", ROOT / "kufr/docs/dv10/DELIVERY.md", ROOT / "kufr/docs/dv10/STATIC_EVENING.md"]
    skills = [ROOT / ".agents/skills" / ("dildovander-keepsake-" + name) / "SKILL.md"
              for name in ("design", "eval", "delivery")]
    for path in files + skills:
        source = path.read_text(encoding="utf-8-sig")
        if path.name == "SKILL.md":
            if not source.startswith("---\n") or not re.search(r"^name: [a-z0-9-]+$", source, re.M) or not re.search(r"^description: .+", source, re.M):
                raise ValueError("Invalid skill frontmatter: " + str(path.relative_to(ROOT)))
        for target in re.findall(r"\]\(([^)]+)\)", source):
            if target.startswith(("https://", "http://", "#")):
                continue
            destination = (path.parent / target.split("#", 1)[0]).resolve()
            if not destination.is_relative_to(ROOT) or not destination.is_file():
                raise ValueError("Missing/unsafe guidance reference: " + str(path.relative_to(ROOT)) + " -> " + target)
            checked += 1
    shell = (ROOT / "kufr/mobilni-aplikace/index.html").read_text(encoding="utf-8")
    capture = re.search(r"<script>(.*?)</script>", shell, re.S)
    first_asset = re.search(r"<(?:link\b[^>]*href|script\b[^>]*src)", shell, re.I)
    if not capture or (first_asset and capture.end() > first_asset.start()):
        raise ValueError("Fragment capture must precede asset delivery")
    if "history.replaceState" not in capture.group(1) or "location.hash" not in capture.group(1):
        raise ValueError("Fragment capture must remove the capability before module initialization")
    if web_dist is not None:
        import base64
        built = (web_dist / "index.html").read_text(encoding="utf-8")
        scripts = re.findall(r"<script(?:\s[^>]*)?>(.*?)</script>", built, re.S)
        built_capture = [script for script in scripts if "location.hash" in script]
        if len(built_capture) != 1:
            raise ValueError("Compiled app must contain one fragment capture")
        digest = base64.b64encode(hashlib.sha256(built_capture[0].encode()).digest()).decode()
        if "'sha256-" + digest + "'" not in html.unescape(built):
            raise ValueError("Compiled fragment bootstrap CSP hash mismatch")
    print(f"DV10 offline guidance verified: {checked} local references, three skills and early fragment capture. No AI/runtime/deploy claim.")
    if web_dist is not None:
        print("Compiled application fragment CSP hash verified.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--web-dist", type=Path, help="Also verify the compiled application fragment CSP hash")
    args = parser.parse_args()
    try:
        check(args.web_dist)
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
