#!/usr/bin/env python3
"""Root-owned signer/publisher. Private signing keys never reach the runner."""
import importlib.util
import os
from pathlib import Path
import re
import stat
import sys
import tempfile


DESTINATION = Path('/opt/kufr-firmware')
RUNNER_TEMPS = tuple(Path('/opt') / name / '_work' / '_temp'
                     for name in ('actions-runner', 'actions-runner-2'))
KEYS = Path('/etc/kufr-backend')
TOOL = Path('/usr/local/lib/kufr-firmware/ota_release.py')
PARTITIONS = Path('/usr/local/lib/kufr-firmware/partitions_16mb.csv')


def source_path(value):
    source = Path(value)
    if not source.is_absolute() or not source.is_file() or source.is_symlink():
        raise ValueError('Source must be a regular runner artifact')
    resolved = source.resolve(strict=True)
    root = runner_directory(source)
    if not resolved.is_relative_to(root.resolve(strict=True)):
        raise ValueError('Source must be below the configured runner temporary directory')
    for entry in [source, *source.parents]:
        if entry.is_symlink():
            raise ValueError('Symlink source is not allowed')
    if not 288 <= source.stat().st_size <= 0x640000:
        raise ValueError('Invalid artifact size')
    return source


def publish(source, version, build_id, channel):
    if channel not in {'stable', 'development'} or not re.fullmatch(r'[0-9a-f]{40}', build_id):
        raise ValueError('Invalid channel/build identity')
    source = source_path(source)
    key = KEYS / ('ota-' + channel + '-private.pem')
    public = KEYS / ('ota-' + channel + '-public.pem')
    for path in [key, public, TOOL, PARTITIONS]:
        if not path.is_file() or path.is_symlink() or path.stat().st_uid != 0 or path.stat().st_mode & 0o022:
            raise ValueError('Signing configuration must be root-owned and immutable to other users')
    if key.stat().st_mode & 0o077:
        raise ValueError('Private signing key must have mode 0600')
    specification = importlib.util.spec_from_file_location('ota_release', TOOL)
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    # Snapshot untrusted runner bytes once, before validation/signing. A runner
    # cannot swap the input between hash, signing and the copy to public storage.
    with tempfile.TemporaryDirectory(prefix='kufr-firmware-publish-') as temporary:
        root = Path(temporary)
        snapshot = root / 'firmware.bin'
        snapshot.write_bytes(read_source(source))
        release = root / 'release'
        metadata = module.package(snapshot, PARTITIONS, version, build_id, channel, key, release)
        module.verify(release)
        if (release / 'trust.pem').read_bytes() != public.read_bytes():
            raise ValueError('Configured public key does not match the signer')
        if DESTINATION.is_symlink() or DESTINATION.parent.is_symlink():
            raise ValueError('Unsafe distribution directory')
        DESTINATION.mkdir(mode=0o755, exist_ok=True)
        for folder in ['artifacts', 'manifests']:
            target = DESTINATION / folder
            if target.is_symlink():
                raise ValueError('Unsafe distribution subdirectory')
            target.mkdir(mode=0o755, exist_ok=True)
        artifact = metadata['sha256'] + '.bin'
        destination = DESTINATION / 'artifacts' / artifact
        data = (release / 'artifacts' / artifact).read_bytes()
        if destination.exists() and (destination.is_symlink() or destination.read_bytes() != data):
            raise ValueError('Immutable artifact collision')
        if not destination.exists():
            atomic_write(destination, data)
        atomic_write(DESTINATION / ('trust-' + channel + '.pem'), public.read_bytes())
        atomic_write(DESTINATION / 'manifests' / (channel + '.json'),
                     (release / 'manifests' / (channel + '.json')).read_bytes())
        print(f"Published {channel} {version} {build_id} sha256={metadata['sha256']}")


def read_source(source):
    root = runner_directory(source)
    relative = source.relative_to(root)
    if '..' in relative.parts:
        raise ValueError('Invalid source path')
    # Resolve directory handles without following links; replacing a checked
    # parent path cannot redirect the privileged read to an unrelated file.
    directory = open_directory(root)
    descriptor = None
    try:
        for part in relative.parts[:-1]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory)
            directory = child
        descriptor = os.open(relative.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=directory)
        return snapshot_descriptor(descriptor)
    finally:
        if descriptor is not None:
            os.close(descriptor)
        os.close(directory)


def runner_directory(source):
    for directory in RUNNER_TEMPS:
        if source.is_relative_to(directory):
            return directory
    raise ValueError('Source must be below an approved runner temporary directory')


def open_directory(path):
    directory = os.open(path.anchor, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        for part in path.parts[1:]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=directory)
            os.close(directory)
            directory = child
        return directory
    except BaseException:
        os.close(directory)
        raise


def snapshot_descriptor(descriptor):
    before = os.fstat(descriptor)
    if not stat.S_ISREG(before.st_mode) or not 288 <= before.st_size <= 0x640000:
        raise ValueError('Invalid opened artifact')
    chunks = []
    remaining = 0x640000 + 1
    while remaining:
        chunk = os.read(descriptor, min(65536, remaining))
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    after = os.fstat(descriptor)
    data = b''.join(chunks)
    if len(data) != before.st_size or before.st_size != after.st_size or before.st_mtime_ns != after.st_mtime_ns:
        raise ValueError('Artifact changed while copying')
    return data


def atomic_write(path, data):
    if path.is_symlink():
        raise ValueError('Symlink destination is not allowed')
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        stream.write(data)
        temporary = Path(stream.name)
    try:
        temporary.chmod(0o644)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == '__main__':
    if len(sys.argv) != 5 or os.geteuid() != 0:
        raise SystemExit('Usage as root: kufr-firmware-publish BINARY VERSION BUILD_ID CHANNEL')
    publish(*sys.argv[1:])
