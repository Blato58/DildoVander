"""Independently verify immutable GitHub firmware artifacts before root signing.

No runner-supplied digest, version, build identity or URL is trusted. HTTP and
archive contents stay in memory; the ephemeral GitHub token is never persisted.
"""
import base64
from dataclasses import dataclass
import gzip
import hashlib
import io
import json
import re
import ssl
import stat
import struct
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from firmware_version import resolve_version

REPOSITORY = 'Blato58/DildoVander'
API_ORIGIN = 'https://api.github.com'
API_ROOT = '/repos/' + REPOSITORY
WORKFLOW = 'ci.yml'
ARTIFACT = 'firmware'
VERSION_PATH = 'firmware/version.txt'
MAX_ZIP = 32 * 1024 * 1024
MAX_PAYLOAD = 32 * 1024 * 1024
MAX_TAR_DATA = 64 * 1024 * 1024
MAX_TAR_STREAM = MAX_TAR_DATA + 2 * 1024 * 1024
MAX_MEMBERS = 128
MAX_IMAGE = 0x640000
SHA = re.compile(r'[a-f0-9]{40}\Z')
DIGEST = re.compile(r'[a-f0-9]{64}\Z')


class ArtifactVerificationError(ValueError):
    """Sanitized error safe for caller diagnostics; never contains URLs/secrets."""


@dataclass(frozen=True)
class VerifiedArtifact:
    version: str
    build_id: str
    sha256: str
    size_bytes: int
    main_sha: str


def _fail(code):
    raise ArtifactVerificationError(code) from None


def _positive(value):
    return type(value) is int and 1 <= value <= 9007199254740991


def _sha(value):
    return isinstance(value, str) and SHA.fullmatch(value) is not None


def _unique_object(items):
    result = {}
    for key, value in items:
        if key in result:
            _fail('duplicate-json-field')
        result[key] = value
    return result


def _json(data):
    return json.loads(data.decode('utf-8'), object_pairs_hook=_unique_object)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        return None


class _GitHubClient:
    def __init__(self, token):
        if (not isinstance(token, str) or not 1 <= len(token) <= 4096
                or any(ord(character) < 0x21 or ord(character) > 0x7e for character in token)):
            _fail('invalid-github-token')
        self._token = token
        # A runner-controlled proxy must not receive this privileged request.
        self._opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}), _NoRedirect(),
            urllib.request.HTTPSHandler(context=ssl.create_default_context()))

    def _request(self, url, maximum, authenticated):
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != 'https' or not parsed.hostname or parsed.username is not None
                or parsed.password is not None or parsed.fragment or parsed.port not in (None, 443)
                or any(ord(character) < 0x21 or ord(character) > 0x7e for character in url)):
            _fail('invalid-download-location')
        if authenticated and parsed.netloc != 'api.github.com':
            _fail('invalid-authenticated-origin')
        headers = {'User-Agent': 'kufr-firmware-root-verifier',
                   'Accept-Encoding': 'identity',
                   'Accept': 'application/vnd.github+json' if authenticated else 'application/octet-stream'}
        if authenticated:
            headers['Authorization'] = 'Bearer ' + self._token
            headers['X-GitHub-Api-Version'] = '2022-11-28'
        request = urllib.request.Request(url, headers=headers, method='GET')
        deadline = time.monotonic() + 15
        try:
            response = self._opener.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            # Redirects are observed, never followed with Authorization.
            response = error
        with response:
            status = response.status
            location = response.headers.get('Location')
            if status != 200:
                return status, location, b''
            if response.headers.get('Content-Encoding', 'identity').lower() != 'identity':
                _fail('http-content-encoding-unsupported')
            length = response.headers.get('Content-Length')
            if length is not None and (not re.fullmatch(r'[0-9]+', length) or int(length) > maximum):
                _fail('http-size-limit')
            data = bytearray()
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    _fail('http-time-limit')
                # urllib retains the underlying socket even for Connection:
                # close responses. Bound a slow read to the remaining deadline.
                socket = getattr(getattr(getattr(response, 'fp', None), 'raw', None), '_sock', None)
                if socket is not None:
                    socket.settimeout(remaining)
                chunk = response.read1(min(65536, maximum - len(data) + 1))
                if not chunk:
                    break
                data.extend(chunk)
                if len(data) > maximum:
                    _fail('http-size-limit')
            if length is not None and len(data) != int(length):
                _fail('http-response-incomplete')
            return status, location, bytes(data)

    def api(self, path):
        if not path.startswith(API_ROOT + '/') or '\\' in path or '#' in path:
            _fail('invalid-api-path')
        status, _, data = self._request(API_ORIGIN + path, 2 * 1024 * 1024, True)
        if status != 200:
            _fail('github-api-failed')
        result = _json(data)
        if not isinstance(result, dict):
            _fail('github-response-invalid')
        return result

    def artifact_zip(self, artifact_id):
        status, location, data = self._request(
            API_ORIGIN + API_ROOT + '/actions/artifacts/' + str(artifact_id) + '/zip', MAX_ZIP, True)
        if status == 302 and isinstance(location, str):
            # No Authorization, cookie jar, or automatic redirects are shared
            # with GitHub's signed storage URL. A second redirect is rejected.
            status, _, data = self._request(location, MAX_ZIP, False)
        if status != 200:
            _fail('artifact-download-failed')
        return data


