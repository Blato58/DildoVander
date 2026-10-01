#!/usr/bin/env python3
"""Bundle build outputs with provenance; validate checksums and safe extraction."""
import argparse
import hashlib
import json
import os
import shutil
from pathlib import Path, PurePosixPath
import subprocess
import tarfile


def sha256_file(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def git_revision(ref):
    return subprocess.check_output(['git', 'rev-parse', ref], text=True).strip()


def pack(source, destination):
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination / 'payload.tar.gz'
    if not source.is_dir() or not any(source.iterdir()):
        raise ValueError('Build output must be a non-empty directory')
    with tarfile.open(archive, 'w:gz') as bundle:
        for entry in sorted(source.iterdir()):
            bundle.add(entry, arcname=entry.name)
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    pr = event.get('pull_request', {})
    manifest = {
        'schema': 1,
        'repository': os.environ['GITHUB_REPOSITORY'],
        'run_id': int(os.environ['GITHUB_RUN_ID']),
        'run_attempt': int(os.environ['GITHUB_RUN_ATTEMPT']),
        'event': os.environ['GITHUB_EVENT_NAME'],
        'source_sha': git_revision('HEAD'),
        'source_tree': git_revision('HEAD^{tree}'),
        'head_sha': pr.get('head', {}).get('sha', git_revision('HEAD')),
        'base_sha': pr.get('base', {}).get('sha'),
        'pr_number': pr.get('number'),
        'payload_sha256': sha256_file(archive),
    }
    (destination / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


def unpack(source, destination):
    manifest = json.loads((source / 'build-manifest.json').read_text())
    archive = source / 'payload.tar.gz'
    checksum = sha256_file(archive)
    if checksum != manifest['payload_sha256']:
        raise ValueError('Build payload checksum mismatch')
    with tarfile.open(archive, 'r:gz') as bundle:
        members = bundle.getmembers()
        if not members:
            raise ValueError('Empty build payload')
        names = set()
        for member in members:
            path = PurePosixPath(member.name)
            if (path.is_absolute() or '..' in path.parts or not path.parts
                    or not (member.isfile() or member.isdir()) or path.as_posix() in names):
                raise ValueError('Unsafe or duplicate archive entry')
            names.add(path.as_posix())
        # Fresh destination prevents pre-existing symlinks or stale release files.
        destination.mkdir(parents=True, exist_ok=False)
        # Extract only validated regular files/directories; strip setuid/ownership.
        # This also works with the Python shipped on older Mac/server runners.
        for member in members:
            target = destination / member.name
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.extractfile(member) as source_stream, target.open('xb') as output:
                    shutil.copyfileobj(source_stream, output)
                target.chmod(member.mode & 0o777)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['pack', 'unpack'])
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    args = parser.parse_args()
    globals()[args.command](args.source, args.destination)
