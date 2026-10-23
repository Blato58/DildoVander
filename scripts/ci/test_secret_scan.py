"""Exercise the pinned Gitleaks binary, without real credentials or ignore entries."""

import base64
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
CPP_SOCKET_FIXTURE = "firmware/tests/espidf_device_media_socket_test.cpp"
# Historical fixtures: full-history secret scans must still handle old commits
# after their source files have been removed from the current tree.
INVENTORY_PATHS = (
    "docs/project/evidence/fixture.json",
    "docs/project/artifacts/fixture.json",
    "docs/project/artifacts/KUFR-TWIN-MIC-QUEUE-deploy-v1/twin-release.json",
)
HISTORICAL_HEADER_COMMIT = "e5dc9e3df11c7ece97de70afa924357b00d86176"
VUE_HEADER_COMMIT = "d96ff01ac21f4d0a088739a93e811189cdaa5e3e"
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
VUE_HEADER_STATEMENTS = {
    "kufr/mobilni-aplikace/src/features/archive/nativeGuest.ts":
        "  'idempotency-key',\n  '" + EXPECTED_HEADER_NAMES[-1] + "',",
    "kufr/mobilni-aplikace/ios/App/App/AccountTransportPolicy.swift":
        "return " + json.dumps(EXPECTED_HEADER_NAMES) + ".contains(key)",
}


class SecretScanWorkflowTests(unittest.TestCase):
    def test_source_literal_exceptions_preserve_current_and_legacy_history_paths(self):
        config = tomllib.loads((ROOT / ".gitleaks.toml").read_text(encoding="utf-8"))
        exceptions = {entry["description"]: entry for entry in config["allowlists"]}
        paths = {
            "CAD component identifier, not an API credential": "mechanika/cad/scripts/generate.py",
            "Smoke test passes a generated idempotency key by variable": "backend/tests/deployed_smoke.py",
            "Reviewed synthetic firmware idempotency UUID": "firmware/tests/conversation_text_codec_test.cpp",
            "Synthetic host-only ESP-IDF socket test token": CPP_SOCKET_FIXTURE,
            "RFC 6455 public example nonce in the synthetic WebSocket handshake":
                "mobilni-aplikace/tests/native_handsfree/socket_peer.test.mjs",
        }
        for description, path in paths.items():
            with self.subTest(path=path):
                entry = exceptions[description]
                self.assertEqual(entry["condition"], "AND")
                self.assertEqual(entry["targetRules"], ["generic-api-key"])
                self.assertNotIn("commits", entry)
                self.assertTrue(any(re.fullmatch(pattern, path) for pattern in entry["paths"]))
                self.assertTrue(any(re.fullmatch(pattern, "kufr/" + path) for pattern in entry["paths"]))
                self.assertFalse(any(re.fullmatch(pattern, "unrelated/" + path) for pattern in entry["paths"]))

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
        exceptions = [entry for entry in config["allowlists"] if entry.get("commits")]
        self.assertEqual(4, len(exceptions))
        statements = {
            (commit, path): statement
            for commit, fixtures in (
                (HISTORICAL_HEADER_COMMIT, HISTORICAL_HEADER_STATEMENTS),
                (VUE_HEADER_COMMIT, VUE_HEADER_STATEMENTS),
            )
            for path, statement in fixtures.items()
        }
        matched_paths = set()
        for entry in exceptions:
            self.assertEqual(["generic-api-key"], entry["targetRules"])
            self.assertEqual("AND", entry["condition"])
            self.assertEqual(1, len(entry["commits"]))
            self.assertEqual("line", entry["regexTarget"])
            self.assertEqual(1, len(entry["paths"]))
            self.assertEqual(1, len(entry["regexes"]))
            matching = [key for key in statements
                        if key[0] == entry["commits"][0] and re.fullmatch(entry["paths"][0], key[1])]
            self.assertEqual(1, len(matching))
            key = matching[0]
            path = key[1]
            matched_paths.add(key)
            self.assertIsNotNone(re.fullmatch(entry["regexes"][0], statements[key]))
            self.assertIsNone(re.fullmatch(entry["regexes"][0], statements[key] + " // changed"))
            self.assertIsNone(re.fullmatch(entry["paths"][0], "unrelated/" + Path(path).name))
        self.assertEqual(set(statements), matched_paths)


