"""Build the public site only from source-bound, verified CAD exports."""
import argparse
import hashlib
import html
import json
import re
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import zipfile


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


def verified_exports(root):
    out = root / 'exports'
    report = json.loads((out / 'verification.json').read_bytes())
    manifest = json.loads((out / 'manifest.json').read_bytes())
    if report['result'] != 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE' or manifest['sources'] != report['sources']:
        raise ValueError('CAD source verification is missing')
    for name, expected in report['sources'].items():
        if digest(safe_member(root, name)) != expected:
            raise ValueError('CAD sources changed after verification: ' + name)
    hashes = json.loads((out / 'artifact-sha256.json').read_bytes())
    verified = report['export_sha256']
    if hashes != {**verified, 'verification.json': digest(out / 'verification.json')}:
        raise ValueError('Export index differs from verification snapshot')
    for name, expected in hashes.items():
        if digest(safe_member(out, name)) != expected:
            raise ValueError('Export changed after verification: ' + name)
    checksum, package_name = (out / 'package-sha256.txt').read_text().split()
    package = safe_member(out, package_name)
    if package_name != manifest['revision'] + '-kouzelny-kufr.zip' or digest(package) != checksum:
        raise ValueError('Manufacturing ZIP checksum or revision differs')
    expected_members = {**report['sources'], **{'exports/' + key: value for key, value in hashes.items()},
                        'exports/artifact-sha256.json': digest(out / 'artifact-sha256.json')}
    with zipfile.ZipFile(package) as archive:
        if len(archive.namelist()) != len(expected_members) or set(archive.namelist()) != set(expected_members):
            raise ValueError('Manufacturing ZIP inventory differs')
        for name, expected in expected_members.items():
            if hashlib.sha256(archive.read(name)).hexdigest() != expected:
                raise ValueError('Manufacturing ZIP readback differs: ' + name)
    return manifest, sorted([*hashes, 'artifact-sha256.json', package_name, 'package-sha256.txt'])


def build(repo, destination, web_dist):
    if destination.exists():
        raise ValueError('Build destination must be new')
    root = repo / 'kufr/mechanika/final'
    manifest, exports = verified_exports(root)
    source_sha = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
    destination.mkdir(parents=True)
    workshop = destination / 'workshop'
    workshop.mkdir()
    for name in exports:
        target = workshop / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(root / 'exports' / name, target)
    shutil.copyfile(workshop / 'viewer.html', workshop / 'index.html')
    shutil.copytree(repo / 'kufr/design-system', destination / 'design-system',
                    ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    shutil.copytree(web_dist, destination / 'companion')
    companion_index = destination / 'companion/index.html'
    companion_html = companion_index.read_text(encoding='utf-8')
    companion_index.write_text(companion_html.replace('</head>',
        '<meta name="kufr-browser-mode" content="local-fake"></head>'), encoding='utf-8')
    simulator = destination / 'simulator'
    simulator.mkdir()
    dashboard = (repo / 'kufr/simulator/dashboard.html').read_text(encoding='utf-8')
    dashboard, count = re.subn(r'<body\b([^>]*)>', r'<body\1 data-static-preview>', dashboard, count=1)
    if count != 1:
        raise ValueError('Simulator preview cannot be safely marked')
    (simulator / 'index.html').write_text(dashboard, encoding='utf-8')
    shutil.copyfile(repo / 'kufr/simulator/dashboard.js', simulator / 'dashboard.js')
    package_name = manifest['revision'] + '-kouzelny-kufr.zip'
    substitutions = {'__REVISION__': manifest['revision'], '__PACKAGE_NAME__': package_name,
                     '__PACKAGE_SIZE__': f'{(workshop / package_name).stat().st_size / 1048576:.1f}',
                     '__SOURCE_SHORT__': source_sha[:8]}
    homepage = (repo / 'kufr/web/index.html').read_text(encoding='utf-8')
    for marker, value in substitutions.items():
        homepage = homepage.replace(marker, html.escape(value))
    (destination / 'index.html').write_text(homepage, encoding='utf-8')
    shutil.copyfile(repo / 'kufr/web/site.css', destination / 'site.css')
    hashes = {path.relative_to(destination).as_posix(): digest(path)
              for path in sorted(destination.rglob('*')) if path.is_file()}
    receipt = {'schema': 1, 'source_sha': source_sha, 'revision': manifest['revision'],
               'cad_result': 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', 'package': package_name, 'sha256': hashes}
    (destination / 'site-manifest.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    print(f'Built {manifest["revision"]} site from {source_sha}: {len(hashes)} files')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--web-dist', type=Path, required=True)
    args = parser.parse_args()
    build(Path(__file__).resolve().parents[2], args.destination.resolve(), args.web_dist.resolve())
