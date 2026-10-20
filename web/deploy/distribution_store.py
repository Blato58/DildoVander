"""Publish existing distribution bytes independently of application/model activation.

The explicit manufacturing promotion verifies its recorded ZIP inventory; normal
application builds never import this module or inspect a manufacturing archive.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import tempfile
import zipfile

from release_store import atomic_json, checked_directory, publication_lock, validate_model

HASH = re.compile(r'^[a-f0-9]{64}$')
ID = re.compile(r'^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$')
FILENAME = re.compile(r'^[A-Za-z0-9][A-Za-z0-9_.-]{0,159}$')
EXTENSIONS = {'.zip', '.pdf', '.step', '.stp', '.stl', '.3mf', '.dxf', '.txt'}
KINDS = {'manufacturing', 'model-archive', 'offline-viewer'}
MAX_ARTIFACT = 2 * 1024 * 1024 * 1024
MAX_MODEL = 96 * 1024 * 1024 + 128 * 1024
ASSET_PATH = re.compile(r'^[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*(?:/[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*)*$')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(path):
    result = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(block)
    return result.hexdigest()


def file_name(value):
    require(isinstance(value, str) and FILENAME.fullmatch(value) is not None
            and Path(value).suffix in EXTENSIONS, 'Unsupported distribution filename')
    return value


def safe_file(root, name):
    file_name(name)
    path = root / name
    require(not root.is_symlink() and not path.is_symlink() and path.is_file()
            and path.resolve().parent == root.resolve(), 'Distribution input must be a confined real file')
    require(0 < path.stat().st_size <= MAX_ARTIFACT, 'Distribution input exceeds size limit')
    return path


def read_json(path):
    require(not path.is_symlink() and path.is_file() and 0 < path.stat().st_size <= 16 * 1024 * 1024,
            'Missing or excessive distribution evidence')
    return json.loads(path.read_text(encoding='utf-8'))


def hashes(value):
    require(isinstance(value, dict) and 0 < len(value) <= 10000, 'Invalid recorded distribution hashes')
    for name, checksum in value.items():
        require(isinstance(name, str) and 0 < len(name) <= 512 and not name.startswith('/')
                and '\\' not in name and all(part not in ('', '.', '..') for part in name.split('/'))
                and isinstance(checksum, str) and HASH.fullmatch(checksum), 'Unsafe recorded distribution hash')
    return value


def verify_manufacturing(source, artifact, metadata):
    report_path = source / 'verification.json'
    report = read_json(report_path)
    manifest = read_json(source / 'manifest.json')
    index_path = source / 'artifact-sha256.json'
    index = hashes(read_json(index_path))
    require(isinstance(report, dict) and isinstance(manifest, dict)
            and report.get('result') == 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE'
            and report.get('physical_test_performed') is False, 'Existing successful CAD verification is required')
    sources = hashes(report.get('sources'))
    exports = hashes(report.get('export_sha256'))
    require(manifest.get('sources') == sources and manifest.get('revision') == metadata['version'],
            'Manufacturing provenance or revision differs')
    require(artifact.name == manifest['revision'] + '-kouzelny-kufr.zip', 'Manufacturing archive name differs from recorded revision')
    require(index == {**exports, 'verification.json': sha256(report_path)}, 'Manufacturing export index differs from verified report')
    checksum_path = source / 'package-sha256.txt'
    require(not checksum_path.is_symlink() and checksum_path.is_file() and checksum_path.stat().st_size <= 512,
            'Missing original manufacturing package checksum')
    checksum = checksum_path.read_text(encoding='utf-8').split()
    require(len(checksum) == 2 and checksum[0] == sha256(artifact) and checksum[1] == artifact.name,
            'Manufacturing archive checksum differs')
    expected = {**sources, **{'exports/' + name: digest for name, digest in index.items()},
                'exports/artifact-sha256.json': sha256(index_path)}
    # Read back frozen members without extraction, CAD imports or source edits.
    with zipfile.ZipFile(artifact) as archive:
        entries = archive.infolist()
        require(len(entries) == len(expected) and {entry.filename for entry in entries} == set(expected)
                and sum(entry.file_size for entry in entries) <= 4 * MAX_ARTIFACT,
                'Manufacturing archive inventory differs')
        for entry in entries:
            digest = hashlib.sha256()
            with archive.open(entry) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b''):
                    digest.update(block)
            require(digest.hexdigest() == expected[entry.filename], 'Manufacturing archive member differs: ' + entry.filename)
    if metadata['modelAssetSha256'] is not None:
        evidence = report.get('modelEvidence')
        require(isinstance(evidence, dict) and evidence.get('modelId') == metadata['modelId']
                and evidence.get('modelAssetSha256') == metadata['modelAssetSha256'],
                'Legacy archive has no evidence binding it to the selected model bytes')


def metadata_value(metadata):
    require(isinstance(metadata, dict) and set(metadata) <= {'kind', 'modelId', 'modelAssetSha256', 'bundleHash', 'label', 'version'},
            'Invalid distribution metadata fields')
    for key in ('kind', 'modelId', 'label', 'version'):
        require(isinstance(metadata.get(key), str) and 0 < len(metadata[key]) <= 160
                and all(ord(character) >= 32 for character in metadata[key]), 'Invalid distribution metadata: ' + key)
    require(metadata['kind'] in KINDS and ID.fullmatch(metadata['modelId']) and ID.fullmatch(metadata['version']),
            'Invalid distribution kind or identity')
    result = dict(metadata)
    result.setdefault('modelAssetSha256', None)
    require(result['modelAssetSha256'] is None and result['kind'] == 'manufacturing'
            or isinstance(result['modelAssetSha256'], str) and HASH.fullmatch(result['modelAssetSha256']),
            'A matching model content hash is required for this distribution')
    if 'bundleHash' in result:
        require(isinstance(result['bundleHash'], str) and HASH.fullmatch(result['bundleHash']), 'Invalid model bundle reference')
    return result


def verify_model_archive(artifact, metadata):
    """Validate inert bundle files through the application's maintained loader."""
    with tempfile.TemporaryDirectory(prefix='distribution-model-') as temporary:
        directory = Path(temporary)
        with zipfile.ZipFile(artifact) as archive:
            entries = archive.infolist()
            names = [entry.filename.rstrip('/') if entry.is_dir() else entry.filename for entry in entries]
            require(0 < len(entries) <= 128 and len(set(name.lower() for name in names)) == len(names)
                    and sum(entry.file_size for entry in entries) <= MAX_MODEL,
                    'Invalid model archive inventory or size')
            for entry, name in zip(entries, names):
                mode = entry.external_attr >> 16
                require(len(name) <= 240 and ASSET_PATH.fullmatch(name) and not entry.flag_bits & 1
                        and not stat.S_ISLNK(mode) and (stat.S_IFMT(mode) in (0, stat.S_IFREG, stat.S_IFDIR)),
                        'Unsafe model archive entry')
                target = directory / name
                if entry.is_dir():
                    require(entry.file_size == 0, 'Invalid model archive directory')
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(entry) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output, 1024 * 1024)
                    require(target.stat().st_size == entry.file_size, 'Model archive entry size differs')
        manifest = validate_model(directory)
        model = next(asset for asset in manifest['assets'] if asset['kind'] == 'model-json')
        require(metadata['modelId'] == manifest['modelId'] and metadata['modelAssetSha256'] == model['sha256']
                and ('bundleHash' not in metadata or metadata['bundleHash'] == manifest['bundleHash']),
                'Distribution model claim differs from validated archive bytes')