@unittest.skipUnless(BINARY, "Set GITLEAKS_BINARY to run pinned scanner integration tests")
class SecretScanRepository(unittest.TestCase):
    header_commit = HISTORICAL_HEADER_COMMIT
    header_statements = HISTORICAL_HEADER_STATEMENTS

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
        # statements, so the scanner exercises every AND constraint.
        path = self.root / ".gitleaks.toml"
        config = path.read_text(encoding="utf-8")
        self.assertEqual(2, config.count(self.header_commit))
        path.write_text(config.replace(self.header_commit, self.git("rev-parse", "HEAD")), encoding="utf-8")

    @staticmethod
    def synthetic_token():
        return "ghp" + "_" + "Ab3Cd4Ef5Gh6Ij7Kl8Mn9Op0Qr1St2Uv3Wx4"

    def cpp_socket_statements(self):
        lines = (ROOT / CPP_SOCKET_FIXTURE).read_text(encoding="utf-8").splitlines()
        token = next(line for line in lines if 'const char* Token = ' in line)
        nonce = next(line for line in lines if 'Check(request.find("Sec-WebSocket-Key:' in line)
        self.assertIn(base64.b64encode(b"synthetic-test-token").decode("ascii"), token)
        self.assertIn(base64.b64encode(b"the sample nonce").decode("ascii"), nonce)
        return token + "\n" + nonce


