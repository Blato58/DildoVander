import base64
from dataclasses import FrozenInstanceError
import hashlib
import io
import json
from pathlib import Path
import stat
import struct
import sys
import tarfile
import unittest
from unittest.mock import patch
import urllib.error
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'firmware' / 'tools'))
import firmware_artifact as verifier

HEAD, SOURCE, TREE, BASE, MAIN = (letter * 40 for letter in ('a', 'b', 'c', 'd', 'f'))
TOKEN = 'ephemeral-secret-never-log'


def image(version='1.2.3'):
    data = bytearray(512)
    data[0], data[1], data[3] = 0xe9, 1, 0x40
    struct.pack_into('<H', data, 12, 9)
    struct.pack_into('<I', data, 32, 0xabcd5432)
    encoded_version = version.encode('ascii')
    assert len(encoded_version) <= 31
    data[48:48 + len(encoded_version)] = encoded_version
    return bytes(data)


def tar_payload(entries=None, version='1.2.3'):
    if entries is None:
        directory = tarfile.TarInfo('esp32-s3-idf')
        directory.type = tarfile.DIRTYPE
        binary = tarfile.TarInfo('esp32-s3-idf/firmware.bin')
        binary.size = len(image(version))
        entries = [(directory, b''), (binary, image(version))]
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode='w:gz') as archive:
        for member, data in entries:
            archive.addfile(member, io.BytesIO(data) if member.isfile() else None)
    return output.getvalue()


def zip_contents(manifest, payload, extra=None, manifest_bytes=None):
    output = io.BytesIO()
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('build-manifest.json', manifest_bytes if manifest_bytes is not None else json.dumps(manifest))
        archive.writestr('payload.tar.gz', payload)
        if extra:
            for name, data in extra:
                archive.writestr(name, data)
    return output.getvalue()


class Fixture:
    def __init__(self, dispatch=False):
        self.requests = []
        self.run = {'id': 123, 'workflow_id': 7, 'repository': {'full_name': verifier.REPOSITORY},
                    'status': 'completed', 'conclusion': 'success', 'event': 'pull_request',
                    'run_attempt': 2, 'head_sha': HEAD, 'head_branch': 'codex/firmware'}
        self.manifest = {'schema': 1, 'repository': verifier.REPOSITORY, 'run_id': 123,
                         'run_attempt': 1, 'event': 'pull_request', 'head_sha': HEAD,
                         'source_sha': SOURCE, 'source_tree': TREE, 'base_sha': BASE,
                         'pr_number': 42, 'payload_sha256': ''}
        self.pr = {'merged': True, 'base': {'ref': 'main', 'repo': {'full_name': verifier.REPOSITORY}},
                   'head': {'sha': HEAD, 'ref': 'codex/firmware', 'repo': {'full_name': verifier.REPOSITORY}}}
        self.commits = {
            SOURCE: {'sha': SOURCE, 'tree': {'sha': TREE}, 'parents': [{'sha': BASE}, {'sha': HEAD}]},
            HEAD: {'sha': HEAD, 'tree': {'sha': TREE}, 'parents': [{'sha': BASE}]},
            MAIN: {'sha': MAIN, 'tree': {'sha': TREE}, 'parents': [{'sha': BASE}]}}
        self.main = MAIN
        self.version = b'1.2.3\n'
        self.workflow = {'id': 7, 'path': '.github/workflows/ci.yml'}
        self.payload = tar_payload()
        self.artifact = {'id': 999, 'name': 'firmware', 'expired': False,
                         'workflow_run': {'id': 123}, 'digest': '', 'size_in_bytes': 0}
        self.artifacts = [self.artifact]
        if dispatch:
            self.run.update(event='workflow_dispatch', head_branch='main', head_sha=MAIN)
            self.manifest.update(event='workflow_dispatch', source_sha=MAIN, head_sha=MAIN,
                                 pr_number=None, base_sha=None)
        self.refresh()

    def refresh(self):
        self.manifest['payload_sha256'] = hashlib.sha256(self.payload).hexdigest()
        self.zipped = zip_contents(self.manifest, self.payload)
        self.approve_zip()

    def approve_zip(self):
        self.artifact['digest'] = 'sha256:' + hashlib.sha256(self.zipped).hexdigest()
        self.artifact['size_in_bytes'] = len(self.zipped)

    def api(self, path):
        self.requests.append(path)
        root = verifier.API_ROOT
        if path == root + '/actions/runs/123':
            return self.run
        if path == root + '/actions/workflows/ci.yml':
            return self.workflow
        if path == root + '/actions/runs/123/artifacts?per_page=100&page=1':
            return {'total_count': len(self.artifacts), 'artifacts': self.artifacts}
        if path == root + '/pulls/42':
            return self.pr
        if path == root + '/git/ref/heads/main':
            return {'object': {'type': 'commit', 'sha': self.main}}
        if path.startswith(root + '/git/commits/'):
            return self.commits[path.rsplit('/', 1)[1]]
        if path == root + '/contents/firmware/version.txt?ref=' + self.main:
            return {'type': 'file', 'path': verifier.VERSION_PATH, 'sha': 'e' * 40,
                    'encoding': 'base64', 'size': len(self.version),
                    'content': base64.b64encode(self.version).decode('ascii') + '\n'}
        raise AssertionError('Unexpected fixed API request')

    def artifact_zip(self, artifact_id):
        if artifact_id != 999:
            raise AssertionError('Unexpected artifact ID')
        return self.zipped