def _commit(client, sha):
    if not _sha(sha):
        _fail('commit-sha-invalid')
    commit = client.api(API_ROOT + '/git/commits/' + sha)
    parents = commit.get('parents')
    if (commit.get('sha') != sha or not _sha(commit.get('tree', {}).get('sha'))
            or not isinstance(parents, list) or len(parents) > 16
            or any(not isinstance(parent, dict) or not _sha(parent.get('sha')) for parent in parents)):
        _fail('commit-response-invalid')
    return commit


def _main(client):
    reference = client.api(API_ROOT + '/git/ref/heads/main')
    object_ = reference.get('object', {})
    if object_.get('type') != 'commit' or not _sha(object_.get('sha')):
        _fail('main-reference-invalid')
    return _commit(client, object_['sha'])


def _artifact_metadata(client, run_id):
    artifacts = []
    total = None
    for page in range(1, 11):
        result = client.api(API_ROOT + '/actions/runs/' + str(run_id)
                            + '/artifacts?per_page=100&page=' + str(page))
        count = result.get('total_count')
        entries = result.get('artifacts')
        if (type(count) is not int or not 0 <= count <= 1000
                or not isinstance(entries, list) or len(entries) > 100
                or (total is not None and count != total)):
            _fail('artifact-list-invalid')
        total = count
        artifacts.extend(entries)
        if len(entries) < 100:
            break
    if len(artifacts) != total or any(not isinstance(item, dict) for item in artifacts):
        _fail('artifact-list-incomplete')
    ids = [item.get('id') for item in artifacts]
    if any(not _positive(id_) for id_ in ids) or len(set(ids)) != len(ids):
        _fail('artifact-list-invalid')
    matches = [item for item in artifacts if item.get('name') == ARTIFACT and item.get('expired') is False]
    if len(matches) != 1:
        _fail('artifact-missing-expired-or-ambiguous')
    match = matches[0]
    digest = match.get('digest')
    if (not isinstance(digest, str) or not re.fullmatch(r'sha256:[a-f0-9]{64}', digest)
            or type(match.get('size_in_bytes')) is not int or not 1 <= match['size_in_bytes'] <= MAX_ZIP):
        _fail('artifact-digest-or-size-invalid')
    if 'workflow_run' in match and match['workflow_run'].get('id') != run_id:
        _fail('artifact-run-mismatch')
    return match


def _zip_contents(data):
    if not 1 <= len(data) <= MAX_ZIP:
        _fail('zip-size-limit')
    # Check the central-directory count before ZipFile creates an unbounded
    # object for every member. Two small files never require ZIP64 sizes.
    end = data.rfind(b'PK\x05\x06', max(0, len(data) - 65557))
    if end < 0 or end + 22 > len(data):
        _fail('zip-directory-invalid')
    (_, disk, directory_disk, disk_entries, entries, directory_size,
     directory_offset, comment_size) = struct.unpack_from('<4sHHHHIIH', data, end)
    if (disk != 0 or directory_disk != 0 or disk_entries != 2 or entries != 2
            or end + 22 + comment_size != len(data)
            or directory_offset + directory_size > end):
        _fail('zip-directory-invalid')
    contents = {}
    limits = {'build-manifest.json': 8192, 'payload.tar.gz': MAX_PAYLOAD}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = archive.infolist()
        if len(entries) != 2:
            _fail('zip-members-invalid')
        for entry in entries:
            mode = entry.external_attr >> 16
            if (entry.filename not in limits or entry.filename in contents or entry.is_dir()
                    or (stat.S_IFMT(mode) not in (0, stat.S_IFREG))
                    or entry.flag_bits & 1 or not 0 < entry.file_size <= limits.get(entry.filename, 0)):
                _fail('zip-member-unsafe')
            with archive.open(entry) as stream:
                value = stream.read(limits[entry.filename] + 1)
                if len(value) != entry.file_size or len(value) > limits[entry.filename]:
                    _fail('zip-member-size-invalid')
                contents[entry.filename] = value
    return contents


