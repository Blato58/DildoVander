"""Exercise the pinned Gitleaks binary, without real credentials or ignore entries."""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import tomllib
import unittest


ROOT = Path(__file__).resolve().parents[2]
BINARY = os.environ.get("GITLEAKS_BINARY") or shutil.which("gitleaks")
SCAN_COMMAND = '"$RUNNER_TEMP/gitleaks" git --config=.gitleaks.toml --redact=100 --no-banner --log-opts=HEAD'
INVENTORY_PATHS = (
    "docs/project/evidence/fixture.json",
    "docs/project/artifacts/fixture.json",
    "docs/project/artifacts/KUFR-TWIN-MIC-QUEUE-deploy-v1/twin-release.json",
)
HISTORICAL_HEADER_COMMIT = "e5dc9e3df11c7ece97de70afa924357b00d86176"
EXPECTED_HEADER_NAMES = (
    "accept",
    "content-type",
    "x-dv10-csrf",
    "idempotency-key",
    "x-dv10-consent",
)
# These immutable statement fixtures are self-contained. Repository-check jobs
# use shallow checkouts; only the separate scanner job needs checkout ancestry.
HISTORICAL_HEADER_STATEMENTS = {
    "kufr/mobilni-aplikace/src/features/archive/nativeGuest.ts":
        "if (![" + ", ".join(repr(name) for name in EXPECTED_HEADER_NAMES)
        + "].includes(name)) throw new GuestError(400);",
    "kufr/mobilni-aplikace/ios/App/App/KufrIdentityPlugin.swift":
        "let permitted = " + json.dumps(EXPECTED_HEADER_NAMES),
}


def historical_header_statements():
    return dict(HISTORICAL_HEADER_STATEMENTS)


