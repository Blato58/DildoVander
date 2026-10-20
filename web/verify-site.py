"""Read back published pages and CAD download over certificate-verified HTTPS."""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen


def verify(base, local):
    expected = json.loads((local / 'site-manifest.json').read_bytes())
    with urlopen(base + '/site-manifest.json', timeout=30) as response:
        published = json.load(response)
    if published != expected:
        raise ValueError('Public release manifest differs from built release')
    names = sorted(expected['sha256'])
    for name in names:
        url = base + ('/' if name == 'index.html' else '/' + name)
        digest = hashlib.sha256()
        with urlopen(url, timeout=180) as response:
            for chunk in iter(lambda: response.read(1048576), b''):
                digest.update(chunk)
        if digest.hexdigest() != expected['sha256'][name]:
            raise ValueError('Public checksum differs: ' + name)
    for name in ('ios/', 'healthz'):
        with urlopen(base + '/' + name, timeout=30) as response:
            if response.status != 200:
                raise ValueError('Existing route is unavailable: ' + name)
    print(f'Public site: all {len(names)} files, complete CAD ZIP, iOS route and backend health verified')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--local', type=Path, required=True)
    args = parser.parse_args()
    if not args.base.startswith('https://'):
        parser.error('Public verification requires HTTPS')
    verify(args.base.rstrip('/'), args.local)