class SecretScanIntegrationTests(SecretScanRepository):
    def test_export_idempotency_exception_is_exact_and_path_bound(self):
        path = "mobilni-aplikace/src/features/customer/customerDataExport.spec.ts"
        declarations = [line for line in (ROOT / path).read_text(encoding="utf-8").splitlines()
                        if line.startswith(("const keyOne = ", "const keyTwo = "))]
        self.assertEqual(2, len(declarations))
        self.write(path, "\n".join(declarations))
        self.commit("Synthetic export idempotency fixtures")
        self.assertEqual([], self.scan(0))
        self.write("unrelated/export.spec.ts", "\n".join(declarations))
        self.commit("Same declarations at unrelated path")
        findings = self.scan(1)
        self.assertEqual({"unrelated/export.spec.ts"}, {finding["File"] for finding in findings})

    def test_export_fixture_still_rejects_changed_values_and_credentials(self):
        path = "mobilni-aplikace/src/features/customer/customerDataExport.spec.ts"
        changed = hashlib.sha256(b"changed synthetic export idempotency value").hexdigest()
        self.write(path, f"const keyOne = '{changed}';\nconst keyTwo = '{self.synthetic_token()}';")
        self.commit("Changed synthetic export values")
        findings = self.scan(1)
        self.assertIn("generic-api-key", {finding["RuleID"] for finding in findings})
        self.assertIn("github-pat", {finding["RuleID"] for finding in findings})

    def test_file_hashes_survive_new_commits_and_squash_without_exceptions(self):
        digest = hashlib.sha256(b"synthetic compiled file bytes").hexdigest()
        for path in INVENTORY_PATHS:
            self.write(path, json.dumps({
                "assembly_access.py": digest,
                "backend/DeviceAccessTokenService.cs": digest,
                "Microsoft.AspNetCore.Authentication.BearerToken.dll": digest,
                "contracts/verify-backend-openapi.mjs": digest,
                "NativeAccessToken.swift": digest,
                "design-system/tokens.d.mts": digest,
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
        self.write("mechanika/cad/scripts/generate.py", '    key = "DFR0954_amplifier"')
        self.write("backend/tests/deployed_smoke.py",
                   '    "POST", "/v1/conversations", bearer=token, key=create_key, body=conversation_body')
        for name in ("text_codec", "start_coordinator"):
            self.write(f"firmware/tests/conversation_{name}_test.cpp",
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
        self.write("backend/tests/deployed_smoke.py", f'api_key = "{token}"')
        self.write("mechanika/cad/scripts/generate.py", f'key = "{token}"')
        self.write("firmware/tests/conversation_text_codec_test.cpp",
                   f'request.idempotencyKey = "{token}";')
        self.write("mobilni-aplikace/tests/native_handsfree/socket_peer.test.mjs",
                   f'const api_key = "{token}";')
        self.write(CPP_SOCKET_FIXTURE, f'const char* api_key = "{token}";')
        self.commit("Synthetic leaked credentials")
        findings = self.scan(1)
        self.assertIn("github-pat", {finding["RuleID"] for finding in findings})
        self.assertIn((CPP_SOCKET_FIXTURE, "github-pat"),
                      {(finding["File"], finding["RuleID"]) for finding in findings})
        self.assertIn("generic-api-key", {finding["RuleID"] for finding in findings})
        self.assertEqual(8, len({finding["File"] for finding in findings}))
        for path in INVENTORY_PATHS:
            self.assertEqual({"github-pat", "generic-api-key"},
                             {finding["RuleID"] for finding in findings if finding["File"] == path})

    def test_public_websocket_nonce_exception_requires_exact_value_and_fixture(self):
        path = "mobilni-aplikace/tests/native_handsfree/socket_peer.test.mjs"
        handshake = next(line for line in (ROOT / path).read_text(encoding="utf-8").splitlines()
                         if "Sec-WebSocket-Key:" in line)
        nonce = base64.b64encode(b"the sample nonce").decode("ascii")
        self.assertIn(nonce, handshake)
        self.write(path, handshake)
        self.commit("Public protocol example nonce")
        self.assertEqual([], self.scan(0))
        self.write("other-socket.mjs", handshake)
        self.commit("Same value outside its reviewed fixture")
        self.assertEqual({"other-socket.mjs"}, {item["File"] for item in self.scan(1)})

    def test_cpp_socket_fixture_values_pass_at_current_and_legacy_paths(self):
        for path in (CPP_SOCKET_FIXTURE, "kufr/" + CPP_SOCKET_FIXTURE):
            self.write(path, self.cpp_socket_statements())
        self.commit("Reviewed C++ socket fixture values")
        self.assertEqual([], self.scan(0))

    def test_cpp_socket_exceptions_reject_changed_values_in_same_file(self):
        content = self.cpp_socket_statements()
        for original in (b"synthetic-test-token", b"the sample nonce"):
            value = base64.b64encode(original).decode("ascii")
            content = content.replace(value, hashlib.sha256(b"different fixture " + original).hexdigest())
        self.write(CPP_SOCKET_FIXTURE, content)
        self.commit("Changed C++ socket fixture values")
        findings = self.scan(1)
        self.assertEqual(2, len(findings))
        self.assertEqual({CPP_SOCKET_FIXTURE}, {item["File"] for item in findings})
        self.assertEqual({"generic-api-key"}, {item["RuleID"] for item in findings})

    def test_cpp_socket_fixture_values_remain_blocked_at_unrelated_paths(self):
        self.write("firmware/tests/other_socket_test.cpp", self.cpp_socket_statements())
        self.commit("Same socket values outside the reviewed fixture")
        findings = self.scan(1)
        self.assertEqual(2, len(findings))
        self.assertEqual({"firmware/tests/other_socket_test.cpp"}, {item["File"] for item in findings})
        self.assertEqual({"generic-api-key"}, {item["RuleID"] for item in findings})

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


class HistoricalHeaderIntegrationTests(SecretScanRepository):
    def test_historical_header_statements_pass_only_when_all_constraints_match(self):
        for path, statement in self.header_statements.items():
            self.write(path, statement)
        self.commit("Reviewed header declaration fixtures")
        self.scope_header_exceptions_to_fixture_commit()
        self.assertEqual([], self.scan(0))
        self.assertFalse((self.root / ".gitleaksignore").exists())

    def test_header_exception_does_not_suppress_an_adjacent_generic_credential(self):
        digest = hashlib.sha256(b"synthetic adjacent generic credential").hexdigest()
        statements = self.header_statements
        for path, statement in statements.items():
            self.write(path, statement + '\napi_key = "' + digest + '"')
        self.commit("Adjacent synthetic credentials")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(
            finding["RuleID"] == "generic-api-key"
            and finding["StartLine"] == statements[finding["File"]].count("\n") + 2
            for finding in findings
        ))

    def test_header_exception_does_not_suppress_a_changed_header_literal(self):
        digest = hashlib.sha256(b"synthetic substituted header credential").hexdigest()
        statements = self.header_statements
        for path, statement in statements.items():
            self.write(path, statement.replace("x-dv10" + "-consent", digest))
        self.commit("Changed header literal fixtures")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))

    def test_header_exception_does_not_apply_at_unrelated_paths(self):
        paths = set()
        for path, statement in self.header_statements.items():
            unrelated = "unrelated/" + Path(path).name
            paths.add(unrelated)
            self.write(unrelated, statement)
        self.commit("Header declarations outside reviewed paths")
        self.scope_header_exceptions_to_fixture_commit()
        findings = self.scan(1)
        self.assertEqual(paths, {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))

    def test_header_exception_does_not_apply_in_another_commit(self):
        statements = self.header_statements
        for path, statement in statements.items():
            self.write(path, statement)
        self.commit("Same statements in a different commit")
        findings = self.scan(1)
        self.assertEqual(set(statements), {finding["File"] for finding in findings})
        self.assertTrue(all(finding["RuleID"] == "generic-api-key" for finding in findings))


class VueHeaderIntegrationTests(HistoricalHeaderIntegrationTests):
    header_commit = VUE_HEADER_COMMIT
    header_statements = VUE_HEADER_STATEMENTS


if __name__ == "__main__":
    unittest.main()
