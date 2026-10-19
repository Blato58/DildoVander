"""Exercise the pinned Gitleaks binary, without real credentials or ignore entries."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
BINARY = os.environ.get("GITLEAKS_BINARY") or shutil.which("gitleaks")
SCAN_COMMAND = '"$RUNNER_TEMP/gitleaks" git --config=.gitleaks.toml --redact=100 --no-banner --log-opts=HEAD'
INVENTORY_PATHS = (
    "docs/project/evidence/fixture.json",
    "docs/project/artifacts/fixture.json",
    "docs/project/artifacts/KUFR-TWIN-MIC-QUEUE-deploy-v1/twin-release.json",
)


class SecretScanWorkflowTests(unittest.TestCase):
    def test_workflow_scans_checkout_ancestry_and_runs_binary_regressions(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        job = workflow.split("  secret-scan:\n", 1)[1].split("\n  changes:", 1)[0]
        self.assertIn(SCAN_COMMAND, job)
        self.assertNotIn("--log-opts=--all", job)
        self.assertIn("fetch-depth: 0", job)
        self.assertIn("GITLEAKS_BINARY=", job)
        self.assertIn("test_secret_scan.py", job)


@unittest.skipUnless(BINARY, "Set GITLEAKS_BINARY to run pinned scanner integration tests")
class SecretScanIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        shutil.copyfile(ROOT / ".gitleaks.toml", self.root / ".gitleaks.toml")
        self.git("init", "--quiet", "--initial-branch=main")
        self.git("config", "user.email", "scan-fixture@localhost")
        self.git("config", "user.name", "Scan fixture")
        self.git("config", "core.autocrlf", "false")
        self.commit("Baseline")

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.root), *args], text=True).strip()

    def write(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content + "\n", encoding="utf-8")

    def commit(self, message):
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", message)

    def scan(self, expected, revision="HEAD"):
        report = self.root / "report.json"
        result = subprocess.run(
            [BINARY, "git", "--config=.gitleaks.toml", "--redact=100", "--no-banner",
             "--log-opts=" + revision, "--report-format=json", "--report-path=" + str(report)],
            cwd=self.root, capture_output=True, text=True,
        )
        self.assertEqual(expected, result.returncode, result.stderr)
        return json.loads(report.read_text(encoding="utf-8"))

    @staticmethod
    def synthetic_token():
        return "ghp" + "_" + "Ab3Cd4Ef5Gh6Ij7Kl8Mn9Op0Qr1St2Uv3Wx4"

    def test_file_hashes_survive_new_commits_and_squash_without_exceptions(self):
        digest = hashlib.sha256(b"synthetic compiled file bytes").hexdigest()
        for path in INVENTORY_PATHS:
            self.write(path, json.dumps({
                "assembly_access.py": digest,
                "kufr/backend/DeviceAccessTokenService.cs": digest,
                "Microsoft.AspNetCore.Authentication.BearerToken.dll": digest,
                "kufr/contracts/verify-backend-openapi.mjs": digest,
                "NativeAccessToken.swift": digest,
                "kufr/design-system/tokens.d.mts": digest,
            }, indent=2))
        self.commit("File inventories")
        self.assertEqual([], self.scan(0))
        self.write("unrelated.txt", "Later change")
        self.commit("Later commit")
        self.git("reset", "--soft", "HEAD~2")
        self.commit("Squashed inventories")
        self.assertEqual([], self.scan(0))
        self.assertFalse((self.root / ".gitleaksignore").exists())

    def test_reviewed_source_literals_pass_without_commit_fingerprints(self):
        self.write("kufr/mechanika/cad/scripts/generate.py", '    key = "DFR0954_amplifier"')
        self.write("kufr/backend/tests/deployed_smoke.py",
                   '    "POST", "/v1/conversations", bearer=token, key=create_key, body=conversation_body')
        for name in ("text_codec", "start_coordinator"):
            self.write(f"kufr/firmware/tests/conversation_{name}_test.cpp",
                       '  request.idempotencyKey = "7731ce10-a53c-4934-8909-06870fc86a12";')
        self.commit("Reviewed non-secrets")
        self.assertEqual([], self.scan(0))

    def test_real_detectors_still_block_secrets_in_allowed_files(self):
        token = self.synthetic_token()
        for path in INVENTORY_PATHS:
            self.write(path, json.dumps({
                "api_key": hashlib.sha256(b"synthetic secret value").hexdigest(),
                "access.py": token,
            }, indent=2))
        self.write("kufr/backend/tests/deployed_smoke.py", f'api_key = "{token}"')
        self.write("kufr/mechanika/cad/scripts/generate.py", f'key = "{token}"')
        self.write("kufr/firmware/tests/conversation_text_codec_test.cpp",
                   f'request.idempotencyKey = "{token}";')
        self.commit("Synthetic leaked credentials")
        findings = self.scan(1)
        self.assertIn("github-pat", {finding["RuleID"] for finding in findings})
        self.assertIn("generic-api-key", {finding["RuleID"] for finding in findings})
        self.assertEqual(6, len({finding["File"] for finding in findings}))
        for path in INVENTORY_PATHS:
            self.assertEqual({"github-pat", "generic-api-key"},
                             {finding["RuleID"] for finding in findings if finding["File"] == path})

    def test_hash_exception_requires_inventory_path(self):
        digest = hashlib.sha256(b"synthetic bytes").hexdigest()
        self.write("settings.json", json.dumps({"assembly_access.py": digest}, indent=2))
        self.write("docs/project/artifacts/other/nested.json",
                   json.dumps({"assembly_access.py": digest}, indent=2))
        self.commit("Hash outside inventory")
        findings = self.scan(1)
        self.assertEqual({"settings.json", "docs/project/artifacts/other/nested.json"},
                         {finding["File"] for finding in findings})

    def test_inventory_exception_does_not_suppress_provider_specific_detector(self):
        digest = hashlib.sha256(b"synthetic Sentry-shaped token").hexdigest()
        for path in INVENTORY_PATHS:
            self.write(path, json.dumps({"sentry_api_key.py": digest}, indent=2))
        self.commit("Synthetic provider token matching inventory format")
        findings = self.scan(1)
        self.assertIn("sentry-access-token", {finding["RuleID"] for finding in findings})
        for path in INVENTORY_PATHS:
            self.assertIn("sentry-access-token",
                          {finding["RuleID"] for finding in findings if finding["File"] == path})

    def test_regression_source_only_allows_the_reviewed_non_secret_values(self):
        path = "scripts/ci/test_secret_scan.py"
        self.write(path, (ROOT / path).read_text(encoding="utf-8"))
        self.commit("Scanner regression source")
        self.assertEqual([], self.scan(0))
        self.write(path, 'api_key = "' + self.synthetic_token() + '"')
        self.commit("Synthetic credential in regression source")
        self.assertTrue(self.scan(1))

    def test_removed_secret_remains_blocking_in_checkout_history(self):
        self.write("settings.txt", "api_key = " + self.synthetic_token())
        self.commit("Synthetic leak")
        self.write("settings.txt", "Removed from current files")
        self.commit("Remove leak")
        self.assertTrue(self.scan(1))

    def test_unrelated_fetched_branch_does_not_block_checkout(self):
        self.git("switch", "--quiet", "-c", "unrelated")
        self.write("settings.txt", "api_key = " + self.synthetic_token())
        self.commit("Unrelated synthetic leak")
        self.git("tag", "unrelated-tag")
        self.git("switch", "--quiet", "main")
        self.assertEqual([], self.scan(0))
        self.assertTrue(self.scan(1, "--all"))


if __name__ == "__main__":
    unittest.main()
