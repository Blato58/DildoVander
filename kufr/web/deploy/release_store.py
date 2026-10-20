"""Prepare immutable local app/model publication; never SSH or deploy by itself."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import uuid

# Repository-only Node validation uses this path. Portable publishers inject
# their transport validator and may import this file from a shallow /tmp path.
REPO = Path(__file__).resolve().parent.parent.parent.parent

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inventory(root):
    if root.is_symlink() or not root.is_dir():
        raise ValueError('Release must be a real directory')
    files = {}
    for path in root.rglob('*'):
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError('Release symlinks and escaped paths are forbidden')
        if path.is_file():
            files[path.relative_to(root).as_posix()] = digest(path)
        elif not path.is_dir():
            raise ValueError('Special release files are forbidden')
    return files


@contextmanager
def publication_lock(root):
    if root.is_symlink():
        raise ValueError('Publication root must not be a symlink')
    root.mkdir(parents=True, exist_ok=True)
    lock = root / '.publication-lock'
    try:
        lock.mkdir(mode=0o700)
    except FileExistsError as error:
        raise ValueError('Another publication is running; inspect stale locks explicitly') from error
    try:
        yield
    finally:
        lock.rmdir()


def atomic_json(path, value):
    temporary = path.with_name('.' + path.name + '-' + uuid.uuid4().hex)
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, sort_keys=True, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        # These pointers are public static data, even with a private SSH umask.
        temporary.chmod(0o644)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def model_activation_lock(root):
    """Shared with the backend model selector; only the creator removes it."""
    path = root / '.activation-lock'
    try:
        stream = path.open('xb')
    except FileExistsError as error:
        raise ValueError('Another model activation is running; inspect stale locks explicitly') from error
    try:
        yield
    finally:
        stream.close()
        path.unlink()


def store_immutable(source, target):
    if target.resolve().is_relative_to(source.resolve()):
        raise ValueError('Immutable target must be outside release input')
    if target.is_symlink():
        raise ValueError('Immutable release target must not be a symlink')
    if target.exists():
        if inventory(source) != inventory(target):
            raise ValueError('Immutable release identifier already has different content')
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix='.staged-', dir=target.parent))
    try:
        shutil.copytree(source, staging, dirs_exist_ok=True)
        if inventory(staging) != inventory(source):
            raise ValueError('Staged release integrity differs')
        staging.rename(target)
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def prepare_public_model(root, bundle):
    """Validated model data must be readable by the separate static-file user."""
    # Preflight every path before changing any mode, including on rollback.
    if (bundle.parent != root / 'bundles'
            or any(path.is_symlink() for path in (root, *root.parents))):
        raise ValueError('Model permissions require a canonical public store')
    paths = (root, root / 'bundles', bundle, *bundle.rglob('*'))
    for path in paths:
        if (path.is_symlink() or not path.resolve().is_relative_to(root.resolve())
                or not (path.is_file() or path.is_dir())):
            raise ValueError('Model permissions require a canonical public store')
    # Limit mode changes to this public store; never alter source or host parents.
    for path in paths:
        path.chmod(0o755 if path.is_dir() else 0o644)


def checked_directory(root, relative):
    """Never publish through an existing symlink within the durable store."""
    path = root
    for part in relative.parts:
        path = path / part
        if path.is_symlink() or (path.exists() and not path.is_dir()):
            raise ValueError('Publication directory must be real: ' + str(relative))
        path.mkdir(exist_ok=True)
    return path


def validate_model(bundle):
    # Same maintained loader as the application; this checks schemas, paths,
    # compatibility, byte limits and every declared asset hash, without CAD.
    subprocess.run(['node', str(REPO / 'kufr/mobilni-aplikace/scripts/validate-model-bundle.mjs'),
                    '--bundle', str(bundle)], check=True, stdout=subprocess.DEVNULL)
    manifest = json.loads((bundle / 'model-bundle.json').read_text(encoding='utf-8'))
    allowed = {'model-bundle.json', *(asset['path'] for asset in manifest['assets'])}
    if set(inventory(bundle)) != allowed:
        raise ValueError('Model directory contains undeclared files')
    return manifest


def publish_model(bundle, root, *, validator=None, check=None):
    validator = validator or validate_model
    with publication_lock(root):
        before = inventory(bundle)
        manifest = validator(bundle)
        model_hash = manifest['bundleHash']
        if not re.fullmatch('[0-9a-f]{64}', model_hash):
            raise ValueError('Invalid immutable model identifier')
        checked_directory(root, Path('bundles'))
        target = root / 'bundles' / model_hash
        store_immutable(bundle, target)
        validator(target)
        if inventory(target) != before:
            raise ValueError('Validated model changed during publication')
        prepare_public_model(root, target)
        pointer = {'schemaVersion': 1, 'modelId': manifest['modelId'], 'bundleHash': model_hash,
                   'manifestPath': f'bundles/{model_hash}/model-bundle.json'}
        with model_activation_lock(root):
            active = root / 'active.json'
            previous = json.loads(active.read_text()) if active.exists() else None
            if previous == pointer:
                atomic_json(active, pointer)
                if check:
                    check(pointer)
                return pointer
            switch_model(root, pointer, previous, check)
        return pointer


def switch_model(root, pointer, current, check):
    """Caller holds both locks. Save rollback before switching, recover both files."""
    active, prior = root / 'active.json', root / 'previous.json'
    old_prior = json.loads(prior.read_text()) if prior.exists() else None
    # A crash after pointer activation must retain the immediately prior model.
    if current:
        atomic_json(prior, current)
    try:
        atomic_json(active, pointer)
        if check:
            check(pointer)
    except Exception:
        if current is None:
            active.unlink(missing_ok=True)
        else:
            atomic_json(active, current)
        if old_prior is None:
            prior.unlink(missing_ok=True)
        else:
            atomic_json(prior, old_prior)
        raise


def rollback_model(root, *, validator=None, check=None):
    validator = validator or validate_model
    with publication_lock(root):
        with model_activation_lock(root):
            # Backend selectors share this lock, not the CLI publication lock.
            previous = json.loads((root / 'previous.json').read_text())
            model_hash = previous.get('bundleHash', '')
            if (previous.get('schemaVersion') != 1 or not re.fullmatch('[0-9a-f]{64}', model_hash)
                    or previous.get('manifestPath') != f'bundles/{model_hash}/model-bundle.json'):
                raise ValueError('Invalid rollback reference')
            manifest = validator(root / 'bundles' / model_hash)
            if manifest['modelId'] != previous['modelId'] or manifest['bundleHash'] != model_hash:
                raise ValueError('Rollback reference identity differs')
            prepare_public_model(root, root / 'bundles' / model_hash)
            active = root / 'active.json'
            current = json.loads(active.read_text()) if active.exists() else None
            if previous != current:
                switch_model(root, previous, current, check)
            else:
                atomic_json(active, previous)
                if check:
                    check(previous)
        return previous


def validate_application(source):
    manifest = json.loads((source / 'site-manifest.json').read_text(encoding='utf-8'))
    actual = inventory(source)
    actual.pop('site-manifest.json', None)
    if (manifest.get('schemaVersion') != 2 or manifest.get('artifactType') != 'application'
            or not re.fullmatch('[0-9a-f]{40}', manifest.get('applicationSourceRevision', ''))
            or manifest.get('applicationSourceDirty') is not False
            or manifest.get('apiContractVersion') != 1 or manifest.get('sha256') != actual
            or 'index.html' not in actual):
        raise ValueError('Invalid application release provenance or inventory')
    for name in actual:
        path = PurePosixPath(name)
        if any(part.startswith('.') for part in path.parts):
            raise ValueError('Private application file')
        if path.suffix.lower() in {'.pem', '.key', '.pfx', '.p12', '.sqlite', '.sqlite-wal', '.sqlite-shm', '.db'}:
            raise ValueError('Private material in application release')
        if path.suffix.lower() == '.html' and name != 'index.html':
            raise ValueError('Multiple hosted application entries')
    return digest(source / 'site-manifest.json')


def activate_application(root, target):
    current = root / 'current'
    if current.exists() and not current.is_symlink():
        raise ValueError('Current application must be a managed symlink')
    previous = os.readlink(current) if current.is_symlink() else None
    if previous:
        prior = (root / previous).resolve()
        if not prior.is_relative_to((root / 'releases').resolve()):
            raise ValueError('Previous application link escapes releases')
        if prior == target.resolve():
            return
        atomic_json(root / 'previous-application.json', {'target': previous})
    temporary = root / ('.current-' + uuid.uuid4().hex)
    try:
        temporary.symlink_to(target.relative_to(root), target_is_directory=True)
        temporary.replace(current)
    finally:
        temporary.unlink(missing_ok=True)


def publish_application(source, root):
    with publication_lock(root):
        release_id = validate_application(source)
        checked_directory(root, Path('releases'))
        target = root / 'releases' / release_id
        store_immutable(source, target)
        validate_application(target)
        # Retain immutable asset URLs so open tabs survive activation/rollback.
        for path in (target / 'assets').rglob('*'):
            if path.is_dir():
                continue
            relative = path.relative_to(target)
            if not re.search(r'-[A-Za-z0-9_-]{8,}\.[^.]+$', path.name):
                raise ValueError('Application assets must have Vite fingerprinted filenames')
            output = root / relative
            if output.is_symlink():
                raise ValueError('Asset target must not be a symlink')
            checked_directory(root, relative.parent)
            if output.exists():
                if digest(output) != digest(path):
                    raise ValueError('Immutable application asset collision')
            else:
                temporary = output.with_name('.' + output.name + '-' + uuid.uuid4().hex)
                try:
                    shutil.copyfile(path, temporary)
                    temporary.replace(output)
                finally:
                    temporary.unlink(missing_ok=True)
        validate_application(target)
        activate_application(root, target)
        return release_id


def rollback_application(root):
    with publication_lock(root):
        previous = json.loads((root / 'previous-application.json').read_text())['target']
        target = (root / previous).resolve()
        if not target.is_relative_to((root / 'releases').resolve()):
            raise ValueError('Rollback target escapes releases')
        validate_application(target)
        activate_application(root, target)
        return target.name


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('kind', choices=('application', 'model'))
    parser.add_argument('operation', choices=('publish', 'rollback'))
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--source', type=Path)
    args = parser.parse_args()
    if not args.root.is_absolute():
        parser.error('--root must be an explicitly provisioned absolute durable directory')
    if args.operation == 'publish':
        if args.source is None:
            parser.error('--source required for publication')
        action = publish_application if args.kind == 'application' else publish_model
        result = action(args.source, args.root)
    else:
        action = rollback_application if args.kind == 'application' else rollback_model
        result = action(args.root)
    print(json.dumps(result, sort_keys=True))
