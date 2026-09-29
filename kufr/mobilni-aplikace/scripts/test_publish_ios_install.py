import hashlib
import importlib.util
import plistlib
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("publish-ios-install.py")
SPEC = importlib.util.spec_from_file_location("publish_ios_install", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PublishIosInstallTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.ipa = self.root / "App.ipa"
        self.checksum = self.root / "App.ipa.sha256"
        self.site = self.root / "site"
        self.site.mkdir()
        info = {
            "CFBundleIdentifier": "app.turquoise6409.green2444",
            "CFBundleShortVersionString": "1.0.0",
            "CFBundleVersion": "42",
        }
        with zipfile.ZipFile(self.ipa, "w") as archive:
            archive.writestr("Payload/App.app/Info.plist", plistlib.dumps(info))
        self.digest = hashlib.sha256(self.ipa.read_bytes()).hexdigest()
        self.checksum.write_text(f"{self.digest}  dist/ipa/App.ipa\n", encoding="ascii")

    def publish(self):
        return MODULE.publish(
            self.ipa,
            self.checksum,
            self.site,
            "2.28.202.220",
            "123-1",
            "app.turquoise6409.green2444",
        )

    def test_publishes_manifest_page_and_unchanged_ipa(self):
        self.assertEqual("https://2.28.202.220/ios/", self.publish())
        release = self.site / "releases" / "123-1"
        self.assertEqual(self.ipa.read_bytes(), (release / "kufr.ipa").read_bytes())
        manifest = plistlib.loads((release / "manifest.plist").read_bytes())
        item = manifest["items"][0]
        self.assertEqual(
            "https://2.28.202.220/ios/releases/123-1/kufr.ipa",
            item["assets"][0]["url"],
        )
        self.assertEqual("app.turquoise6409.green2444",
                         item["metadata"]["bundle-identifier"])
        self.assertEqual("42", item["metadata"]["bundle-version"])
        page = (self.site / "index.html").read_text(encoding="utf-8")
        self.assertIn("itms-services://?action=download-manifest", page)
        self.assertIn("https%3A%2F%2F2.28.202.220%2Fios%2Freleases%2F123-1%2Fmanifest.plist", page)
        self.assertIn("Verze 1.0.0 · sestavení 42", page)

    def test_rejects_bad_checksum_before_publishing(self):
        self.checksum.write_text("0" * 64 + "  App.ipa\n", encoding="ascii")
        with self.assertRaisesRegex(ValueError, "checksum does not match"):
            self.publish()
        self.assertFalse((self.site / "releases").exists())

    def test_rejects_empty_checksum(self):
        self.checksum.write_text("", encoding="ascii")
        with self.assertRaisesRegex(ValueError, "Invalid IPA checksum"):
            self.publish()

    def test_rejects_existing_release(self):
        self.publish()
        with self.assertRaisesRegex(ValueError, "already published"):
            self.publish()


if __name__ == "__main__":
    unittest.main()
