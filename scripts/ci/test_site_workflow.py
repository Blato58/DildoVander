"""Regression checks for the public build/private deployment boundary."""
import json
from pathlib import Path
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / '.github/workflows'


class PublicWorkflowTests(unittest.TestCase):
    def test_every_public_workflow_uses_hosted_runners_without_production_secrets(self):
        allowed = {'IOS_DISTRIBUTION_CERTIFICATE_BASE64', 'IOS_DISTRIBUTION_PROFILE_BASE64',
                   'IOS_P12_PASSWORD', 'IOS_KEYCHAIN_PASSWORD'}
        for path in WORKFLOWS.glob('*.yml'):
            with self.subTest(workflow=path.name):
                source = path.read_text()
                self.assertNotIn('self-hosted', source)
                self.assertNotIn('backend-production', source)
                self.assertNotIn('/var/www/', source)
                self.assertNotIn('workflow_run:', source)
                secrets = set(re.findall(r'secrets\.([A-Z0-9_]+)', source))
                self.assertTrue(secrets <= (allowed if path.name == 'kufr-ios-sign.yml' else set()))

    def test_site_workflow_only_builds_and_packages(self):
        source = (WORKFLOWS / 'kufr-site.yml').read_text()
        self.assertEqual(re.findall(r'^  ([\w-]+):$', source.split('jobs:', 1)[1], re.MULTILINE), ['build'])
        self.assertIn('runs-on: ubuntu-24.04', source)
        self.assertIn('uses: ./.github/actions/verified-cad', source)
        self.assertIn('pack "$SITE_WORK/source" "$SITE_WORK/package"', source)
        self.assertNotIn('inputs.publish', source)
        self.assertFalse((WORKFLOWS / 'kufr-site-deploy.yml').exists())

    def test_main_only_artifact_retention_excludes_pr_and_feature_builds(self):
        source = (WORKFLOWS / 'kufr-site.yml').read_text()
        upload = source.split('      - name: Upload immutable main package', 1)[1].split('      - name:', 1)[0]
        condition = re.search(r'^        if: (.+)$', upload, re.MULTILINE).group(1)
        condition = condition.replace('inputs.verify-manufacturing', "inputs['verify-manufacturing']")
        script = "const check = new Function('github', 'inputs', 'return (' + process.argv[1] + ')'); process.stdout.write(JSON.stringify(JSON.parse(process.argv[2]).map(c => !!check(c, { 'verify-manufacturing': c.verify ?? true }))));"
        cases = [{'ref': ref, 'event_name': event, 'event': {'inputs': {'pr_number': pr}}}
                 for ref, event, pr in [('refs/heads/main', 'workflow_dispatch', ''),
                                        ('refs/heads/main', 'workflow_dispatch', '42'),
                                        ('refs/pull/42/merge', 'pull_request', ''),
                                        ('refs/heads/feature', 'workflow_dispatch', ''),
                                        ('refs/heads/main', 'push', '')]]
        cases.append({**cases[0], 'verify': False})
        result = subprocess.check_output(['node', '-e', script, condition, json.dumps(cases)], text=True)
        self.assertEqual(json.loads(result), [True, False, False, False, False, False])
        self.assertIn('retention-days: 1', upload)

    def test_pr_site_checks_skip_every_expensive_or_publishable_step(self):
        source = (WORKFLOWS / 'kufr-site.yml').read_text()
        for name in ['Verify source-bound CAD outputs for a complete site package',
                     'Assemble verified site', 'Package with source provenance']:
            with self.subTest(step=name):
                block = source.split('      - name: ' + name, 1)[1].split('      - name:', 1)[0]
                self.assertIn('if: inputs.verify-manufacturing', block)
        upload = source.split('      - name: Upload immutable main package', 1)[1].split('      - name:', 1)[0]
        self.assertIn('if: inputs.verify-manufacturing &&', upload)
        self.assertNotIn('pip install', source)
        self.assertNotIn('install-cad-slicer.sh', source)
        ci = (WORKFLOWS / 'ci.yml').read_text()
        self.assertIn("verify-manufacturing: ${{ github.event_name == 'workflow_dispatch' && inputs.pr_number == '' }}", ci)
        self.assertIn('site, cad]', ci.split('  required:', 1)[1])

    def test_shared_cad_action_never_trusts_restore_alone(self):
        source = (ROOT / '.github/actions/verified-cad/action.yml').read_text()
        restore = source.index('      uses: actions/cache/restore@')
        verify = source.index('Regenerate.ps1')
        save = source.index('      uses: actions/cache/save@')
        self.assertLess(restore, verify)
        self.assertLess(verify, save)
        self.assertNotIn('restore-keys:', source)
        verify_block = source.split('    - name: Regenerate and verify', 1)[1].split('    - name:', 1)[0]
        self.assertNotIn('if:', verify_block)
        workflow = (WORKFLOWS / 'kufr-cad.yml').read_text()
        self.assertIn('branches: [main]', workflow)
        for path in ['kufr/mechanika/final/**', 'kufr/design-system/**',
                     'kufr/docs/zapojeni-potvrzeneho-hardwaru.md',
                     'kufr/mechanika/rozmery-nakoupenych-soucastek.md',
                     'scripts/ci/install-cad-slicer.sh', '.github/actions/verified-cad/**',
                     'kufr/mobilni-aplikace/.nvmrc']:
            with self.subTest(input=path):
                self.assertIn("- '" + path + "'", workflow)
        self.assertNotIn('upload-artifact', workflow)
        self.assertIn('uses: ./.github/actions/verified-cad', workflow)
        self.assertIn('group: cad-${{ github.event_name }}-${{ github.ref }}', workflow)
        self.assertIn("cancel-in-progress: ${{ github.event_name == 'push' || github.event_name == 'pull_request' }}", workflow)

    def test_signing_requires_canonical_main_dispatch_and_separate_environment(self):
        source = (WORKFLOWS / 'kufr-ios-sign.yml').read_text()
        for block in [source.split('  verify:', 1)[1].split('  signed-ipa:', 1)[0],
                      source.split('  signed-ipa:', 1)[1]]:
            self.assertIn("github.repository == 'Blato58/DildoVander'", block)
            self.assertIn("github.event_name == 'workflow_dispatch'", block)
            self.assertIn("github.ref == 'refs/heads/main'", block)
            self.assertNotIn('repository.private', block)
        self.assertIn('environment: ios-signing', source)
        self.assertIn('runs-on: macos-15', source)
        self.assertIn('artifact-ids: ${{ needs.verify.outputs.artifact_id }}', source)
        self.assertIn('node scripts/generate-native-assets.mjs', source)
        self.assertLess(source.index('npx cap sync ios'), source.index('node scripts/generate-native-assets.mjs'))
        self.assertLess(source.index('Recheck main immediately before signing'), source.index('      - name: Build signed IPA'))
        self.assertIn('retention-days: 3', source)

    def test_compiled_diagnostics_package_is_separate_from_backend_runtime(self):
        source = (WORKFLOWS / 'kufr-backend.yml').read_text()
        self.assertIn('name: backend-diagnostics', source)
        self.assertIn('pack kufr/backend-tests/bin/Release/net10.0/linux-x64', source)
        self.assertNotIn('OPENAI_API_KEY', source)
        self.assertIn("!github.event.inputs.pr_number", source)

    def test_secret_scan_is_pinned_redacted_and_required_for_all_prs(self):
        workflow = (WORKFLOWS / 'ci.yml').read_text()
        trigger = workflow.split('  pull_request:', 1)[1].split('  workflow_dispatch:', 1)[0]
        self.assertNotIn('paths:', trigger)
        self.assertNotIn('paths-ignore:', trigger)
        scan = workflow.split('  secret-scan:', 1)[1].split('  changes:', 1)[0]
        self.assertIn('fetch-depth: 0', scan)
        self.assertIn('gitleaks/releases/download/v8.30.1/', scan)
        self.assertIn('sha256sum -c -', scan)
        self.assertIn('--redact=100', scan)
        self.assertIn('--log-opts=HEAD', scan)
        self.assertIn('--config=.gitleaks.toml', scan)
        self.assertIn('needs: [secret-scan,', workflow.split('  required:', 1)[1])


if __name__ == '__main__':
    unittest.main()
