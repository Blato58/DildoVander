import importlib.util
from contextlib import closing
import io
import hashlib
import json
from pathlib import Path
import sqlite3
import tarfile
import tempfile
import unittest


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


recovery = load("keepsake_recovery")
release = load("keepsake_release")


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.database = self.root / "data" / "journal.sqlite"
        self.database.parent.mkdir()
        with closing(sqlite3.connect(self.database)) as database:
            database.execute("PRAGMA journal_mode=WAL")
            database.execute("CREATE TABLE synthetic (id INTEGER PRIMARY KEY, state TEXT)")
            database.execute("INSERT INTO synthetic VALUES (1, 'retained')")
            database.executescript("""
                CREATE TABLE dv10_recovery_state(store_id TEXT PRIMARY KEY,revision INTEGER);
                INSERT INTO dv10_recovery_state VALUES('synthetic-store',1);
                CREATE TABLE dv10_revocations(id TEXT PRIMARY KEY,keepsake_id TEXT,session_hash TEXT,kind TEXT,revision INTEGER,created INTEGER);
                INSERT INTO dv10_revocations VALUES('revocation-1',NULL,NULL,'initial-test',1,1);
                CREATE TABLE dv10_deletions(id TEXT,keepsake_id TEXT,scope TEXT,local_state TEXT,requested INTEGER,session_hash TEXT);
                CREATE TABLE dv10_keepsakes(id TEXT PRIMARY KEY,deleted INTEGER,document TEXT);
                INSERT INTO dv10_keepsakes VALUES('retained-id',0,'private visitor story MUST NOT BE EXPORTED');
                CREATE TABLE dv10_jobs(id TEXT PRIMARY KEY,state TEXT,fence INTEGER,request_json TEXT);
            """)
            database.commit()
        self.archive = self.root / "data" / "archive"
        self.archive.mkdir()
        self.keys = self.root / "data" / "keyring"
        self.keys.mkdir()
        (self.keys / "synthetic-key.xml").write_bytes(b"synthetic key ring, never a real key")
        (self.archive / "original.bin").write_bytes(b"synthetic accepted photograph")
        (self.archive / "history.json").write_text('{"edition":1}')
        self.evidence = self.root / "evidence.json"
        self.value = recovery.export_evidence(self.database, self.archive, self.keys, self.evidence, True)
        self.snapshot = self.root / "isolated" / "snapshot"

    def take_snapshot(self):
        recovery.snapshot(self.database, self.archive, self.evidence, self.snapshot, True, self.keys)

    def test_online_database_and_retained_bytes_round_trip(self):
        self.take_snapshot()
        result = recovery.verify(self.snapshot, self.evidence)
        self.assertEqual(result["result"], "verified_isolated_only")
        self.assertFalse(result["publicStartAuthorized"])
        self.assertFalse(result["providerStartAuthorized"])
        self.assertEqual((self.snapshot / "archive" / "original.bin").read_bytes(), (self.archive / "original.bin").read_bytes())
        self.assertEqual((self.snapshot / "keyring" / "synthetic-key.xml").read_bytes(), (self.keys / "synthetic-key.xml").read_bytes())
        with closing(sqlite3.connect(self.snapshot / "journal.sqlite")) as database:
            self.assertEqual(database.execute("SELECT state FROM synthetic").fetchone()[0], "retained")

    def test_writers_must_be_quiesced(self):
        with self.assertRaises(ValueError):
            recovery.snapshot(self.database, self.archive, self.evidence, self.snapshot, False)
        self.assertFalse(self.snapshot.exists())

    def test_newer_delete_and_revocation_block_restore(self):
        self.take_snapshot()
        self.value.update(revision=2, deletedKeepsakeIds=["deleted-id"], revocationEvidenceHashes=["a" * 64])
        self.evidence.write_text(json.dumps(self.value))
        with self.assertRaisesRegex(ValueError, "differs"):
            recovery.verify(self.snapshot, self.evidence)

    def test_ambiguous_attempts_block_provider_start(self):
        with closing(sqlite3.connect(self.database)) as database:
            database.execute("INSERT INTO dv10_jobs VALUES('sent-before-crash','dispatched',1,'private request MUST NOT BE EXPORTED')")
            database.commit()
        self.evidence.unlink()
        self.value = recovery.export_evidence(self.database, self.archive, self.keys, self.evidence, True)
        self.take_snapshot()
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            recovery.verify(self.snapshot, self.evidence)

    def test_authoritative_export_has_no_content_or_secret(self):
        exported = self.evidence.read_text()
        self.assertNotIn("private visitor", exported)
        self.assertNotIn("synthetic key ring", exported)
        self.assertEqual(self.value["archiveSha256"], recovery.file_inventory(self.archive))
        self.assertEqual(len(self.value["revocationEvidenceHashes"]), 1)

    def test_missing_revocation_revision_rejected(self):
        with closing(sqlite3.connect(self.database)) as database:
            database.execute("UPDATE dv10_recovery_state SET revision=2")
            database.commit()
        with self.assertRaisesRegex(ValueError, "revision gap"):
            recovery.export_evidence(self.database, self.archive, self.keys, self.root / "new-evidence.json", True)

    def test_snapshot_rejects_mismatched_authority(self):
        self.value["revision"] = 0
        self.evidence.write_text(json.dumps(self.value))
        with self.assertRaisesRegex(ValueError, "SQLite authority"):
            self.take_snapshot()
        self.assertFalse(self.snapshot.exists())

    def test_retained_hash_change_fails(self):
        self.take_snapshot()
        (self.snapshot / "archive" / "original.bin").write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "hash"):
            recovery.verify(self.snapshot, self.evidence)

    def test_new_untracked_content_fails(self):
        self.take_snapshot()
        (self.snapshot / "archive" / "unexpected.bin").write_bytes(b"untracked")
        with self.assertRaises(ValueError):
            recovery.verify(self.snapshot, self.evidence)

    def test_unsafe_restore_tar_rejected_before_any_write(self):
        bundle = self.root / "unsafe.tar"
        with tarfile.open(bundle, "w") as archive:
            member = tarfile.TarInfo("../escape")
            member.size = 3
            archive.addfile(member, io.BytesIO(b"bad"))
        with self.assertRaises(ValueError):
            recovery.unpack(bundle, self.root / "restore")
        self.assertFalse((self.root / "restore").exists())

    def test_snapshot_unpack_round_trip(self):
        self.take_snapshot()
        bundle = self.root / "snapshot.tar"
        with tarfile.open(bundle, "w") as archive:
            for path in self.snapshot.rglob("*"):
                archive.add(path, arcname=path.relative_to(self.snapshot), recursive=False)
        recovery.unpack(bundle, self.root / "restore")
        self.assertEqual(recovery.verify(self.root / "restore", self.evidence)["revision"], 1)


