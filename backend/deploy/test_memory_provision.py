import contextlib
import importlib.util
import io
import json
import os
import sqlite3
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("memory_provision", Path(__file__).with_name("memory_provision.py"))
provision = importlib.util.module_from_spec(spec)
spec.loader.exec_module(provision)


class MemoryProvisioningDeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.directory = self.root / "metadata"
        self.directory.mkdir(mode=0o700)
        self.backend = self.root / "backend"
        self.backend.touch()

    def journal(self, personal="", motifs=""):
        with sqlite3.connect(self.directory / "journal.sqlite") as db:
            db.execute("CREATE TABLE IF NOT EXISTS server_settings (name TEXT PRIMARY KEY, value TEXT NOT NULL)")
            for scope, value in (("personal", personal), ("motifs", motifs)):
                if value:
                    db.execute("INSERT OR REPLACE INTO server_settings VALUES (?, ?)", (provision.SETTINGS[scope], value))
        (self.directory / "journal.sqlite").chmod(0o600)

    def test_metadata_is_outside_cleaned_checkout(self):
        path = provision.provisioning_directory(self.root / "repo" / "checkout")
        self.assertEqual(path, self.root / "repo" / ".kufr-memory-provisioning-production")
        self.assertNotIn("checkout", path.parts)

    def test_missing_metadata_is_not_created_by_deployment_read(self):
        missing = self.root / "missing"
        self.assertEqual(provision.read_stores(missing), {"personal": "", "motifs": ""})
        self.assertFalse(missing.exists())

    def test_partial_journal_survives_restart_and_is_used_for_next_create(self):
        self.journal("vs_first")
        observed = []
        def cli(command, **kwargs):
            observed.append(kwargs["env"]["Backend__Memory__PersonalStoreId"])
            self.journal("vs_first", "vs_second")
            return subprocess.CompletedProcess(command, 0, '\n'.join(json.dumps(row) for row in (
                {"scope": "Personal", "storeId": "vs_first", "verified": True, "exportedRecords": 0},
                {"scope": "EveningMotif", "storeId": "vs_second", "verified": True, "exportedRecords": 0})), "")
        with patch.dict(os.environ, {"OPENAI_API_KEY": "test-secret"}), contextlib.redirect_stdout(io.StringIO()) as output:
            result = provision.run_provisioning(self.backend, self.directory, "create", runner=cli)
            provision.run_provisioning(self.backend, self.directory, "verify", runner=cli)
        self.assertEqual(observed, ["vs_first", "vs_first"])
        self.assertEqual(result["storeIds"], {"personal": "vs_first", "motifs": "vs_second"})
        self.assertNotIn("test-secret", output.getvalue())

    def test_dry_run_removes_key_and_never_enables_export(self):
        def cli(command, **kwargs):
            self.assertNotIn("--execute", command)
            environment = kwargs["env"]
            self.assertNotIn("OPENAI_API_KEY", environment)
            for key in ("Enabled", "CloudIndexingEnabled", "LongTermPersonalEnabled"):
                self.assertEqual(environment["Backend__Memory__" + key], "false")
            self.assertEqual(environment["Backend__Memory__Environment"], "Production")
            self.assertEqual(kwargs["timeout"], 145)
            return subprocess.CompletedProcess(command, 0, '{"mode":"dry-run","networkRequests":0,"exports":0}', "")
        with patch.dict(os.environ, {"OPENAI_API_KEY": "must-not-reach-cli"}), contextlib.redirect_stdout(io.StringIO()):
            result = provision.run_provisioning(self.backend, self.directory, "dry-run", runner=cli)
        self.assertEqual(result["networkRequests"], 0)

    def test_wrong_or_shared_ids_do_not_repoint_installation(self):
        self.journal("vs_original", "vs_motifs")
        for personal, motifs in (("vs_foreign", ""), ("", "vs_original"), ("bad\nID", "")):
            with self.subTest(personal=personal), self.assertRaises(ValueError):
                provision.resolve_stores(self.directory, personal, motifs)

    def test_public_metadata_and_symlinks_are_rejected(self):
        self.journal("vs_personal", "vs_motifs")
        self.directory.chmod(0o755)
        with self.assertRaises(ValueError):
            provision.read_stores(self.directory)
        self.directory.chmod(0o700)
        database = self.directory / "journal.sqlite"
        target = self.directory / "real.sqlite"
        database.rename(target)
        database.symlink_to(target)
        with self.assertRaises(ValueError):
            provision.read_stores(self.directory)

    def test_provider_failure_keeps_partial_resource_without_fake_success(self):
        def cli(command, **kwargs):
            self.journal("vs_completed_first")
            return subprocess.CompletedProcess(command, 2, "", "Provisioning failed: http_429\nsecret-do-not-print")
        with patch.dict(os.environ, {"OPENAI_API_KEY": "key"}), contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaises(RuntimeError):
                provision.run_provisioning(self.backend, self.directory, "create", runner=cli)
        self.assertEqual(provision.read_stores(self.directory)["personal"], "vs_completed_first")
        self.assertIn("http_429", output.getvalue())
        self.assertNotIn("secret-do-not-print", output.getvalue())

    def test_execution_without_key_or_both_verified_results_fails(self):
        with patch.dict(os.environ, {"OPENAI_API_KEY": ""}), self.assertRaises(ValueError):
            provision.run_provisioning(self.backend, self.directory, "create")
        def cli(command, **kwargs):
            return subprocess.CompletedProcess(command, 0, '{"scope":"Personal","verified":true,"storeId":"vs_first","exportedRecords":0}', "")
        with patch.dict(os.environ, {"OPENAI_API_KEY": "key"}), self.assertRaises(ValueError):
            provision.run_provisioning(self.backend, self.directory, "create", runner=cli)


if __name__ == "__main__":
    unittest.main()
