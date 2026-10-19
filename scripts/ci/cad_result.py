"""Transport verified main CAD results without installing the CAD toolchain.

Only call restore on an immutable artifact resolved by cad-result.cjs. Hashes
detect corruption; GitHub run provenance, not a self-authored receipt, is the
trust boundary. Local regeneration retains its stricter actual-runtime gate.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys

ROOT = Path(__file__).resolve().parents[2]
FINAL = ROOT / 'kufr/mechanika/final'
RECEIPT = 'trusted-result.json'
SCHEMA = 1
TOOLS = (
    'scripts/ci/cad_result.py', 'scripts/ci/cad-result.cjs',
    'scripts/ci/cad_cache.py', 'scripts/ci/install-cad-slicer.sh',
    '.github/actions/verified-cad/action.yml',
    '.github/workflows/kufr-cad.yml',
    'kufr/mobilni-aplikace/.nvmrc',
    'kufr/mechanika/rozmery-doruceneho-kufru.md',
    'kufr/mechanika/rozmery-hulek.md',
    'kufr/mechanika/rozmery-nakoupenych-soucastek.md',
    'kufr/docs/zapojeni-potvrzeneho-hardwaru.md',
)


def digest(path):
    hasher = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            hasher.update(block)
    return hasher.hexdigest()


def key(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def inputs():
    sys.path.insert(0, str(FINAL))
    from source_inputs import prepared_sources, presentation_only
    sources = prepared_sources()
    # The runtime contract is independent of commits, kernel/image rollout and
    # runner-temp paths. Changes to requirements or the pinned slicer invalidate
    # it. Installed runtime evidence remains in the original acceptance receipt.
    return {
        'schema': SCHEMA,
        'runtime_contract': 'ubuntu-24.04-x86_64-python-3.11',
        'sources': {name: value for name, value in sources.items() if not presentation_only(name)},
        'tools': {name: digest(ROOT / name) for name in TOOLS},
    }, sources


def safe_file(out, name):
    if not isinstance(name, str) or not name or '\\' in name or ':' in name:
        raise ValueError('Invalid CAD inventory path')
    if PurePosixPath(name).is_absolute() or any(part in ('', '.', '..') for part in name.split('/')):
        raise ValueError('Escaping CAD inventory path')
    path = out / name
    if path.is_symlink() or not path.resolve().is_relative_to(out.resolve()):
        raise ValueError('Escaping CAD output')
    if not path.is_file():
        raise ValueError('Missing CAD output: ' + name)
    return path


def inventory(out):
    result = {}
    for path in sorted(out.rglob('*')):
        if path.is_symlink():
            raise ValueError('Symlink in CAD outputs')
        if path.is_file():
            name = path.relative_to(out).as_posix()
            if name != RECEIPT:
                result[name] = digest(safe_file(out, name))
    return result


def read_receipt(out):
    receipt_path = safe_file(out, RECEIPT)
    if receipt_path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError('Oversized CAD receipt')
    receipt = json.loads(receipt_path.read_text(encoding='utf-8'))
    if receipt.get('schema') != SCHEMA or receipt.get('key') != key(receipt.get('inputs')):
        raise ValueError('Invalid CAD result identity')
    if not isinstance(receipt.get('runtime'), dict) or not receipt['runtime']:
        raise ValueError('Missing accepted CAD runtime')
    hashes = receipt.get('sha256')
    if not isinstance(hashes, dict) or not hashes:
        raise ValueError('Empty CAD inventory')
    for name, expected in hashes.items():
        if not isinstance(expected, str) or not re.fullmatch('[a-f0-9]{64}', expected):
            raise ValueError('Invalid CAD hash')
        if digest(safe_file(out, name)) != expected:
            raise ValueError('Corrupt CAD output: ' + name)
    if inventory(out) != hashes:
        raise ValueError('Incomplete or extra CAD outputs')
    report = json.loads(safe_file(out, 'verification.json').read_bytes())
    if report.get('result') != 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE' or report.get('sources') != receipt.get('sources'):
        raise ValueError('CAD acceptance differs from receipt')
    slicing = json.loads(safe_file(out, 'slicer-validation.json').read_bytes())
    if slicing.get('status') != 'PASS_SLICER':
        raise ValueError('Missing actual slicer acceptance')
    return receipt


def publish(out, identity, sources, runtime):
    # Only freshly accepted local regeneration or a verified presentation
    # transfer may publish. Callers enforce that before reaching this function.
    receipt = {'schema': SCHEMA, 'key': key(identity), 'inputs': identity,
               'sources': sources, 'runtime': runtime, 'sha256': inventory(out)}
    temporary = out / '.trusted-result.tmp'
    temporary.write_text(json.dumps(receipt, sort_keys=True) + '\n', encoding='utf-8')
    temporary.replace(out / RECEIPT)
    read_receipt(out)


def restore(out, identity, sources, validated=None):
    saved = read_receipt(out)
    if validated:
        validated()
    if saved['inputs'] != identity:
        print('CAD result: different manufacturing inputs; reuse eligible part exports only.')
        return False
    if saved['sources'] != sources:
        from refresh_package import refresh
        refresh(saved['sources'], manufacturing_key=key(identity))
        publish(out, identity, sources, saved['runtime'])
        print('CAD result: trusted mechanics reused; presentation/package refreshed without CAD.')
    else:
        print('CAD result: trusted exact input and output match; generation and verification skipped.')
    return True


def reuse_generation(out, runtime):
    """Reuse generated bytes for validator-only changes; never reuse acceptance."""
    from source_inputs import validation_only
    identity, sources = inputs()
    try:
        saved = read_receipt(out)
        # Fresh verification writes runtime-bound acceptance. Old slicing
        # evidence cannot be transferred to a newly installed different tool.
        if saved['runtime'] != runtime:
            print('CAD generated-stage runtime differs; regenerate with the installed tools.')
            return False
        previous = saved['inputs']
        current_geometry = {name: value for name, value in identity['sources'].items()
                            if not validation_only(name)}
        previous_geometry = {name: value for name, value in previous['sources'].items()
                             if not validation_only(name)}
        if ({**previous, 'sources': previous_geometry} != {**identity, 'sources': current_geometry}
                or saved['sources'] == sources):
            return False
        from refresh_package import refresh_generation
        return refresh_generation(saved['sources'])
    except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
        print(f'CAD generated-stage reuse unavailable: {error}')
        return False


def output(name, value):
    print(f'{name}={value}')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as stream:
            stream.write(f'{name}={value}\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('key', 'restore', 'publish', 'discard'))
    args = parser.parse_args()
    out = FINAL / 'exports'
    if args.command == 'discard':
        # A failed/partial download must not supply individual export receipts.
        # This command is CI staging cleanup, never a repository-wide delete.
        if out.is_symlink():
            out.unlink()
        elif out.exists():
            if out.resolve().parent != FINAL.resolve():
                raise ValueError('Refusing to discard exports outside CAD staging')
            shutil.rmtree(out)
        return
    identity, sources = inputs()
    if args.command == 'key':
        output('key', key(identity))
    elif args.command == 'restore':
        try:
            hit = restore(out, identity, sources, validated=lambda: output('artifact-valid', 'true'))
        except (ValueError, OSError, KeyError, TypeError, json.JSONDecodeError) as error:
            print(f'CAD result rejected: {error}; regeneration required.')
            hit = False
        output('hit', str(hit).lower())
    else:
        # The existing fail-closed checker, with actual installed tools, is the
        # authority for a newly generated result, never filenames or this key.
        sys.path.insert(0, str(FINAL))
        import regenerate
        import regeneration_cache
        from cad_cache import normalize_identity
        actual = regenerate.prepared_identity()
        actual = normalize_identity(actual)
        if not regeneration_cache.check_cache(FINAL, out, actual):
            raise ValueError('Cannot publish CAD result without complete runtime acceptance')
        publish(out, identity, sources, actual['environment'])


if __name__ == '__main__':
    main()
