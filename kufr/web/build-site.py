"""Package the compiled application independently of CAD and model releases."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def safe_member(root, name):
    relative = PurePosixPath(name)
    if not name or relative.is_absolute() or '..' in relative.parts or '\\' in name:
        raise ValueError('Unsafe member: ' + name)
    path = root / name
    if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError('Missing or unsafe file: ' + name)
    return path


def build(repo, destination, web_dist):
    """Package exactly one compiled application; model releases are independent."""
    if destination.exists():
        raise ValueError('Build destination must be new')
    if web_dist.is_symlink() or not web_dist.is_dir():
        raise ValueError('Application dist must be a real directory')
    if destination.resolve().is_relative_to(web_dist.resolve()):
        raise ValueError('Build destination must be outside application dist')
    safe_member(web_dist, 'index.html')
    files = []
    for path in sorted(web_dist.rglob('*')):
        name = path.relative_to(web_dist).as_posix()
        if path.is_symlink():
            raise ValueError('Symlinks cannot be published: ' + name)
        if path.is_dir():
            continue
        safe_member(web_dist, name)
        if any(part.startswith('.') or part in {'node_modules', '__pycache__'}
               for part in path.relative_to(web_dist).parts):
            raise ValueError('Private or development file in application dist: ' + name)
        if path.suffix.lower() in {'.pem', '.key', '.pfx', '.p12', '.sqlite', '.sqlite-wal', '.sqlite-shm', '.db'}:
            raise ValueError('Private material in application dist: ' + name)
        if name == 'site-manifest.json':
            raise ValueError('Compiled dist contains a reserved release manifest')
        if path.suffix.lower() == '.html' and name != 'index.html':
            raise ValueError('Hosted release must have one application entry: ' + name)
        files.append(name)
    source_sha = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    source_dirty = bool(subprocess.check_output([
        'git', '-C', str(repo), 'status', '--porcelain', '--',
        'kufr/mobilni-aplikace', 'kufr/contracts', 'kufr/design-system',
        'kufr/web/build-site.py',
        'kufr/mechanika/final/twin_core.js', 'kufr/mechanika/final/timeline_core.js',
        'kufr/mechanika/final/twin_media_core.js', 'kufr/mechanika/final/twin_media_controller.js',
        'kufr/mechanika/final/viewer_engine_core.js',
        'kufr/firmware/diagnostics/servo-bench/web/controller.mjs',
        'kufr/firmware/diagnostics/servo-bench/web/serial.mjs',
        'kufr/firmware/diagnostics/servo-bench/web/profiles.mjs',
        'kufr/firmware/diagnostics/servo-bench/web/auto-calibration.mjs',
        'kufr/firmware/diagnostics/servo-bench/web/mcp.mjs',
    ], text=True).strip())
    # Read every input before creating an output. A failed validation never leaves
    # something that can be mistaken for a complete release.
    payload = {name: safe_member(web_dist, name).read_bytes() for name in files}
    destination.mkdir(parents=True)
    for name, data in payload.items():
        target = destination / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    receipt = {
        'schemaVersion': 2, 'artifactType': 'application',
        'applicationSourceRevision': source_sha, 'apiContractVersion': 1,
        'applicationSourceDirty': source_dirty,
        'modelRelease': 'independent',
        'sha256': {name: hashlib.sha256(data).hexdigest() for name, data in payload.items()},
    }
    (destination / 'site-manifest.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(f'Built application {source_sha}: {len(files)} files; no CAD dependency')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--web-dist', type=Path, required=True)
    args = parser.parse_args()
    build(Path(__file__).resolve().parents[2], args.destination.resolve(), args.web_dist.resolve())