class ArtifactTests(unittest.TestCase):
    def ci_fixture(self, run_number=7, embedded_version='0.1.8', policy=b'0.1.1+ci\n'):
        fixture = Fixture()
        fixture.version = policy
        fixture.run['run_number'] = run_number
        fixture.payload = tar_payload(version=embedded_version)
        fixture.refresh()
        return fixture

    def verify(self, fixture):
        with patch.object(verifier, '_GitHubClient', return_value=fixture):
            return verifier.verify_artifact('123', TOKEN)

    def assert_rejected(self, fixture):
        with self.assertRaises(verifier.ArtifactVerificationError) as failure:
            self.verify(fixture)
        self.assertNotIn(TOKEN, str(failure.exception))
        return str(failure.exception)

    def test_merged_pr_exact_trusted_binary_and_frozen_receipt(self):
        fixture = Fixture()
        result = self.verify(fixture)
        self.assertEqual(result, verifier.VerifiedArtifact('1.2.3', SOURCE,
                         hashlib.sha256(image()).hexdigest(), len(image()), MAIN))
        self.assertIn(verifier.API_ROOT + '/git/commits/' + HEAD, fixture.requests)
        self.assertIn('?ref=' + MAIN, fixture.requests[-1])
        with self.assertRaises(FrozenInstanceError):
            result.version = '9.0.0'

    def test_current_main_dispatch_and_merged_pr_dispatch(self):
        self.assertEqual(self.verify(Fixture(dispatch=True)).build_id, MAIN)
        fixture = Fixture()
        fixture.run['event'] = fixture.manifest['event'] = 'workflow_dispatch'
        fixture.manifest['source_sha'] = HEAD
        fixture.refresh()
        self.assertEqual(self.verify(fixture).build_id, HEAD)

    def test_runner_modified_zip_and_missing_trusted_digest_are_rejected(self):
        fixture = Fixture()
        fixture.zipped += b'local-runner-change'
        self.assertEqual(self.assert_rejected(fixture), 'github-artifact-digest-mismatch')
        for digest in (None, '', 'sha256:' + 'g' * 64, 'sha1:' + 'a' * 40):
            fixture = Fixture(); fixture.artifact['digest'] = digest
            self.assertEqual(self.assert_rejected(fixture), 'artifact-digest-or-size-invalid')

    def test_forged_manifest_and_payload_are_rejected_even_with_trusted_zip_hash(self):
        fixture = Fixture()
        fixture.manifest['source_tree'] = 'e' * 40
        fixture.refresh()
        self.assertEqual(self.assert_rejected(fixture), 'build-source-tree-mismatch')
        fixture = Fixture()
        changed = bytearray(fixture.payload); changed[-1] ^= 1
        fixture.zipped = zip_contents(fixture.manifest, bytes(changed))
        fixture.approve_zip()
        self.assertEqual(self.assert_rejected(fixture), 'build-payload-digest-mismatch')
        fixture = Fixture()
        data = json.dumps(fixture.manifest)
        fixture.zipped = zip_contents(fixture.manifest, fixture.payload,
            manifest_bytes=data[:-1] + ',"run_id":124}')
        fixture.approve_zip()
        self.assertEqual(self.assert_rejected(fixture), 'duplicate-json-field')

    def test_workflow_run_must_be_successful_same_repo_and_expected_workflow(self):
        for field, value in [('id', 124), ('workflow_id', 8), ('status', 'in_progress'),
                             ('conclusion', 'failure'), ('event', 'push'), ('run_attempt', True),
                             ('head_sha', 'invalid')]:
            with self.subTest(field=field):
                fixture = Fixture(); fixture.run[field] = value
                self.assert_rejected(fixture)
        fixture = Fixture(); fixture.run['repository']['full_name'] = 'other/repo'
        self.assert_rejected(fixture)
        fixture = Fixture(); fixture.workflow['path'] = '.github/workflows/untrusted.yml'
        self.assert_rejected(fixture)

    def test_manifest_run_attempt_event_sha_and_repository_must_match(self):
        for field, value in [('schema', True), ('repository', 'other/repo'), ('run_id', 124),
                             ('run_attempt', 3), ('run_attempt', True), ('event', 'push'),
                             ('head_sha', 'e' * 40), ('source_sha', '../secret'),
                             ('source_tree', 'Z' * 40), ('pr_number', True), ('base_sha', None)]:
            with self.subTest(field=field, value=value):
                fixture = Fixture(); fixture.manifest[field] = value; fixture.refresh()
                self.assert_rejected(fixture)

    def test_artifact_missing_expired_ambiguous_wrong_run_and_size(self):
        fixture = Fixture(); fixture.artifacts.clear()
        self.assert_rejected(fixture)
        fixture = Fixture(); fixture.artifact['expired'] = True
        self.assert_rejected(fixture)
        fixture = Fixture(); fixture.artifacts.append(dict(fixture.artifact, id=1000))
        self.assert_rejected(fixture)
        fixture = Fixture(); fixture.artifact['workflow_run']['id'] = 124
        self.assert_rejected(fixture)
        fixture = Fixture(); fixture.artifact['size_in_bytes'] = verifier.MAX_ZIP + 1
        self.assert_rejected(fixture)

    def test_pr_must_be_merged_into_main_from_same_repository(self):
        for mutate in [lambda f: f.pr.update(merged=False),
                       lambda f: f.pr['base'].update(ref='other'),
                       lambda f: f.pr['head']['repo'].update(full_name='fork/repo'),
                       lambda f: f.pr['base']['repo'].update(full_name='other/repo'),
                       lambda f: f.pr['head'].update(sha='e' * 40)]:
            fixture = Fixture(); mutate(fixture)
            self.assert_rejected(fixture)
        fixture = Fixture()
        fixture.run['event'] = fixture.manifest['event'] = 'workflow_dispatch'
        fixture.manifest['source_sha'] = HEAD; fixture.pr['head']['ref'] = 'different'
        fixture.refresh(); self.assert_rejected(fixture)

    def test_source_synthetic_parents_and_current_head_trees_are_independent_checks(self):
        fixture = Fixture(); fixture.commits[SOURCE]['parents'].reverse()
        self.assertEqual(self.assert_rejected(fixture), 'build-synthetic-parents-mismatch')
        fixture = Fixture(); fixture.commits[MAIN]['tree']['sha'] = 'e' * 40
        self.assertEqual(self.assert_rejected(fixture), 'build-tree-differs-from-current-main')
        fixture = Fixture(); fixture.commits[HEAD]['tree']['sha'] = 'e' * 40
        self.assertEqual(self.assert_rejected(fixture), 'build-tree-differs-from-current-main')
        fixture = Fixture(); fixture.commits[SOURCE]['tree']['sha'] = 'invalid'
        self.assert_rejected(fixture)

    def test_version_only_from_immutable_main_and_canonical_embedded_contract(self):
        for version in (b'01.2.3\n', b'65536.0.0\n', b'1.2\n', b'1.2.3-rc\n', b' 1.2.3\n'):
            fixture = Fixture(); fixture.version = version
            self.assertEqual(self.assert_rejected(fixture), 'main-version-invalid')
        fixture = Fixture(); fixture.version = b'2.0.0\n'
        self.assertEqual(self.assert_rejected(fixture), 'firmware-image-contract-invalid')
        fixture = Fixture()
        invalid = bytearray(image()); invalid[12] = 0
        member = tarfile.TarInfo('esp32-s3-idf/firmware.bin'); member.size = len(invalid)
        fixture.payload = tar_payload([(member, bytes(invalid))]); fixture.refresh()
        self.assertEqual(self.assert_rejected(fixture), 'firmware-image-contract-invalid')

    def test_ci_version_derived_from_authoritative_run_matches_embedded_image(self):
        fixture = self.ci_fixture()
        result = self.verify(fixture)
        self.assertEqual(result.version, '0.1.8')
        self.assertEqual(result.sha256, hashlib.sha256(image('0.1.8')).hexdigest())
        self.assertIn('?ref=' + MAIN, fixture.requests[-1])
        fixture = self.ci_fixture(1, '0.2.0', b'0.1.65535+ci\n')
        self.assertEqual(self.verify(fixture).version, '0.2.0')

    def test_ci_run_number_missing_boolean_negative_or_forged_is_rejected(self):
        for run_number in (None, True, False, 0, -1, '7', 7.0, 9007199254740992):
            with self.subTest(run_number=run_number):
                self.assertEqual(self.assert_rejected(self.ci_fixture(run_number)), 'main-version-invalid')
        fixture = self.ci_fixture()
        del fixture.run['run_number']
        self.assertEqual(self.assert_rejected(fixture), 'main-version-invalid')
        # A runner cannot replace GitHub's number through an uploaded field.
        for field in ('run_number', 'version'):
            fixture = self.ci_fixture()
            fixture.manifest[field] = 100 if field == 'run_number' else '0.1.101'
            fixture.refresh()
            self.assertEqual(self.assert_rejected(fixture), 'build-provenance-invalid')

    def test_ci_image_cannot_choose_a_different_number_or_baseline(self):
        for embedded in ('0.1.1', '0.1.7', '0.1.9', '1.0.8', '0.1.8+ci'):
            with self.subTest(embedded=embedded):
                self.assertEqual(self.assert_rejected(self.ci_fixture(embedded_version=embedded)),
                                 'firmware-image-contract-invalid')
        fixture = self.ci_fixture(run_number=8)
        self.assertEqual(self.assert_rejected(fixture), 'firmware-image-contract-invalid')

    def test_legacy_plain_versions_do_not_require_ci_run_number(self):
        for run_number in (None, True, 'forged'):
            fixture = Fixture()
            if run_number is not None:
                fixture.run['run_number'] = run_number
            self.assertEqual(self.verify(fixture).version, '1.2.3')
    def test_zip_exact_names_no_extra_paths_links_or_oversized_json(self):
        for name in ('../extra', '/extra', 'extra', 'dir/extra'):
            fixture = Fixture(); fixture.zipped = zip_contents(fixture.manifest, fixture.payload, [(name, b'x')])
            fixture.approve_zip(); self.assert_rejected(fixture)
        fixture = Fixture()
        output = io.BytesIO()
        with zipfile.ZipFile(output, 'w') as archive:
            link = zipfile.ZipInfo('build-manifest.json'); link.external_attr = (stat.S_IFLNK | 0o777) << 16
            archive.writestr(link, 'outside')
            archive.writestr('payload.tar.gz', fixture.payload)
        fixture.zipped = output.getvalue(); fixture.approve_zip(); self.assert_rejected(fixture)
        fixture = Fixture(); fixture.zipped = zip_contents(fixture.manifest, fixture.payload, manifest_bytes=b' ' * 8193)
        fixture.approve_zip(); self.assert_rejected(fixture)

    def test_zip_duplicates_and_all_archive_size_limits(self):
        fixture = Fixture()
        output = io.BytesIO()
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(output, 'w') as archive:
                archive.writestr('build-manifest.json', json.dumps(fixture.manifest))
                archive.writestr('build-manifest.json', json.dumps(fixture.manifest))
        fixture.zipped = output.getvalue(); fixture.approve_zip()
        self.assertEqual(self.assert_rejected(fixture), 'zip-member-unsafe')
        fixture = Fixture()
        with patch.object(verifier, 'MAX_PAYLOAD', len(fixture.payload) - 1):
            self.assertEqual(self.assert_rejected(fixture), 'zip-member-unsafe')
        with patch.object(verifier, 'MAX_ZIP', len(fixture.zipped) - 1):
            self.assertEqual(self.assert_rejected(fixture), 'artifact-digest-or-size-invalid')
        # A forged directory count is rejected before constructing ZipFile.
        data = bytearray(fixture.zipped)
        struct.pack_into('<HH', data, len(data) - 22 + 8, 60000, 60000)
        with patch.object(verifier.zipfile, 'ZipFile') as zip_reader:
            with self.assertRaises(verifier.ArtifactVerificationError):
                verifier._zip_contents(bytes(data))
            zip_reader.assert_not_called()

    def test_artifact_pagination_is_bounded_complete_and_rejects_duplicate_ids(self):
        fixture = Fixture()
        first = [{'id': number, 'name': 'other', 'expired': False} for number in range(1, 101)]
        pages = iter([{'total_count': 101, 'artifacts': first},
                      {'total_count': 101, 'artifacts': [fixture.artifact]}])
        class Client:
            def api(self, _):
                return next(pages)
        self.assertEqual(verifier._artifact_metadata(Client(), 123), fixture.artifact)
        fixture.artifacts.append(dict(fixture.artifact))
        self.assertEqual(self.assert_rejected(fixture), 'artifact-list-invalid')

    def test_tar_links_paths_duplicates_member_count_and_size_are_rejected(self):
        for name in ('../firmware.bin', '/absolute', 'dir\\file', 'dir/../file', 'dir//file', 'C:/file'):
            member = tarfile.TarInfo(name); member.size = 1
            fixture = Fixture(); fixture.payload = tar_payload([(member, b'x')]); fixture.refresh()
            self.assertEqual(self.assert_rejected(fixture), 'tar-member-unsafe')
        for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE):
            member = tarfile.TarInfo('esp32-s3-idf/firmware.bin'); member.type = kind; member.linkname = '../other'
            fixture = Fixture(); fixture.payload = tar_payload([(member, b'')]); fixture.refresh()
            self.assertEqual(self.assert_rejected(fixture), 'tar-member-unsafe')
        member = tarfile.TarInfo('esp32-s3-idf/firmware.bin'); member.size = len(image())
        fixture = Fixture(); fixture.payload = tar_payload([(member, image()), (member, image())]); fixture.refresh()
        self.assertEqual(self.assert_rejected(fixture), 'tar-member-unsafe')
        entries = [(tarfile.TarInfo('entry-' + str(number)), b'') for number in range(129)]
        fixture = Fixture(); fixture.payload = tar_payload(entries); fixture.refresh()
        self.assertEqual(self.assert_rejected(fixture), 'tar-member-unsafe')
        fixture = Fixture()
        with patch.object(verifier, 'MAX_TAR_DATA', 511):
            self.assertEqual(self.assert_rejected(fixture), 'tar-data-size-limit')
        with patch.object(verifier, 'MAX_TAR_STREAM', 1024):
            self.assertEqual(self.assert_rejected(fixture), 'tar-stream-size-limit')
        member = tarfile.TarInfo('esp32-s3-idf/firmware.bin'); member.size = 287
        fixture = Fixture(); fixture.payload = tar_payload([(member, b'x' * 287)]); fixture.refresh()
        self.assertEqual(self.assert_rejected(fixture), 'firmware-member-invalid')

    def test_main_rechecked_immediately_before_publication_and_errors_sanitized(self):
        fixture = Fixture()
        with patch.object(verifier, '_GitHubClient', return_value=fixture):
            verifier.assert_main_unchanged(MAIN, TOKEN)
            with self.assertRaisesRegex(verifier.ArtifactVerificationError, 'main-changed'):
                verifier.assert_main_unchanged(HEAD, TOKEN)
        with patch.object(verifier, '_GitHubClient', side_effect=RuntimeError(TOKEN + ' signed-storage-url')):
            with self.assertRaises(verifier.ArtifactVerificationError) as error:
                verifier.verify_artifact('123', TOKEN)
            self.assertEqual(str(error.exception), 'artifact-verification-failed')
        with patch.object(verifier, '_GitHubClient') as client:
            for run_id in ('0', '01', '../123', '123?token=x', '9007199254740992', 123):
                with self.assertRaises(verifier.ArtifactVerificationError):
                    verifier.verify_artifact(run_id, TOKEN)
            client.assert_not_called()


