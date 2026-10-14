import hashlib
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
                      ("backend", "native-twin", "viewer.html", "twin.manifest.json", "kufr-twin.service", "twin-locations.conf", "install-twin.py")}
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


if __name__ == "__main__":
    unittest.main()