class ReleaseTests(unittest.TestCase):
    def test_shared_application_schema_two_composes_without_legacy_shells(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            backend = root / "backend"
            web = root / "web"
            backend.mkdir()
            web.mkdir()
            (backend / "backend").write_bytes(b"synthetic backend")
            (web / "index.html").write_text("synthetic compiled shared application")
            provenance = {
                "schemaVersion": 2, "artifactType": "application", "apiContractVersion": 1,
                "applicationSourceRevision": "a" * 40,
                "applicationSourceDirty": False,
                "sha256": {"index.html": hashlib.sha256((web / "index.html").read_bytes()).hexdigest()},
            }
            (web / "site-manifest.json").write_text(json.dumps(provenance))
            release.package(backend, web, root / "release", "a" * 40)
            self.assertEqual({"index.html", "site-manifest.json"},
                             {path.name for path in (root / "release/web").iterdir()})
            with self.assertRaises(ValueError):
                release.package(backend, web, root / "wrong-revision", "b" * 40)

    def test_combines_code_and_excludes_database(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            backend, web = root / "backend", root / "web"
            backend.mkdir()
            web.mkdir()
            (backend / "backend").write_bytes(b"synthetic executable")
            (web / "index.html").write_text("<!doctype html><title>synthetic</title>")
            (web / "keepsake").mkdir()
            (web / "design-system").mkdir()
            (web / "keepsake" / "index.html").write_text('<script src="/keepsake/app.js"></script><link href="/design-system/workshop.css" rel="stylesheet">')
            (web / "keepsake" / "app.js").write_text("// synthetic public-root script")
            (web / "design-system" / "workshop.css").write_text("/* synthetic shared tokens */")
            web_hashes = {path.relative_to(web).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                          for path in web.rglob("*") if path.is_file()}
            web_provenance = {"schemaVersion": 1, "sourceRevision": "a" * 40, "sourceDirty": False, "sha256": web_hashes}
            (web / "keepsake-manifest.json").write_text(json.dumps(web_provenance))
            release.package(backend, web, root / "release", "a" * 40)
            manifest = json.loads((root / "release" / "release-manifest.json").read_text())
            self.assertEqual(set(manifest["sha256"]), {
                "backend/backend", "web/index.html", "web/keepsake/index.html",
                "web/keepsake/app.js", "web/design-system/workshop.css", "web/keepsake-manifest.json"})
            self.assertTrue((root / "release" / "web" / "index.html").is_file())
            self.assertTrue((root / "release" / "web" / "keepsake" / "app.js").is_file())
            self.assertFalse((root / "release" / "web" / "keepsake" / "keepsake").exists())
            web_provenance["sourceDirty"] = True
            (web / "keepsake-manifest.json").write_text(json.dumps(web_provenance))
            with self.assertRaises(ValueError):
                release.package(backend, web, root / "dirty", "a" * 40)
            self.assertFalse((root / "dirty").exists())
            web_provenance["sourceDirty"] = False
            (web / "keepsake-manifest.json").write_text(json.dumps(web_provenance))
            (web / "keepsake-manifest.json").unlink()
            for route in ("companion", "workshop", "simulator"):
                (web / route).mkdir()
                (web / route / "index.html").write_text("synthetic existing project route")
            (web / "index.html").write_text("synthetic project homepage")
            site_provenance = {"schema": 1, "source_sha": "a" * 40,
                               "sha256": {path.relative_to(web).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                                          for path in web.rglob("*") if path.is_file()}}
            (web / "site-manifest.json").write_text(json.dumps(site_provenance))
            release.package(backend, web, root / "full-site", "a" * 40)
            self.assertEqual((root / "full-site/web/index.html").read_text(), "synthetic project homepage")
            for route in ("companion", "workshop", "simulator", "keepsake"):
                self.assertTrue((root / "full-site/web" / route / "index.html").is_file())
            with self.assertRaises(ValueError):
                release.package(backend, web, root / "wrong-site-revision", "b" * 40)
            (web / "index.html").write_text("tampered homepage")
            with self.assertRaises(ValueError):
                release.package(backend, web, root / "tampered-site", "a" * 40)
            (web / "index.html").write_text("synthetic project homepage")
            (backend / "journal.sqlite").write_bytes(b"private")
            with self.assertRaises(ValueError):
                release.package(backend, web, root / "unsafe", "a" * 40)
            self.assertFalse((root / "unsafe").exists())


if __name__ == "__main__":
    unittest.main()
