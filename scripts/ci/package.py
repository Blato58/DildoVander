#!/usr/bin/env python3
"""Bundle build outputs with provenance; validate checksums and safe extraction."""
import argparse
import hashlib
import json
import os
import re
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
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    pr = event.get('pull_request', {})
    source_sha = git_revision('HEAD')
    head_sha = pr.get('head', {}).get('sha', source_sha)
    base_sha = pr.get('base', {}).get('sha')
    pr_number = pr.get('number')
    if os.environ['GITHUB_EVENT_NAME'] == 'workflow_dispatch':
        inputs = event.get('inputs') or {}
        provenance_inputs = [inputs.get(name) for name in
                             ('pr_number', 'expected_head_sha', 'expected_base_sha')]
        if any(value not in (None, '') for value in provenance_inputs):
            number, expected_head, expected_base = provenance_inputs
            if (not isinstance(number, str) or not re.fullmatch(r'[1-9][0-9]*', number)
                    or int(number) > 9007199254740991
                    or not isinstance(expected_head, str)
                    or not re.fullmatch(r'[a-f0-9]{40}', expected_head)
                    or not isinstance(expected_base, str)
                    or not re.fullmatch(r'[a-f0-9]{40}', expected_base)):
                raise ValueError('Invalid PR dispatch provenance inputs')
            if source_sha != expected_head:
                raise ValueError('PR dispatch checkout does not match expected head SHA')
            head_sha, base_sha, pr_number = expected_head, expected_base, int(number)
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination / 'payload.tar.gz'
    if not source.is_dir() or not any(source.iterdir()):
        raise ValueError('Build output must be a non-empty directory')
    with tarfile.open(archive, 'w:gz') as bundle:
        for entry in sorted(source.iterdir()):
            bundle.add(entry, arcname=entry.name)
    manifest = {
        'schema': 1,
        'repository': os.environ['GITHUB_REPOSITORY'],
        'run_id': int(os.environ['GITHUB_RUN_ID']),
        'run_attempt': int(os.environ['GITHUB_RUN_ATTEMPT']),
        'event': os.environ['GITHUB_EVENT_NAME'],
        'source_sha': source_sha,
        'source_tree': git_revision('HEAD^{tree}'),
        'head_sha': head_sha,
        'base_sha': base_sha,
        'pr_number': pr_number,
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
