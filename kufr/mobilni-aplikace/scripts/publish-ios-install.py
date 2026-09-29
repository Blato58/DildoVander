#!/usr/bin/env python3
"""Publish a verified signed IPA and Safari manifest to the kufr HTTPS site."""

import argparse
import hashlib
import html
import ipaddress
import os
import plistlib
import re
import shutil
import tempfile
import zipfile
from pathlib import Path
from urllib.parse import quote


MAX_IPA_BYTES = 250_000_000
RELEASE_ID_PATTERN = re.compile(r"[0-9]+-[0-9]+\Z")
INFO_PATH_PATTERN = re.compile(r"Payload/[^/]+\.app/Info\.plist\Z")


def read_ipa_metadata(ipa_path: Path, expected_bundle_id: str) -> tuple[str, str]:
    if not ipa_path.is_file() or not 0 < ipa_path.stat().st_size <= MAX_IPA_BYTES:
        raise ValueError("The signed IPA is missing or exceeds its size limit")
    with zipfile.ZipFile(ipa_path) as archive:
        info_paths = [name for name in archive.namelist() if INFO_PATH_PATTERN.fullmatch(name)]
        if len(info_paths) != 1:
            raise ValueError("The IPA must contain exactly one app Info.plist")
        if archive.getinfo(info_paths[0]).file_size > 1_000_000:
            raise ValueError("The app Info.plist is too large")
        info = plistlib.loads(archive.read(info_paths[0]))

    if info.get("CFBundleIdentifier") != expected_bundle_id:
        raise ValueError("The IPA Bundle ID does not match the signing profile")
    version = info.get("CFBundleShortVersionString")
    build = info.get("CFBundleVersion")
    if not isinstance(version, str) or not version or not isinstance(build, str) or not build:
        raise ValueError("The IPA has no version or build number")
    return version, build


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def render_page(version: str, build: str, manifest_url: str, ipa_url: str) -> str:
    install_url = "itms-services://?action=download-manifest&url=" + quote(
        manifest_url, safe=""
    )
    version_label = html.escape(version, quote=True)
    build_label = html.escape(build, quote=True)
    return f"""<!doctype html>
<html lang="cs">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Kufr · instalace pro iPhone</title>
  <style>
    :root {{ color-scheme: dark; font-family: -apple-system, BlinkMacSystemFont, system-ui, sans-serif; }}
    body {{ margin: 0; padding: 1.5rem; background: radial-gradient(circle at 80% 0%, #493066, #17101e 52%); color: #f8f2ff; min-height: 100vh; }}
    main {{ max-width: 44rem; margin: min(14vh, 7rem) auto; }}
    .eyebrow {{ color: #c7a2f2; font-size: .82rem; font-weight: 700; letter-spacing: .14em; text-transform: uppercase; }}
    h1 {{ font-size: clamp(2.7rem, 10vw, 5rem); line-height: 1.03; margin: .7rem 0 1rem; }}
    p {{ color: #d6c9df; line-height: 1.6; }}
    .card {{ background: #ffffff12; border: 1px solid #ffffff2a; border-radius: 1.25rem; padding: clamp(1.3rem, 5vw, 2rem); margin-top: 2rem; }}
    .button {{ display: inline-block; background: #d4a7ff; color: #21102e; padding: .95rem 1.3rem; border-radius: .8rem; font-weight: 800; text-decoration: none; margin: .8rem 0; }}
    .button:focus-visible, a:focus-visible {{ outline: 3px solid #fff; outline-offset: 3px; }}
    .meta {{ color: #ae9abb; font-size: .92rem; }}
    a {{ color: #e2bdff; }}
    code {{ overflow-wrap: anywhere; }}
  </style>
</head>
<body>
  <main>
    <div class="eyebrow">Soukromý test · iPhone 15 Pro</div>
    <h1>Kufr do kapsy.</h1>
    <p>Otevři tuto stránku v Safari na registrovaném iPhonu a nainstaluj podepsanou aplikaci.</p>
    <section class="card" aria-label="Instalace aplikace">
      <div class="meta">Verze {version_label} · sestavení {build_label}</div>
      <a class="button" href="{html.escape(install_url, quote=True)}">Nainstalovat na iPhone</a>
      <p>Po klepnutí potvrď instalaci. Pokud ji iPhone odmítne, ověř vývojářský režim a profil zařízení.</p>
      <p class="meta">Diagnostika: <a href="{html.escape(ipa_url, quote=True)}">stáhnout IPA</a> · <a href="{html.escape(manifest_url, quote=True)}">otevřít manifest</a>.</p>
    </section>
  </main>
</body>
</html>
"""


