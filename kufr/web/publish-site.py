"""Verify the entire public-site inventory before an atomic release switch."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile


def verify(source, expected_sha):
    if source.is_symlink():
        raise ValueError('Site source must not be a symlink')
    manifest = json.loads((source / 'site-manifest.json').read_bytes())
    if manifest.get('schema') != 1 or manifest.get('source_sha') != expected_sha:
        raise ValueError('Wrong release provenance')
    if not re.fullmatch('[a-f0-9]{40}', expected_sha):
        raise ValueError('Expected a full source SHA')
    entries = list(source.rglob('*'))
    if any(path.is_symlink() for path in entries):
        raise ValueError('Site payload must not contain symlinks')
    actual = {path.relative_to(source).as_posix() for path in entries if path.is_file()}
    if actual != {*manifest['sha256'], 'site-manifest.json'}:
        raise ValueError('Site inventory mismatch')
    for name, expected in manifest['sha256'].items():
        relative = PurePosixPath(name)
        if relative.is_absolute() or '..' in relative.parts or '\\' in name:
            raise ValueError('Unsafe site file')
        path = source / name
        if not path.resolve().is_relative_to(source.resolve()):
            raise ValueError('Site file escapes payload')
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError('Site checksum mismatch: ' + name)
    required = {'index.html', 'site.css', 'workshop/index.html', 'workshop/viewer.html',
                'workshop/' + manifest['package'], 'companion/index.html', 'simulator/index.html',
                'design-system/arcane-workshop.css'}
    if not required.issubset(actual):
        raise ValueError('Incomplete site')
    return manifest


def switch(site, target):
    if not re.fullmatch(r'[0-9]+-[0-9]+', target):
        raise ValueError('Unsafe release ID')
    releases = site / 'releases'
    destination = releases / target
    if releases.is_symlink() or destination.is_symlink() or not destination.is_dir():
        raise ValueError('Invalid release directory')
    current = site / 'current'
    if current.exists() and not current.is_symlink():
        raise ValueError('Current must be a symlink')
    if current.is_symlink() and not re.fullmatch(r'releases/[0-9]+-[0-9]+', current.readlink().as_posix()):
        raise ValueError('Current escapes releases')
    previous = current.readlink().name if current.is_symlink() else ''
    temporary = site / ('.current-' + target)
    temporary.symlink_to(Path('releases') / target, target_is_directory=True)
    try:
        os.replace(temporary, current)
    finally:
        temporary.unlink(missing_ok=True)
    return previous


def publish(source, site, release_id, expected_sha):
    verify(source, expected_sha)
    if site.is_symlink() or not site.is_dir() or not re.fullmatch(r'[0-9]+-[0-9]+', release_id):
        raise ValueError('Invalid site destination or release ID')
    releases = site / 'releases'
    if releases.is_symlink():
        raise ValueError('Release root must not be a symlink')
    releases.mkdir(exist_ok=True)
    destination = releases / release_id
    if destination.exists() or destination.is_symlink():
        raise ValueError('Release already exists')
    with tempfile.TemporaryDirectory(prefix='.staging-', dir=site) as staging:
        staged = Path(staging) / 'payload'
        shutil.copytree(source, staged)
        verify(staged, expected_sha)
        os.replace(staged, destination)
    previous = switch(site, release_id)
    return previous


def restore_activation(site, failed_release, previous_release):
    if site.is_symlink() or not site.is_dir() or not re.fullmatch(r'[0-9]+-[0-9]+', failed_release):
        raise ValueError('Invalid rollback destination or release ID')
    current = site / 'current'
    if not current.is_symlink() or current.readlink() != Path('releases') / failed_release:
        raise ValueError('Current changed after the failed activation')
    if previous_release:
        switch(site, previous_release)
    else:
        current.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--site', type=Path, required=True)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--release-id')
    parser.add_argument('--expected-sha')
    parser.add_argument('--rollback')
    parser.add_argument('--rollback-activation', action='store_true')
    parser.add_argument('--previous-release', default='')
    args = parser.parse_args()
    if args.rollback_activation:
        restore_activation(args.site, args.release_id or '', args.previous_release)
    elif args.rollback:
        switch(args.site, args.rollback)
    else:
        if not all((args.source, args.release_id, args.expected_sha)):
            parser.error('source, release-id and expected-sha are required')
        previous = publish(args.source, args.site, args.release_id, args.expected_sha)
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
                output.write('previous_release=' + previous + '\n')
        print('Published ' + args.release_id + '; previous=' + (previous or 'none'))