class Response:
    def __init__(self, status=200, body=b'', headers=None):
        self.status = status; self.headers = headers or {}; self.body = io.BytesIO(body)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.body.close()

    def read1(self, size):
        return self.body.read(size)


class HttpTests(unittest.TestCase):
    def client(self, responses):
        requests = []

        class Opener:
            def open(self, request, timeout):
                requests.append((request, timeout))
                response = responses.pop(0)
                if isinstance(response, Exception):
                    raise response
                return response
        with patch.object(verifier.urllib.request, 'build_opener', return_value=Opener()):
            return verifier._GitHubClient(TOKEN), requests

    def test_signed_zip_redirect_does_not_forward_authorization(self):
        location = 'https://storage.example.test/private?signature=do-not-log'
        redirect = urllib.error.HTTPError('https://api.github.com/archive', 302, 'Found', {'Location': location}, io.BytesIO())
        client, requests = self.client([redirect, Response(body=b'ZIP', headers={'Content-Length': '3'})])
        self.assertEqual(client.artifact_zip(999), b'ZIP')
        self.assertEqual(len(requests), 2)
        self.assertEqual(requests[0][0].get_header('Authorization'), 'Bearer ' + TOKEN)
        self.assertIsNone(requests[1][0].get_header('Authorization'))
        self.assertEqual(requests[1][0].full_url, location)
        self.assertTrue(all(timeout == 15 for _, timeout in requests))

    def test_downgrade_userinfo_fragment_and_second_redirect_are_rejected(self):
        for location in ('http://storage.example.test/zip', 'https://user:pass@storage.example.test/zip',
                         'https://storage.example.test/zip#fragment', 'https://storage.example.test:80/zip'):
            client, requests = self.client([Response(302, headers={'Location': location})])
            with self.assertRaises(verifier.ArtifactVerificationError):
                client.artifact_zip(999)
            self.assertEqual(len(requests), 1)
        client, requests = self.client([Response(302, headers={'Location': 'https://storage.example.test/zip'}),
                                       Response(302, headers={'Location': 'https://other.example.test/zip'})])
        with self.assertRaisesRegex(verifier.ArtifactVerificationError, 'artifact-download-failed'):
            client.artifact_zip(999)
        self.assertEqual(len(requests), 2)

    def test_api_redirect_never_followed_and_http_bytes_are_bounded(self):
        client, requests = self.client([Response(302, headers={'Location': 'https://evil.example.test'})])
        with self.assertRaisesRegex(verifier.ArtifactVerificationError, 'github-api-failed'):
            client.api(verifier.API_ROOT + '/actions/runs/123')
        self.assertEqual(len(requests), 1)
        for response in (Response(body=b'abcd'), Response(headers={'Content-Length': '4'}),
                         Response(body=b'abc', headers={'Content-Length': '2'}),
                         Response(headers={'Content-Encoding': 'gzip'})):
            client, _ = self.client([response])
            with self.assertRaises(verifier.ArtifactVerificationError):
                client._request('https://storage.example.test/zip', 3, False)
        client, requests = self.client([])
        with self.assertRaisesRegex(verifier.ArtifactVerificationError, 'invalid-authenticated-origin'):
            client._request('https://evil.example.test/api', 3, True)
        self.assertEqual(requests, [])
        client, _ = self.client([Response(body=b'ab')])
        with patch.object(verifier.time, 'monotonic', side_effect=[0, 16]):
            with self.assertRaisesRegex(verifier.ArtifactVerificationError, 'http-time-limit'):
                client._request('https://storage.example.test/zip', 3, False)


if __name__ == '__main__':
    unittest.main()
