"""Regression checks for artifact-free publication and recovery boundaries."""
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
    def test_deploy_uses_one_job_without_artifact_transport_or_secrets(self):
        self.assertNotIn('download-artifact', BUILD + DEPLOY)
        self.assertNotIn('upload-artifact', DEPLOY)
        self.assertNotIn('secrets:', BUILD + DEPLOY)
        self.assertNotIn('actions: read', BUILD + DEPLOY)
        self.assertIn("if: github.ref == 'refs/heads/main'", DEPLOY)
        self.assertIn('publish: true', DEPLOY)
        self.assertIn('default: false', BUILD)
        self.assertEqual(re.findall(r'^  ([\w-]+):$', BUILD.split('jobs:', 1)[1], re.MULTILINE), ['build'])
        self.assertIn('pack "$SITE_WORK/source" "$SITE_WORK/package"', BUILD)
        self.assertIn('unpack "$SITE_WORK/package" "$SITE_WORK/verified"', BUILD)

    def test_recovery_upload_excludes_deploy_pr_and_non_main_runs(self):
        recovery = step('Upload explicit recovery package')
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
        self.assertEqual(json.loads(result), [True, False, False, False, False, False, False])

    @unittest.skipIf(os.name == 'nt', 'Publication guard runs in the Linux runner shell')
    def test_publication_guard_rejects_unsafe_events_refs_and_pr_dispatch(self):
        guard = run_script(step('Reject unsafe publication requests'), 'run')
        for ref, event, pr_number, expected in [
                ('refs/heads/main', 'push', '', 0),
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
        for name in ['Unpack verified local package', 'Atomically publish site',
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


if __name__ == '__main__':
    unittest.main()
