"""Model deployment transaction tests, with synthetic data and no SSH."""
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "mechanika/final"))
from export_model_bundle import write_model_bundle
from preview_ui_fixture import fixture_payload
import cad_host
from cad_host import validate_bundle, validate_pointer, check_release
from release_store import publish_model, rollback_model


class CadHostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.store = self.root / "store"
        self.bundle = self.root / "bundle"
        self.manifest = write_model_bundle(fixture_payload(), self.bundle, source_revision="test")

    def publish(self, **kwargs):
        return publish_model(self.bundle, self.store, validator=validate_bundle, **kwargs)

    def next_bundle(self):
        payload = fixture_payload()
        payload["revision"] = "next"
        self.manifest = write_model_bundle(payload, self.bundle, source_revision="next")

    def test_production_rejects_unverified_and_fixtures(self):
        with self.assertRaisesRegex(ValueError, "Production requires"):
            validate_bundle(self.bundle, "production")
        write_model_bundle(fixture_payload(), self.bundle, source_revision="test", fixture=True)
        with self.assertRaisesRegex(ValueError, "actual CAD"):
            validate_bundle(self.bundle)

    def test_transfer_tampering_and_undeclared_files_rejected(self):
        (self.bundle / "model.json").write_text("{}")
        with self.assertRaisesRegex(ValueError, "integrity"):
            self.publish()
        self.assertFalse((self.store / "active.json").exists())
        write_model_bundle(fixture_payload(), self.bundle, source_revision="test")
        (self.bundle / "extra.json").write_text("{}")
        with self.assertRaisesRegex(ValueError, "undeclared"):
            self.publish()

    def test_failed_http_checks_restore_active_and_previous(self):
        first = self.publish()
        self.next_bundle()
        def fail(pointer):
            self.assertEqual(json.loads((self.store / "active.json").read_bytes()), pointer)
            raise ValueError("HTTP readback failed")
        with self.assertRaisesRegex(ValueError, "HTTP readback"):
            self.publish(check=fail)
        self.assertEqual(json.loads((self.store / "active.json").read_bytes()), first)
        self.assertFalse((self.store / "previous.json").exists())

    def test_first_deploy_failure_leaves_no_active_pointer(self):
        with self.assertRaisesRegex(ValueError, "failed"):
            self.publish(check=lambda pointer: (_ for _ in ()).throw(ValueError("failed")))
        self.assertFalse((self.store / "active.json").exists())

    def test_failed_third_deploy_preserves_both_recovery_pointers(self):
        first = self.publish()
        self.next_bundle()
        second = self.publish()
        payload = fixture_payload()
        payload["revision"] = "third"
        write_model_bundle(payload, self.bundle, source_revision="third")
        def fail(pointer):
            self.assertEqual(json.loads((self.store / "previous.json").read_bytes()), second)
            raise ValueError("failed")
        with self.assertRaisesRegex(ValueError, "failed"):
            self.publish(check=fail)
        self.assertEqual(json.loads((self.store / "active.json").read_bytes()), second)
        self.assertEqual(json.loads((self.store / "previous.json").read_bytes()), first)

    def test_idempotent_publish_preserves_rollback_and_toggle(self):
        first = self.publish()
        self.next_bundle()
        second = self.publish()
        self.publish()
        self.assertEqual(json.loads((self.store / "previous.json").read_bytes()), first)
        self.assertEqual(rollback_model(self.store, validator=validate_bundle), first)
        self.assertEqual(rollback_model(self.store, validator=validate_bundle), second)

    def test_rollback_failure_restores_current(self):
        self.publish()
        self.next_bundle()
        second = self.publish()
        with self.assertRaisesRegex(ValueError, "failed"):
            rollback_model(self.store, validator=validate_bundle,
                           check=lambda pointer: (_ for _ in ()).throw(ValueError("failed")))
        self.assertEqual(json.loads((self.store / "active.json").read_bytes()), second)

    def test_pointer_paths_cannot_escape_store(self):
        with self.assertRaisesRegex(ValueError, "pointer"):
            validate_pointer({"schemaVersion": 1, "bundleHash": "../../private", "manifestPath": "x"})

    def test_publisher_imports_from_shallow_portable_location(self):
        source = Path(cad_host.__file__).with_name("release_store.py").read_text(encoding="utf-8")
        namespace = {"__file__": str(Path(Path.cwd().anchor) / "release_store.py"),
                     "__name__": "portable_store_test"}
        exec(compile(source, namespace["__file__"], "exec"), namespace)
        self.assertTrue(callable(namespace["publish_model"]))

    def test_oversized_directory_is_rejected_before_hashing(self):
        with (self.bundle / "oversized.json").open("wb") as stream:
            stream.truncate(cad_host.LIMIT + 128 * 1024 + 1)
        with patch.object(cad_host, "inventory", side_effect=AssertionError("Must not hash oversized input")):
            with self.assertRaisesRegex(ValueError, "size limit"):
                validate_bundle(self.bundle)

    def test_root_aliases_cannot_share_preview_and_production(self):
        preview, production = self.root / "preview", self.root / "production"
        preview.mkdir()
        production.mkdir()
        config = {"schemaVersion": 1,
                  "preview": {"root": str(preview / ".." / "production"), "enabled": True,
                              "viewerSchema": 1, "baseUrl": "https://example.invalid/model-previews"},
                  "production": {"root": str(production), "enabled": True,
                                 "viewerSchema": 1, "baseUrl": "https://example.invalid/models"}}
        config_path = SimpleNamespace(stat=lambda: SimpleNamespace(st_uid=0, st_mode=0o644))
        with patch.object(cad_host, "CONFIG", config_path), patch.object(cad_host, "read_json", return_value=config):
            with self.assertRaisesRegex(ValueError, "must be separate"):
                cad_host.configuration("preview")

    def test_actual_http_readback_checks_asset_bytes_and_rolls_back_on_failure(self):
        class Handler(SimpleHTTPRequestHandler):
            def log_message(self, *args):
                pass
        self.store.mkdir()
        server = ThreadingHTTPServer(("127.0.0.1", 0), partial(Handler, directory=str(self.store)))
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            base = "http://127.0.0.1:" + str(server.server_port)
            first = self.publish(check=lambda pointer: check_release(self.store, base, pointer))
            self.next_bundle()
            # Exact HTTP pointer succeeds, but an incorrect model response fails.
            real_get = cad_host.http_bytes
            def corrupt(url, maximum):
                return b"{}" if url.endswith("/model.json") else real_get(url, maximum)
            with patch.object(cad_host, "http_bytes", side_effect=corrupt):
                with self.assertRaisesRegex(ValueError, "readback differs"):
                    self.publish(check=lambda pointer: check_release(self.store, base, pointer))
            self.assertEqual(json.loads((self.store / "active.json").read_bytes()), first)
            check_release(self.store, base, first)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    unittest.main()