def descriptor(value):
    require(isinstance(value, dict) and set(value) <= {'kind', 'modelId', 'modelAssetSha256', 'bundleHash', 'label', 'version',
            'releaseId', 'path', 'sha256', 'bytes', 'cadVerification', 'physicalAcceptance'}, 'Invalid distribution descriptor')
    require({'kind', 'modelId', 'modelAssetSha256', 'label', 'version', 'releaseId', 'path', 'sha256', 'bytes',
             'cadVerification', 'physicalAcceptance'} <= set(value), 'Missing distribution descriptor fields')
    metadata_value({key: value[key] for key in ('kind', 'modelId', 'modelAssetSha256', 'bundleHash', 'label', 'version') if key in value})
    checksum = value.get('sha256')
    require(isinstance(checksum, str) and HASH.fullmatch(checksum) and value.get('releaseId') == checksum
            and type(value.get('bytes')) is int and 0 < value['bytes'] <= MAX_ARTIFACT
            and value.get('cadVerification') in ('verified', 'not-verified') and value.get('physicalAcceptance') is False,
            'Invalid distribution descriptor provenance')
    path = value.get('path')
    require(isinstance(path, str) and path.startswith('releases/' + checksum + '/'), 'Invalid distribution path')
    name = path.removeprefix('releases/' + checksum + '/')
    file_name(name)
    return value


