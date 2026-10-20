import hashlib
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("install_twin", Path(__file__).with_name("install-twin.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name).resolve()
        self.files = {name: name.encode() for name in
                      ("backend", "backend.deps.json", "backend.runtimeconfig.json", "libe_sqlite3.so", "native-twin", "viewer.html", "twin.manifest.json", "kufr-twin.service", "twin-locations.conf", "install-twin.py")}
        for name, data in self.files.items():
            (self.root / name).write_bytes(data)
        self.manifest()

    def tearDown(self):
        self.temp.cleanup()

    def manifest(self, files=None):
        (self.root / "twin-release.json").write_text(json.dumps({"buildId": "test", "files": files or
            {name: hashlib.sha256(data).hexdigest() for name, data in self.files.items()}}))

    def test_exact_release(self):
        self.assertEqual(module.verify_release(self.root)["buildId"], "test")

    def test_missing_sqlite_runtime_rejected_before_install(self):
        (self.root / "libe_sqlite3.so").unlink()
        self.files.pop("libe_sqlite3.so")
        self.manifest()
        with self.assertRaises(ValueError):
            module.verify_release(self.root)

    def test_reuse_provider_reads_only_existing_source_and_preserves_config(self):
        source = self.root / "existing-server.env"
        source.write_text("OPENAI_API_KEY=offline-placeholder\nDevices__0__DeviceId=physical-private\nBackend__DatabasePath=/production.sqlite\n")
        config, _ = module.initial_configuration("https://twin.test", self.root / "isolated", "test", "a" * 64)
        original = copy.deepcopy(config)
        selected = module.reuse_provider_source(config, source)
        self.assertEqual(config, original)
        self.assertTrue(selected["DigitalTwin"]["ExternalServicesAuthorized"])
        self.assertEqual(selected["DigitalTwin"]["ProviderEnvFile"], str(source))
        self.assertNotIn("offline-placeholder", json.dumps(selected))
        self.assertNotIn("physical-private", json.dumps(selected))
        self.assertEqual(source.read_text().count("OPENAI_API_KEY="), 1)

    def test_provider_reuse_refuses_guest_storage_and_invalid_source(self):
        source = self.root / "existing-server.env"
        source.write_text("OPENAI_API_KEY=offline-placeholder\n")
        config, _ = module.initial_configuration("https://twin.test", self.root / "isolated", "test", "a" * 64)
        for invalid in (Path("relative.env"), self.root / "missing.env"):
            with self.assertRaises(ValueError):
                module.reuse_provider_source(config, invalid)
        config["Backend"]["Memory"]["Enabled"] = True
        with self.assertRaises(ValueError):
            module.reuse_provider_source(config, source)
        config["Backend"]["Memory"]["Enabled"] = False
        config["Backend"]["DatabasePath"] = str(self.root / "production.sqlite")
        with self.assertRaises(ValueError):
            module.reuse_provider_source(config, source)

    def test_missing_inventory(self):
        self.manifest({"viewer.html": hashlib.sha256(self.files["viewer.html"]).hexdigest()})
        with self.assertRaises(ValueError):
            module.verify_release(self.root)

    def test_modified_binary(self):
        (self.root / "backend").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            module.verify_release(self.root)

    def test_unlisted_file(self):
        (self.root / "credential").write_bytes(b"never-package-private-data")
        with self.assertRaises(ValueError):
            module.verify_release(self.root)

    def test_traversal_manifest(self):
        self.manifest({"../backend": "0" * 64})
        with self.assertRaises(ValueError):
            module.verify_release(self.root)


class LiveDeviceMergeTests(unittest.TestCase):
    def setUp(self):
        self.config = {
            "Devices": [{"DeviceId": "sim-integration", "CredentialSha256": "0" * 64, "Custom": "preserve"}],
            "DigitalTwin": {
                "Instances": {"sim-development": {"Mode": "simulation", "DeviceId": "sim-development", "Custom": 7}},
                "Credentials": [{"Role": "observe", "Sha256": "9" * 64}],
                "Origin": "https://example.invalid", "Custom": {"retain": True},
            },
            "Backend": {"DatabasePath": "/private/existing.sqlite", "Custom": "keep"},
        }
        self.provisioned = [
            {"DeviceId": "kufr-001", "CredentialSha256": "b" * 64},
            {"DeviceId": "kufr-002", "CredentialSha256": "c" * 64},
        ]

    def test_upgrade_adds_only_requested_provisioned_device(self):
        original = copy.deepcopy(self.config)
        merged = module.merge_live_devices(self.config, ["kufr-001"], self.provisioned)
        self.assertEqual(merged["Devices"][-1], self.provisioned[0])
        self.assertEqual(merged["DigitalTwin"]["Instances"]["kufr-001"], {"Mode": "live", "DeviceId": "kufr-001"})
        self.assertNotIn("kufr-002", merged["DigitalTwin"]["Instances"])
        self.assertEqual(merged["DigitalTwin"]["Credentials"], original["DigitalTwin"]["Credentials"])
        self.assertEqual(merged["DigitalTwin"]["Custom"], original["DigitalTwin"]["Custom"])
        self.assertEqual(merged["Backend"], original["Backend"])
        self.assertEqual(merged["Devices"][0], original["Devices"][0])
        self.assertEqual(self.config, original)

    def test_idempotent_merge_preserves_existing_credentials_and_customization(self):
        merged = module.merge_live_devices(self.config, ["kufr-001"], self.provisioned)
        merged["Devices"][-1]["Custom"] = "device-note"
        merged["DigitalTwin"]["Instances"]["kufr-001"]["Custom"] = "instance-note"
        provisioned = copy.deepcopy(self.provisioned)
        provisioned[0]["CredentialSha256"] = provisioned[0]["CredentialSha256"].upper()
        repeated = module.merge_live_devices(merged, ["kufr-001", "kufr-001"], provisioned)
        self.assertEqual(repeated, merged)
        self.assertEqual(len(repeated["Devices"]), 2)

    def test_no_request_imports_no_provisioned_identity(self):
        self.assertEqual(module.merge_live_devices(self.config, [], self.provisioned), self.config)

    def test_fresh_configuration_uses_the_same_explicit_merge(self):
        config, credentials = module.initial_configuration("https://example.invalid", Path("/private/twin"), "build", "a" * 64)
        original_credentials = copy.deepcopy(config["DigitalTwin"]["Credentials"])
        merged = module.merge_live_devices(config, ["kufr-001"], self.provisioned)
        self.assertEqual(merged["DigitalTwin"]["Credentials"], original_credentials)
        self.assertEqual(set(credentials), {"observe", "control", "integration"})
        self.assertEqual(set(merged["DigitalTwin"]["Instances"]), {"sim-development", "sim-integration", "kufr-001"})

    def test_invalid_missing_and_ambiguous_requests_do_not_mutate_config(self):
        original = copy.deepcopy(self.config)
        for requested in ("sim-integration", "debug-server-admin", "../escape", ".", "..", "bad/id", "", "x" * 65):
            with self.subTest(requested=requested), self.assertRaises(ValueError):
                module.merge_live_devices(self.config, ["kufr-001", requested], self.provisioned)
        for source in ([], [{"DeviceId": "kufr-001", "CredentialSha256": "not-a-hash"}],
                       [self.provisioned[0], self.provisioned[0]]):
            with self.assertRaises(ValueError):
                module.merge_live_devices(self.config, ["kufr-001"], source)
        self.assertEqual(self.config, original)

    def test_simulation_other_device_alias_and_credential_collisions_reject(self):
        conflicting = []
        for instance in ({"Mode": "simulation", "DeviceId": "kufr-001"},
                         {"Mode": "live", "DeviceId": "kufr-other"}):
            config = copy.deepcopy(self.config)
            config["DigitalTwin"]["Instances"]["kufr-001"] = instance
            conflicting.append(config)
        config = copy.deepcopy(self.config)
        config["DigitalTwin"]["Instances"]["other-instance"] = {"Mode": "live", "DeviceId": "kufr-001"}
        conflicting.append(config)
        for device in ({"DeviceId": "kufr-001", "CredentialSha256": "f" * 64},
                       {"DeviceId": "kufr-other", "CredentialSha256": "b" * 64}):
            config = copy.deepcopy(self.config)
            config["Devices"].append(device)
            conflicting.append(config)
        for config in conflicting:
            original = copy.deepcopy(config)
            with self.assertRaises(ValueError):
                module.merge_live_devices(config, ["kufr-001"], self.provisioned)
            self.assertEqual(config, original)
        shared = copy.deepcopy(self.provisioned)
        shared[1]["CredentialSha256"] = shared[0]["CredentialSha256"]
        with self.assertRaises(ValueError):
            module.merge_live_devices(self.config, ["kufr-001", "kufr-002"], shared)

    def test_environment_reader_imports_only_identity_fields_with_existing_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "backend.env").write_text("Devices__0__DeviceId=kufr-001\nDevices__0__CredentialSha256=" + "b" * 64 +
                "\nOPENAI_API_KEY=not-imported\nDevices__1__DeviceId=kufr-002\nDevices__1__CredentialSha256=" + "c" * 64 + "\n")
            (root / "device-identities.env").write_text("Devices__0__CredentialSha256=" + "d" * 64 + "\n")
            records = module.read_provisioned_devices(root)
            merged = module.merge_live_devices(self.config, ["kufr-001"], records)
            self.assertEqual(merged["Devices"][-1]["CredentialSha256"], "d" * 64)
            self.assertEqual(len(merged["Devices"]), 2)
            self.assertTrue(all(set(record) == {"DeviceId", "CredentialSha256"} for record in records))


if __name__ == "__main__":
    unittest.main()