class _BoundedTarStream:
    def __init__(self, stream):
        self._stream = stream
        self._read = 0

    def read(self, size=-1):
        remaining = MAX_TAR_STREAM - self._read
        if size < 0 or size > remaining + 1:
            size = remaining + 1
        data = self._stream.read(size)
        self._read += len(data)
        if self._read > MAX_TAR_STREAM:
            _fail('tar-stream-size-limit')
        return data


def _tar_image(payload):
    names = set()
    count = 0
    total = 0
    digest = None
    size = None
    image_header = b''
    with gzip.GzipFile(fileobj=io.BytesIO(payload)) as compressed:
        bounded = _BoundedTarStream(compressed)
        with tarfile.open(fileobj=bounded, mode='r|', ignore_zeros=True) as archive:
            for member in archive:
                count += 1
                name = member.name[:-1] if member.isdir() and member.name.endswith('/') else member.name
                if (count > MAX_MEMBERS or not name or len(name) > 256 or name.startswith('/') or '\\' in name or ':' in name
                        or any(ord(character) < 0x20 or ord(character) > 0x7e for character in name)
                        or any(part in ('', '.', '..') for part in name.split('/'))
                        or name in names or not (member.isfile() or member.isdir())
                        or member.sparse is not None or member.size < 0
                        or (member.isdir() and member.size != 0)):
                    _fail('tar-member-unsafe')
                names.add(name)
                total += member.size
                if total > MAX_TAR_DATA:
                    _fail('tar-data-size-limit')
                target = name == 'esp32-s3-idf/firmware.bin'
                if target and (not member.isfile() or not 288 <= member.size <= MAX_IMAGE):
                    _fail('firmware-member-invalid')
                if not member.isfile():
                    continue
                checksum = hashlib.sha256() if target else None
                header = bytearray()
                with archive.extractfile(member) as stream:
                    remaining = member.size
                    while remaining:
                        chunk = stream.read(min(65536, remaining))
                        if not chunk:
                            _fail('tar-member-incomplete')
                        remaining -= len(chunk)
                        if target:
                            checksum.update(chunk)
                            if len(header) < 288:
                                header.extend(chunk[:288 - len(header)])
                if target:
                    digest, size, image_header = checksum.hexdigest(), member.size, bytes(header)
        # Validate gzip trailers and bound any decompressed tail as well.
        while bounded.read(65536):
            pass
    if digest is None:
        _fail('firmware-member-missing')
    return digest, size, image_header


def _version(client, main_sha, run_number=None):
    result = client.api(API_ROOT + '/contents/' + VERSION_PATH + '?ref=' + main_sha)
    if (result.get('type') != 'file' or result.get('path') != VERSION_PATH
            or result.get('encoding') != 'base64' or not _sha(result.get('sha'))
            or type(result.get('size')) is not int or not 1 <= result['size'] <= 128
            or not isinstance(result.get('content'), str) or len(result['content']) > 256):
        _fail('main-version-response-invalid')
    data = base64.b64decode(result['content'].replace('\n', ''), validate=True)
    if len(data) != result['size']:
        _fail('main-version-size-invalid')
    version = data.decode('ascii').rstrip('\r\n')
    try:
        # Only immutable main opts into CI numbering. The number comes from
        # GitHub's run record, never the uploaded manifest or embedded image.
        return resolve_version(version, run_number if version.endswith('+ci') else None)
    except ValueError:
        _fail('main-version-invalid')


