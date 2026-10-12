"""Regression checks for hosted CI, private publication and artifact provenance."""
import json
import os
from pathlib import Path
import re
import subprocess
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[2]
BUILD = (ROOT / '.github/workflows/kufr-site.yml').read_text(encoding='utf-8')
DEPLOY = (ROOT / '.github/workflows/kufr-site-deploy.yml').read_text(encoding='utf-8')


def step(name):
    for block in BUILD.split('      - name: ')[1:]:
        if block.splitlines()[0] == name:
            return block
    raise AssertionError('Missing step: ' + name)


def condition(block):
    value = re.search(r'^        if: (.+)$', block, re.MULTILINE).group(1)
    return value.removeprefix('${{ ').removesuffix(' }}')


def run_script(block, key):
    return textwrap.dedent(block.split(key + ': |\n', 1)[1])


class SiteWorkflowTests(unittest.TestCase):
    def test_deploy_separates_hosted_build_from_protected_publication(self):
        self.assertNotIn('upload-artifact', DEPLOY)
        self.assertNotIn('secrets:', BUILD + DEPLOY)
        self.assertIn("github.event.repository.private == true", DEPLOY)
        self.assertIn('publish: true', DEPLOY)
        self.assertIn('default: false', BUILD)
        self.assertEqual(re.findall(r'^  ([\w-]+):$', BUILD.split('jobs:', 1)[1], re.MULTILINE), ['build', 'publish'])
        build, publish = BUILD.split('jobs:', 1)[1].split('\n  publish:\n', 1)
        self.assertIn('runs-on: ubuntu-24.04', build)
        self.assertNotIn('self-hosted', build)
        self.assertNotIn('environment:', build)
        self.assertNotIn('/var/www/', build)
        self.assertIn('needs: build', publish)
        self.assertIn('environment: backend-production', publish)
        self.assertIn('artifact-ids: ${{ needs.build.outputs.artifact_id }}', publish)
        self.assertIn('run-id: ${{ github.run_id }}', publish)
        self.assertIn('github-token: ${{ github.token }}', publish)
        self.assertIn('actions: read', publish)
        self.assertIn('pack "$SITE_WORK/source" "$SITE_WORK/package"', build)
        self.assertIn('unpack "$SITE_WORK/package" "$SITE_WORK/verified"', publish)
        self.assertIsNone(re.search(r'^  push:', DEPLOY, re.MULTILINE), 'Site deployment must be deliberately dispatched')

    def test_recovery_upload_excludes_deploy_pr_and_non_main_runs(self):
        recovery = step('Upload immutable main package')
        self.assertIn('retention-days: 1', recovery)
        cases = []
        for publish, ref, event, pr_number in [
                (False, 'refs/heads/main', 'workflow_dispatch', ''),
                (True, 'refs/heads/main', 'workflow_dispatch', ''),
                (True, 'refs/heads/main', 'push', ''),
                (False, 'refs/pull/172/merge', 'pull_request', ''),
                (False, 'refs/heads/main', 'workflow_dispatch', '172'),
                (False, 'refs/heads/feature', 'workflow_dispatch', ''),
                (False, 'refs/heads/main', 'push', '')]:
            cases.append({'inputs': {'publish': publish}, 'github': {
                'ref': ref, 'event_name': event, 'event': {'inputs': {'pr_number': pr_number}}}})
        script = '''
          const check = new Function('inputs', 'github', 'return (' + process.argv[1] + ')');
          const cases = JSON.parse(process.argv[2]);
          process.stdout.write(JSON.stringify(cases.map(c => !!check(c.inputs, c.github))));
        '''
        result = subprocess.check_output(
            ['node', '-e', script, condition(recovery), json.dumps(cases)], text=True)
        # The upload is also gated by the build job, which rejects publication from push.
        self.assertEqual(json.loads(result), [True, True, True, False, False, False, False])

    @unittest.skipIf(os.name == 'nt', 'Publication guard runs in the Linux runner shell')
    def test_publication_guard_rejects_unsafe_events_refs_and_pr_dispatch(self):
        guard = run_script(step('Reject unsafe publication requests'), 'run')
        for ref, event, pr_number, expected in [
                ('refs/heads/main', 'push', '', 1),
                ('refs/heads/main', 'workflow_dispatch', '', 0),
                ('refs/heads/feature', 'workflow_dispatch', '', 1),
                ('refs/pull/172/merge', 'pull_request', '', 1),
                ('refs/heads/main', 'pull_request_target', '', 1),
                ('refs/heads/main', 'workflow_dispatch', '172', 1)]:
            with self.subTest(ref=ref, event=event, pr_number=pr_number):
                result = subprocess.run(['bash', '-e', '-c', guard], capture_output=True,
                                        env={**os.environ, 'SOURCE_REF': ref,
                                             'SOURCE_EVENT': event, 'PR_NUMBER': pr_number})
                self.assertEqual(result.returncode, expected)

    def test_both_freshness_checks_reject_any_different_main_sha(self):
        for name in ['Reject stale main before publication', 'Recheck main immediately before activation']:
            check = step(name)
            self.assertEqual(condition(check), 'inputs.publish')
            script = run_script(check, 'script')
            harness = '''
              const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
              const execute = new AsyncFunction('github', 'context', process.argv[1]);
              const github = {rest: {git: {getRef: async args => {
                if (args.ref !== 'heads/main') throw new Error('Unexpected ref');
                return {data: {object: {sha: process.argv[2]}}};
              }}}};
              execute(github, {repo: {owner:'owner', repo:'repo'}}).catch(() => {process.exitCode = 1});
            '''
            for actual, expected_code in [('a' * 40, 0), ('b' * 40, 1)]:
                with self.subTest(step=name, actual=actual):
                    result = subprocess.run(['node', '-e', harness, script, actual], capture_output=True,
                                            env={**os.environ, 'EXPECTED_SHA': 'a' * 40})
                    self.assertEqual(result.returncode, expected_code, result.stderr)

    def test_mutations_readback_and_rollback_remain_publish_only(self):
        for name in ['Atomically publish site',
                     'Read back HTTPS pages and full download']:
            self.assertEqual(condition(step(name)), 'inputs.publish')
        rollback = step('Restore pre-deploy state after failed verification')
        self.assertEqual(condition(rollback), "inputs.publish && failure() && steps.publish.outcome == 'success'")
        self.assertIn('--rollback-activation', rollback)
        for name in ['Remove obsolete CAD files from the iOS route', 'Retain a bounded verified rollback set']:
            self.assertEqual(condition(step(name)), 'inputs.publish && success()')
        self.assertIn('--retain 3', step('Retain a bounded verified rollback set'))
        self.assertIn("('kufr-mechanika-prototyp.zip', 'viewer.html')", step('Remove obsolete CAD files from the iOS route'))
        self.assertIn('always()', condition(step('Remove local site staging')))

    def test_publication_job_rejects_forks_public_repo_wrong_branch_and_events(self):
        build = BUILD.split('jobs:', 1)[1].split('\n  publish:\n', 1)[0]
        publish = BUILD.split('jobs:', 1)[1].split('\n  publish:\n', 1)[1]
        build_guard = re.search(r'^    if: (.+)$', build, re.MULTILINE).group(1)
        publish_guard = re.search(r'^    if: (.+)$', publish, re.MULTILINE).group(1)
        build_guard = build_guard.removeprefix('${{ ').removesuffix(' }}')
        cases = []
        for repository, private, ref, event, requested in [
                ('Blato58/DildoVander', True, 'refs/heads/main', 'workflow_dispatch', True),
                ('Blato58/DildoVander', False, 'refs/heads/main', 'workflow_dispatch', True),
                ('someone/DildoVander', True, 'refs/heads/main', 'workflow_dispatch', True),
                ('Blato58/DildoVander', True, 'refs/heads/feature', 'workflow_dispatch', True),
                ('Blato58/DildoVander', True, 'refs/heads/main', 'push', True),
                ('Blato58/DildoVander', False, 'refs/pull/172/merge', 'pull_request', False),
                ('someone/DildoVander', False, 'refs/heads/main', 'workflow_dispatch', False),
                ('Blato58/DildoVander', None, 'refs/heads/main', 'workflow_dispatch', True)]:
            cases.append({'inputs': {'publish': requested}, 'github': {
                'repository': repository, 'ref': ref, 'event_name': event,
                'event': {'repository': {'private': private}}}})
        script = """
          const check = new Function('inputs', 'github', 'return (' + process.argv[1] + ')');
          process.stdout.write(JSON.stringify(JSON.parse(process.argv[2]).map(c => !!check(c.inputs, c.github))));
        """
        for guard, expected in [(build_guard, [True, False, False, False, False, True, True, False]),
                                (publish_guard, [True, False, False, False, False, False, False, False])]:
            with self.subTest(guard=guard):
                result = subprocess.check_output(['node', '-e', script, guard, json.dumps(cases)], text=True)
                self.assertEqual(json.loads(result), expected)

    def test_every_privileged_runner_job_is_private_main_and_environment_protected(self):
        for path in (ROOT / '.github/workflows').glob('*.yml'):
            jobs = path.read_text(encoding='utf-8').split('jobs:', 1)[1]
            for block in re.split(r'(?=^  [\w-]+:\s*$)', jobs, flags=re.MULTILINE):
                if 'runs-on: [self-hosted,' not in block:
                    continue
                with self.subTest(workflow=path.name, job=block.splitlines()[0]):
                    guard = re.search(r'^    if: (.+)$', block, re.MULTILINE).group(1)
                    self.assertIn("github.repository == 'Blato58/DildoVander'", guard)
                    self.assertIn('github.event.repository.private == true', guard)
                    self.assertIn("github.ref == 'refs/heads/main'", guard)
                    self.assertIn('environment: backend-production', block)

    def test_pr_and_reusable_builds_have_no_production_access(self):
        jobs_by_workflow = {
            'ci.yml': ['secret-scan', 'changes', 'repository', 'required'],
            'kufr-backend.yml': ['build'], 'kufr-firmware.yml': ['host-tests', 'firmware-build'],
            'kufr-web.yml': ['web'], 'kufr-ai-eval.yml': ['offline'],
            'kufr-backend-ip-https.yml': ['lint'], 'reuse-build.yml': ['verify'],
        }
        for name, names in jobs_by_workflow.items():
            workflow = (ROOT / '.github/workflows' / name).read_text(encoding='utf-8')
            jobs = re.split(r'(?=^  [\w-]+:\s*$)', workflow.split('jobs:', 1)[1], flags=re.MULTILINE)
            for job in jobs:
                if not job.strip() or job.splitlines()[0].strip().rstrip(':') not in names:
                    continue
                with self.subTest(workflow=name, job=job.splitlines()[0]):
                    self.assertIn('runs-on: ubuntu-24.04', job)
                    self.assertNotIn('self-hosted', job)
                    self.assertNotIn('secrets.', job)
                    self.assertNotIn('environment:', job)
                    self.assertNotIn('/var/www/', job)
                    self.assertNotIn('sudo ', job)

    def test_site_provenance_rejects_substituted_packages(self):
        script = run_script(step('Check provenance against this main workflow run'), 'script')
        manifest = {'schema': 1, 'repository': 'Blato58/DildoVander', 'run_id': 42, 'run_attempt': 1,
                    'event': 'workflow_dispatch', 'head_sha': 'a' * 40, 'source_sha': 'a' * 40,
                    'source_tree': 'b' * 40, 'pr_number': None}
        harness = """
          const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
          const execute = new AsyncFunction('require', 'github', 'context', process.argv[1]);
          const manifest = JSON.parse(process.argv[2]);
          const requireMock = name => {if (name !== 'node:fs') throw new Error('Unexpected module');
            return {readFileSync: () => JSON.stringify(manifest)};};
          const github = {rest: {git: {getCommit: async () => ({data: {tree: {sha: 'b'.repeat(40)}}})}}};
          execute(requireMock, github, {repo: {owner:'Blato58', repo:'DildoVander'},
            sha: 'a'.repeat(40), runAttempt: Number(process.argv[3])}).catch(() => {process.exitCode = 1});
        """
        variants = [(manifest, 0, 1), (manifest, 0, 2)] + [({**manifest, key: value}, 1, 1) for key, value in [
            ('schema', 2), ('repository', 'someone/DildoVander'), ('run_id', 43), ('run_attempt', 0), ('run_attempt', 2),
            ('event', 'pull_request'), ('head_sha', 'c' * 40), ('source_sha', 'c' * 40),
            ('source_tree', 'c' * 40), ('pr_number', 172)]]
        for candidate, expected, attempt in variants:
            with self.subTest(manifest=candidate):
                result = subprocess.run(['node', '-e', harness, script, json.dumps(candidate), str(attempt)], capture_output=True,
                                        env={**os.environ, 'BUILD_RUN_ID': '42', 'SITE_WORK': 'synthetic'})
                self.assertEqual(result.returncode, expected, result.stderr)

    def test_secret_scan_is_pinned_redacted_and_required_for_all_prs(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        pull_request = workflow.split('  pull_request:', 1)[1].split('  workflow_dispatch:', 1)[0]
        self.assertNotIn('paths:', pull_request)
        self.assertNotIn('paths-ignore:', pull_request)
        scan = workflow.split('  secret-scan:\n', 1)[1].split('  changes:\n', 1)[0]
        self.assertIn('runs-on: ubuntu-24.04', scan)
        self.assertIn('fetch-depth: 0', scan)
        self.assertIn('gitleaks/releases/download/v8.30.1/', scan)
        self.assertIn('sha256sum -c -', scan)
        self.assertIn('--redact=100', scan)
        self.assertIn('--log-opts=--all', scan)
        self.assertNotIn('secrets.', scan)
        required = workflow.split('  required:\n', 1)[1]
        self.assertIn('needs: [secret-scan,', required)
        self.assertIn('SECRET_SCAN_RESULT: ${{ needs.secret-scan.result }}', required)
        self.assertIn('if [ "$SECRET_SCAN_RESULT" != success ]', required)


if __name__ == '__main__':
    unittest.main()
