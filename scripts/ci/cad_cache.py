"""Use CAD tool and OS ABI identity instead of hosted runner image revisions."""
import argparse
import copy
from contextlib import redirect_stdout
import json
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
FINAL = ROOT / 'kufr/mechanika/final'


def normalize_identity(identity):
    result = copy.deepcopy(identity)
    environment = result['environment']
    image = environment['ci_image']
    if environment['system'] != 'Linux' or image['ImageOS'] != 'ubuntu24':
        raise ValueError('CAD CI cache requires the hosted Ubuntu 24 runtime')
    # Kernel and image rollout dates vary between runners with identical CAD tools.
    # Keep the distribution, architecture and libc ABI, plus every tool identity.
    libc = re.fullmatch(r'Linux-.+-with-glibc([0-9]+(?:\.[0-9]+)+)', environment['platform'])
    if not libc:
        raise ValueError('Unknown hosted Linux libc identity')
    environment['platform'] = f"Linux-{environment['machine']}-with-glibc{libc[1]}"
    image.pop('ImageVersion', None)
    return result


def promote_legacy_cache(root, out, current, cache):
    """Rewrite only the receipt, after complete source/output/tool validation."""
    stamp_path = out / cache.CACHE_NAME
    if not out.resolve().is_relative_to(root.resolve()) or stamp_path.is_symlink():
        raise ValueError('CAD cache path escapes its source root')
    if not stamp_path.is_file() or stamp_path.stat().st_size > 1024 * 1024:
        return False
    try:
        stamp = json.loads(stamp_path.read_text(encoding='utf-8'))
        saved = stamp['identity']
        if normalize_identity(saved) != current:
            print('CAD cache differs in sources, tools or OS ABI; regeneration required.')
            return False
    except (KeyError, TypeError, ValueError):
        return False
    # The original validator checks the saved key, source bytes, every export,
    # archive member and the actual installed slicer, including its runtime hash.
    if not cache.check_cache(root, out, saved):
        return False
    if saved != current:
        cache.write_cache(root, out, current)
        print('CAD cache adopted: only hosted image/kernel metadata differed.')
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache-key', action='store_true')
    parser.add_argument('--force', action='store_true')
    args = parser.parse_args()
    sys.path.insert(0, str(FINAL))
    import regenerate
    import regeneration_cache

    runtime_environment = regeneration_cache.runtime_environment

    def stable_environment():
        original = runtime_environment()
        print('CAD host diagnostics: ' + json.dumps({
            'platform': original['platform'], 'ci_image': original['ci_image'],
        }, sort_keys=True), file=sys.stderr)
        return normalize_identity({'environment': original})['environment']

    # This policy is CI-only. Local Regenerate.ps1 retains its full host identity.
    regeneration_cache.runtime_environment = stable_environment
    # Source preparation prints progress; the cache-key command must emit only a hash.
    with redirect_stdout(sys.stderr):
        identity = regenerate.prepared_identity()
    if args.cache_key:
        print(regeneration_cache.cache_key(identity))
        return
    if not args.force:
        promote_legacy_cache(FINAL, FINAL / 'exports', identity, regeneration_cache)
    regenerate.regenerate(force=args.force)


if __name__ == '__main__':
    main()