def verify_artifact(run_id: str, token: str) -> VerifiedArtifact:
    try:
        if (not isinstance(run_id, str) or not re.fullmatch(r'[1-9][0-9]{0,15}', run_id)
                or not _positive(int(run_id))):
            _fail('run-id-invalid')
        number = int(run_id)
        client = _GitHubClient(token)
        run = client.api(API_ROOT + '/actions/runs/' + run_id)
        workflow = client.api(API_ROOT + '/actions/workflows/' + WORKFLOW)
        if (run.get('id') != number or type(run.get('id')) is not int
                or run.get('repository', {}).get('full_name') != REPOSITORY
                or not _positive(workflow.get('id')) or run.get('workflow_id') != workflow['id']
                or workflow.get('path') != '.github/workflows/' + WORKFLOW
                or run.get('status') != 'completed' or run.get('conclusion') != 'success'
                or run.get('event') not in {'pull_request', 'workflow_dispatch'}
                or not _positive(run.get('run_attempt')) or not _sha(run.get('head_sha'))):
            _fail('workflow-run-not-approved')
        artifact = _artifact_metadata(client, number)
        zipped = client.artifact_zip(artifact['id'])
        if hashlib.sha256(zipped).hexdigest() != artifact['digest'][7:]:
            _fail('github-artifact-digest-mismatch')
        contents = _zip_contents(zipped)
        manifest = _json(contents['build-manifest.json'])
        required = {'schema', 'repository', 'run_id', 'run_attempt', 'event', 'source_sha',
                    'source_tree', 'head_sha', 'base_sha', 'pr_number', 'payload_sha256'}
        if (not isinstance(manifest, dict) or set(manifest) != required
                or type(manifest.get('schema')) is not int or manifest['schema'] != 1
                or manifest.get('repository') != REPOSITORY or type(manifest.get('run_id')) is not int
                or manifest['run_id'] != number or not _positive(manifest.get('run_attempt'))
                or manifest['run_attempt'] > run['run_attempt'] or manifest.get('event') != run['event']
                or manifest.get('head_sha') != run['head_sha']
                or not _sha(manifest.get('source_sha')) or not _sha(manifest.get('source_tree'))
                or not isinstance(manifest.get('payload_sha256'), str)
                or not DIGEST.fullmatch(manifest['payload_sha256'])):
            _fail('build-provenance-invalid')
        if hashlib.sha256(contents['payload.tar.gz']).hexdigest() != manifest['payload_sha256']:
            _fail('build-payload-digest-mismatch')
        dispatched_pr = run['event'] == 'workflow_dispatch' and manifest['pr_number'] is not None
        if run['event'] == 'pull_request' or dispatched_pr:
            if not _positive(manifest['pr_number']) or not _sha(manifest['base_sha']):
                _fail('build-pr-provenance-invalid')
            if dispatched_pr and (manifest['source_sha'] != run['head_sha']
                                  or not isinstance(run.get('head_branch'), str) or not run['head_branch']):
                _fail('build-pr-dispatch-invalid')
            pr = client.api(API_ROOT + '/pulls/' + str(manifest['pr_number']))
            if (pr.get('merged') is not True or pr.get('base', {}).get('ref') != 'main'
                    or pr.get('base', {}).get('repo', {}).get('full_name') != REPOSITORY
                    or pr.get('head', {}).get('repo', {}).get('full_name') != REPOSITORY
                    or pr.get('head', {}).get('sha') != run['head_sha']
                    or (dispatched_pr and pr['head'].get('ref') != run['head_branch'])):
                _fail('build-pr-not-approved')
        elif (run['event'] != 'workflow_dispatch' or run.get('head_branch') != 'main'
              or manifest['source_sha'] != run['head_sha'] or manifest['base_sha'] is not None):
            _fail('build-main-dispatch-invalid')
        source = _commit(client, manifest['source_sha'])
        if source['tree']['sha'] != manifest['source_tree']:
            _fail('build-source-tree-mismatch')
        if (run['event'] == 'pull_request' and manifest['source_sha'] != run['head_sha']
                and [parent['sha'] for parent in source['parents']] != [manifest['base_sha'], run['head_sha']]):
            _fail('build-synthetic-parents-mismatch')
        main = _main(client)
        head = _commit(client, run['head_sha'])
        if main['tree']['sha'] != manifest['source_tree'] or head['tree']['sha'] != main['tree']['sha']:
            _fail('build-tree-differs-from-current-main')
        version = _version(client, main['sha'], run.get('run_number'))
        digest, size, header = _tar_image(contents['payload.tar.gz'])
        if (header[0] != 0xe9 or not 1 <= header[1] <= 16 or header[3] >> 4 != 4
                or struct.unpack_from('<H', header, 12)[0] != 9
                or struct.unpack_from('<I', header, 32)[0] != 0xabcd5432
                or header[48:80].split(b'\0', 1)[0].decode('ascii') != version):
            _fail('firmware-image-contract-invalid')
        return VerifiedArtifact(version, manifest['source_sha'], digest, size, main['sha'])
    except ArtifactVerificationError:
        raise
    except Exception:
        _fail('artifact-verification-failed')


def assert_main_unchanged(main_sha: str, token: str) -> None:
    try:
        if not _sha(main_sha):
            _fail('main-sha-invalid')
        if _main(_GitHubClient(token))['sha'] != main_sha:
            _fail('main-changed-before-publication')
    except ArtifactVerificationError:
        raise
    except Exception:
        _fail('main-recheck-failed')
