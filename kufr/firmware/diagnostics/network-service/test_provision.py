import copy
import json
from pathlib import Path
import tempfile
import unittest
from provision import make_request, private_receipt, exchange


class ServiceTests(unittest.TestCase):
    def test_frame_matches_firmware_contract(self):
        frame = make_request("a" * 32, "api.example.test", "kufr_1", "b" * 64)
        request = json.loads(frame)
        self.assertEqual("ACTUATORS_OFF_USB_SERVICE", request["physicalConfirmation"])
        self.assertEqual("kufr_1", request["deviceId"])
        self.assertTrue(frame.endswith(b"\n"))
        self.assertNotIn("password", request)

    def test_bad_credentials_and_hosts_fail_before_serial(self):
        for proof in ("", "A" * 32, "a" * 31, "a\n" * 16):
            with self.assertRaises(ValueError): make_request(proof)
        for host in ("https://example.test", "evil@host", "-bad.example", "x/secret", "a..b"):
            with self.assertRaises(ValueError): make_request("a" * 32, host)
        for credential in ("0" * 64, "c" * 63, "d\n" * 32):
            with self.assertRaises(ValueError): make_request("a" * 32, device_id="id", credential=credential)

    def test_ota_private_keys_and_unsafe_origins_rejected(self):
        ota = dict(origin="https://api.example.test:443", channel="stable", trustPem="-----BEGIN PUBLIC KEY-----\npublic-test-data",
                   automaticInstall=False, checkIntervalSeconds=3600)
        self.assertIn("ota", json.loads(make_request("a" * 32, ota=ota)))
        for key, value in (("origin", "http://example.test"), ("origin", "https://example.test/path"),
                           ("trustPem", "-----BEGIN PRIVATE KEY-----"), ("channel", "nightly"),
                           ("checkIntervalSeconds", True), ("checkIntervalSeconds", 59), ("automaticInstall", 1)):
            wrong = copy.deepcopy(ota); wrong[key] = value
            with self.assertRaises(ValueError): make_request("a" * 32, ota=wrong)

    def test_receipt_never_overwrites_and_contains_no_backend_credential(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "receipt.json"
            private_receipt(path, {"proofOfPossession": "a" * 32})
            self.assertEqual("a" * 32, json.loads(path.read_text())["proofOfPossession"])
            with self.assertRaises(FileExistsError): private_receipt(path, {})

    def test_serial_output_is_whitelisted(self):
        class Connection:
            def __init__(self, result): self.lines = [b"boot noise\n", b"KUFR_SERVICE_READY\n", result]; self.sent = None
            def readline(self, limit): return self.lines.pop(0)
            def write(self, frame): self.sent = frame; return len(frame)
            def flush(self): pass
        frame = make_request("a" * 32)
        connection = Connection(b"KUFR_SERVICE_RESULT saved\n")
        self.assertEqual("saved", exchange(connection, frame))
        self.assertEqual(frame, connection.sent)
        with self.assertRaises(RuntimeError): exchange(Connection(b"KUFR_SERVICE_RESULT secret echo\n"), frame)


if __name__ == "__main__": unittest.main()