def publish(
    ipa_path: Path,
    checksum_path: Path,
    site_dir: Path,
    public_ip: str,
    release_id: str,
    bundle_id: str,
) -> str:
    address = ipaddress.IPv4Address(public_ip)
    if not address.is_global or not RELEASE_ID_PATTERN.fullmatch(release_id):
        raise ValueError("A public IPv4 and numeric release ID are required")
    if site_dir.is_symlink() or not site_dir.is_dir():
        raise ValueError("The iOS site must be installed before publishing")

    checksum_parts = checksum_path.read_text(encoding="ascii").split()
    expected_sha = checksum_parts[0].lower() if checksum_parts else ""
    if not re.fullmatch(r"[0-9a-f]{64}", expected_sha):
        raise ValueError("Invalid IPA checksum")
    if sha256_file(ipa_path) != expected_sha:
        raise ValueError("The signed IPA checksum does not match")
    version, build = read_ipa_metadata(ipa_path, bundle_id)

    releases_dir = site_dir / "releases"
    releases_dir.mkdir(mode=0o755, exist_ok=True)
    if releases_dir.is_symlink():
        raise ValueError("The releases directory must not be a symlink")
    release_dir = releases_dir / release_id
    if release_dir.exists():
        raise ValueError("This release was already published")
    base_url = f"https://{address}/ios/releases/{release_id}"
    ipa_url = f"{base_url}/kufr.ipa"
    manifest_url = f"{base_url}/manifest.plist"
    manifest = {
        "items": [{
            "assets": [{"kind": "software-package", "url": ipa_url}],
            "metadata": {
                "bundle-identifier": bundle_id,
                "bundle-version": build,
                "kind": "software",
                "title": "Kufr",
            },
        }]
    }

    stage_dir = Path(tempfile.mkdtemp(prefix=".stage-", dir=releases_dir))
    try:
        shutil.copyfile(ipa_path, stage_dir / "kufr.ipa")
        (stage_dir / "kufr.ipa.sha256").write_text(
            f"{expected_sha}  kufr.ipa\n", encoding="ascii"
        )
        (stage_dir / "manifest.plist").write_bytes(plistlib.dumps(manifest, sort_keys=False))
        page = render_page(version, build, manifest_url, ipa_url)
        (stage_dir / "index.html").write_text(page, encoding="utf-8")
        for entry in stage_dir.iterdir():
            entry.chmod(0o644)
        stage_dir.chmod(0o755)
        os.replace(stage_dir, release_dir)
    finally:
        if stage_dir.exists():
            shutil.rmtree(stage_dir)

    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=site_dir,
                                     prefix=".index-", delete=False) as target:
        target.write(page)
        temporary_index = Path(target.name)
    temporary_index.chmod(0o644)
    os.replace(temporary_index, site_dir / "index.html")
    return f"https://{address}/ios/"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ipa", type=Path, required=True)
    parser.add_argument("--checksum", type=Path, required=True)
    parser.add_argument("--site-dir", type=Path, required=True)
    parser.add_argument("--public-ip", required=True)
    parser.add_argument("--release-id", required=True)
    parser.add_argument("--bundle-id", required=True)
    args = parser.parse_args()
    url = publish(args.ipa, args.checksum, args.site_dir, args.public_ip,
                  args.release_id, args.bundle_id)
    print(f"iOS installation page published at {url}")


if __name__ == "__main__":
    main()