class SecretScanWorkflowTests(unittest.TestCase):
    def test_shallow_checkout_runs_without_historical_objects(self):
        with tempfile.TemporaryDirectory() as directory:
            seed = Path(directory) / "seed"
            seed.mkdir()
            for relative in (".gitleaks.toml", ".github/workflows/ci.yml", "scripts/ci/test_secret_scan.py"):
                destination = seed / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / relative, destination)
            def git(*args):
                return subprocess.check_output(["git", "-C", str(seed), *args], text=True).strip()
            git("init", "--quiet", "--initial-branch=main")
            git("config", "user.email", "scan-fixture@localhost")
            git("config", "user.name", "Scan fixture")
            git("add", ".")
            git("commit", "--quiet", "-m", "Fixture source")
            (seed / "marker.txt").write_text("Second fixture commit\n", encoding="utf-8")
            git("add", ".")
            git("commit", "--quiet", "-m", "Fixture tip")
            shallow = Path(directory) / "shallow"
            subprocess.run(["git", "clone", "--quiet", "--depth=1", seed.as_uri(), str(shallow)], check=True, capture_output=True, text=True)
            is_shallow = subprocess.check_output(["git", "-C", str(shallow), "rev-parse", "--is-shallow-repository"], text=True).strip()
            self.assertEqual("true", is_shallow)
            historical = subprocess.run(["git", "-C", str(shallow), "cat-file", "-e", HISTORICAL_HEADER_COMMIT], capture_output=True, text=True)
            self.assertNotEqual(0, historical.returncode)
            environment = dict(os.environ)
            if BINARY:
                environment["GITLEAKS_BINARY"] = BINARY
            # Select header cases only, so this subprocess never recurses into
            # its shallow-checkout proof. All binary negatives run when present.
            result = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", "scripts/ci", "-p", "test_secret_scan.py", "-k", "header"],
                                    cwd=shallow, env=environment, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)

    def test_workflow_scans_checkout_ancestry_and_runs_binary_regressions(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        job = workflow.split("  secret-scan:\n", 1)[1].split("\n  changes:", 1)[0]
        self.assertIn(SCAN_COMMAND, job)
        self.assertNotIn("--log-opts=--all", job)
        self.assertIn("fetch-depth: 0", job)
        self.assertIn("GITLEAKS_BINARY=", job)
        self.assertIn("test_secret_scan.py", job)

    def test_header_exceptions_bind_the_exact_historical_commit_paths_and_statements(self):
        config = tomllib.loads((ROOT / ".gitleaks.toml").read_text(encoding="utf-8"))
        exceptions = [entry for entry in config["allowlists"] if entry["description"].startswith("Reviewed historical ")]
        self.assertEqual(2, len(exceptions))
        statements = historical_header_statements()
        matched_paths = set()
        for entry in exceptions:
            self.assertEqual(["generic-api-key"], entry["targetRules"])
            self.assertEqual("AND", entry["condition"])
            self.assertEqual([HISTORICAL_HEADER_COMMIT], entry["commits"])
            self.assertEqual("line", entry["regexTarget"])
            self.assertEqual(1, len(entry["paths"]))
            self.assertEqual(1, len(entry["regexes"]))
            matching = [path for path in statements if re.fullmatch(entry["paths"][0], path)]
            self.assertEqual(1, len(matching))
            path = matching[0]
            matched_paths.add(path)
            self.assertIsNotNone(re.fullmatch(entry["regexes"][0], statements[path]))
            self.assertIsNone(re.fullmatch(entry["regexes"][0], statements[path] + " // changed"))
            self.assertIsNone(re.fullmatch(entry["paths"][0], "unrelated/" + Path(path).name))
        self.assertEqual(set(statements), matched_paths)


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

    def scope_header_exceptions_to_fixture_commit(self):
        # A synthetic repository cannot reproduce an immutable production commit
        # hash. Change only the temporary copied policy, after committing fixture
        # statements, so the scanner exercises all three AND constraints.
        path = self.root / ".gitleaks.toml"
        config = path.read_text(encoding="utf-8")
        self.assertEqual(2, config.count(HISTORICAL_HEADER_COMMIT))
        path.write_text(config.replace(HISTORICAL_HEADER_COMMIT, self.git("rev-parse", "HEAD")), encoding="utf-8")

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

    def test_historical_header_statements_pass_only_when_all_constraints_match(self):
        for path, statement in historical_header_statements().items():
            self.write(path, statement)
        self.commit("Reviewed header declaration fixtures")
        self.scope_header_exceptions_to_fixture_commit()
        self.assertEqual([], self.scan(0))
        self.assertFalse((self.root / ".gitleaksignore").exists())

    def test_header_exception_does_not_suppress_an_adjacent_generic_credential(self):
        digest = hashlib.sha256(b"synthetic adjacent generic credential").hexdigest()
        statements = historical_header_statements()
        for path, statement in statements.items():
            self.write(path, statement + '\napi_key = "' + digest + '"')
        self.commit("Adjacent synthetic credentials")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" and finding["StartLine"] == 2 for finding in findings))

    def test_header_exception_does_not_suppress_a_changed_header_literal(self):
        digest = hashlib.sha256(b"synthetic substituted header credential").hexdigest()
        statements = historical_header_statements()
        for path, statement in statements.items():
            self.write(path, statement.replace("x-dv10" + "-consent", digest))
        self.commit("Changed header literal fixtures")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))

    def test_header_exception_does_not_apply_at_unrelated_paths(self):
        paths = set()
        for path, statement in historical_header_statements().items():
            unrelated = "unrelated/" + Path(path).name
            paths.add(unrelated)
            self.write(unrelated, statement)
        self.commit("Header declarations outside reviewed paths")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(paths, {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))

    def test_header_exception_does_not_apply_in_another_commit(self):
        statements = historical_header_statements()
        for path, statement in statements.items():
            self.write(path, statement)
        self.commit("Same statements in a different commit")
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))


if __name__ == "__main__":
    unittest.main()