def publish(source, name, root, metadata, legacy_alias=True):
    metadata = metadata_value(metadata)
    artifact = safe_file(source, name)
    checksum = sha256(artifact)
    if metadata['kind'] == 'manufacturing':
        require(artifact.suffix.lower() == '.zip', 'Manufacturing distribution must be a ZIP')
        verify_manufacturing(source, artifact, metadata)
    else:
        require(artifact.suffix.lower() == '.zip', 'Model and offline viewer compositions must be ZIP downloads')
        require(metadata['kind'] != 'offline-viewer',
                'Bound offline publication is unsupported: executable HTML and a loose receipt do not prove embedded model identity')
        verify_model_archive(artifact, metadata)
    require(sha256(artifact) == checksum, 'Distribution input changed during validation')
    value = descriptor({**metadata, 'releaseId': checksum, 'path': f'releases/{checksum}/{name}', 'sha256': checksum,
                        'bytes': artifact.stat().st_size, 'cadVerification': 'verified' if metadata['kind'] == 'manufacturing' else 'not-verified',
                        'physicalAcceptance': False})
    with publication_lock(root):
        index_path = root / 'index.json'
        previous = read_json(index_path) if index_path.exists() else {'schemaVersion': 1, 'artifacts': []}
        require(isinstance(previous, dict) and set(previous) == {'schemaVersion', 'artifacts'} and type(previous['schemaVersion']) is int and previous['schemaVersion'] == 1
                and isinstance(previous['artifacts'], list) and len(previous['artifacts']) <= 1000, 'Invalid durable distribution index')
        existing = [descriptor(entry) for entry in previous['artifacts']]
        for entry in existing:
            if entry['releaseId'] == checksum:
                require(entry == value, 'Immutable distribution already has different metadata')
        alias = None
        if metadata['kind'] == 'manufacturing' and legacy_alias:
            checked_directory(root, Path('legacy/workshop'))
            alias = root / 'legacy/workshop' / name
            require(not alias.is_symlink() and (not alias.exists() or alias.is_file() and sha256(alias) == checksum), 'Existing legacy URL is immutable; publish a new URL without replacing it')
        target = checked_directory(root, Path('releases') / checksum) / name
        require(not target.is_symlink(), 'Distribution target must not be a symlink')
        if target.exists():
            require(target.is_file() and sha256(target) == checksum, 'Immutable distribution bytes differ')
        else:
            with tempfile.TemporaryDirectory(prefix='.distribution-', dir=root) as temporary:
                staging = Path(temporary) / name
                shutil.copyfile(artifact, staging)
                require(sha256(staging) == checksum, 'Staged distribution readback differs')
                staging.replace(target)
        if alias is not None and not alias.exists():
            # Same-filesystem hard link retains the exact original bytes and URL.
            alias.hardlink_to(target)
        if not any(entry['releaseId'] == checksum for entry in existing):
            require(len(existing) < 1000, 'Distribution index limit reached')
            atomic_json(root / 'previous-index.json', previous)
            atomic_json(index_path, {'schemaVersion': 1, 'artifacts': [*existing, value]})
        return value


def rollback(root):
    with publication_lock(root):
        previous = read_json(root / 'previous-index.json')
        require(isinstance(previous, dict) and set(previous) == {'schemaVersion', 'artifacts'}
                and type(previous['schemaVersion']) is int and previous['schemaVersion'] == 1
                and isinstance(previous['artifacts'], list) and len(previous['artifacts']) <= 1000,
                'Invalid previous distribution index')
        for value in previous['artifacts']:
            descriptor(value)
            target = root / value['path']
            require(not (root / 'releases').is_symlink() and not target.parent.is_symlink()
                    and not target.is_symlink() and target.is_file() and sha256(target) == value['sha256'],
                    'Previous distribution bytes are unavailable or changed')
        current = read_json(root / 'index.json')
        atomic_json(root / 'previous-index.json', current)
        atomic_json(root / 'index.json', previous)
        return previous


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('publish', 'rollback'), nargs='?', default='publish')
    parser.add_argument('--source', type=Path)
    parser.add_argument('--file')
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--metadata', type=Path)
    parser.add_argument('--without-legacy-alias', action='store_true')
    args = parser.parse_args()
    if not args.root.is_absolute():
        parser.error('--root must be an explicitly provisioned absolute durable directory')
    if args.operation == 'rollback':
        result = rollback(args.root)
    else:
        if args.source is None or args.file is None or args.metadata is None:
            parser.error('--source, --file and --metadata are required for publication')
        result = publish(args.source, args.file, args.root, read_json(args.metadata), not args.without_legacy_alias)
    print(json.dumps(result, sort_keys=True))
