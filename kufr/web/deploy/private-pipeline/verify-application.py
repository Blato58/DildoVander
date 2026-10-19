"""Private-pipeline template helper: schema-2 preflight and HTTPS readback.

No providers, CAD, manufacturing downloads or private guest data are requested.
Invocation against production requires the separately authorized deployment.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import urllib.error
import urllib.parse
import urllib.request

SPEC = importlib.util.spec_from_file_location('application_release_store', Path(__file__).resolve().parents[1] / 'release_store.py')
STORE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STORE)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def origin(value):
    parsed = urllib.parse.urlsplit(value)
    require(parsed.scheme == 'https' and parsed.hostname and parsed.path in ('', '/')
            and not parsed.query and not parsed.fragment and not parsed.username and not parsed.password
            and parsed.port in (None, 443) and parsed.hostname == parsed.netloc,
            'An exact canonical HTTPS DNS origin without port, credentials or path is required')
    require(re.fullmatch(r'[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?', parsed.hostname)
            and '.' in parsed.hostname and not re.fullmatch(r'[0-9.]+', parsed.hostname),
            'Canonical application origin must use its configured DNS hostname')
    return value.rstrip('/')


def local_manifest(local, expected_sha):
    require(re.fullmatch(r'[a-f0-9]{40}', expected_sha), 'Expected verified source SHA is required')
    target = STORE.validate_application(local)
    manifest = json.loads((local / 'site-manifest.json').read_text())
    require(manifest['applicationSourceRevision'] == expected_sha, 'Application source differs from verified public artifact')
    return manifest, target


def preflight(local, expected_sha, root):
    _, target = local_manifest(local, expected_sha)
    require(root.is_absolute() and root.is_dir() and not root.is_symlink(), 'Application store must be explicitly provisioned')
    current = root / 'current'
    require(current.is_symlink(), 'First migration needs a separately reviewed paired configuration and rollback procedure')
    prior = current.resolve(strict=True)
    require(prior.parent == (root / 'releases').resolve(), 'Current release must be managed directly beneath releases')
    # The schema-2 rollback tool cannot safely restore a schema-1/configuration
    # pair. Do not make that first transition an accidental ordinary deployment.
    STORE.validate_application(prior)
    prior_manifest = json.loads((prior / 'site-manifest.json').read_text())
    return {'APP_PRIOR_RELEASE': prior.name, 'APP_TARGET_RELEASE': target,
            'APP_PRIOR_SOURCE_SHA': prior_manifest['applicationSourceRevision']}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        return None


def get(base, path, limit=32 * 1024 * 1024):
    request = urllib.request.Request(base + path, headers={'User-Agent': 'DildoVander-Application-Readback/1', 'Accept-Encoding': 'identity'})
    opener = urllib.request.build_opener(NoRedirect())
    try:
        response = opener.open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        body = response.read(limit + 1)
        require(len(body) <= limit, 'Readback response exceeds limit: ' + path)
        return response.status, response.headers, body


def verify(base, local, expected_sha, fetch=get):
    base = origin(base)
    manifest, _ = local_manifest(local, expected_sha)
    status, headers, body = fetch(base, '/site-manifest.json')
    require(status == 200 and json.loads(body) == manifest and 'no-store' in headers.get('Cache-Control', ''), 'Published schema-2 receipt differs')
    entry = (local / 'index.html').read_bytes()
    for path in ('/', '/signin', '/settings/admin', '/suitcase', '/suitcase/diagnostics', '/workshop', '/archive',
                 '/app/index.html', '/companion/index.html', '/simulator/index.html', '/workshop/viewer.html',
                 '/keepsake/', '/keepsake/evening/'):
        status, headers, body = fetch(base, path)
        require(status == 200 and body == entry and 'no-store' in headers.get('Cache-Control', ''), 'Application deep link differs: ' + path)
        require(headers.get('Referrer-Policy') == 'no-referrer', 'Application referrer policy differs: ' + path)
        if path.startswith('/keepsake/'):
            require('noindex' in headers.get('X-Robots-Tag', ''), 'Private entry permits indexing')
    for name, digest in manifest['sha256'].items():
        if name == 'index.html':
            continue
        path = '/' + urllib.parse.quote(name, safe='/')
        status, headers, body = fetch(base, path)
        require(status == 200 and hashlib.sha256(body).hexdigest() == digest, 'Published application bytes differ: ' + path)
        if name.startswith('assets/'):
            require('immutable' in headers.get('Cache-Control', ''), 'Fingerprint asset cache policy differs')
    for path in ('/app/v1/application-deployment-probe', '/guest/v1/application-deployment-probe',
                 '/assets/application-deployment-probe.js', '/application-deployment-probe', '/downloads/application-deployment-probe.html'):
        status, _, body = fetch(base, path)
        require(status == 404 and body != entry, 'Missing API/asset unexpectedly became an application page: ' + path)
    status, headers, body = fetch(base, '/.well-known/apple-app-site-association')
    require(status == 200 and 'json' in headers.get('Content-Type', '')
            and bool(json.loads(body).get('webcredentials', {}).get('apps')), 'Native associated domain is not configured')
    status, _, _ = fetch(base, '/healthz')
    require(status == 200, 'Backend health failed')
    return {'applicationSourceRevision': expected_sha, 'verifiedFiles': len(manifest['sha256'])}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--local', required=True, type=Path)
    parser.add_argument('--expected-sha', required=True)
    parser.add_argument('--base')
    parser.add_argument('--preflight-root', type=Path)
    parser.add_argument('--github-env', type=Path)
    args = parser.parse_args()
    if args.preflight_root:
        if args.base:
            origin(args.base)
        result = preflight(args.local, args.expected_sha, args.preflight_root)
        if args.github_env:
            with args.github_env.open('a', encoding='utf-8') as output:
                for key, value in result.items():
                    require(re.fullmatch(r'[a-zA-Z0-9_-]+', value), 'Unsafe managed release identifier')
                    output.write(key + '=' + value + '\n')
    else:
        if not args.base or args.github_env:
            parser.error('--base is required for readback; --github-env is preflight only')
        result = verify(args.base, args.local, args.expected_sha)
    print(json.dumps(result, sort_keys=True))
