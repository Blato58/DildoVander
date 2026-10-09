"""Regression: an old export tree must not contaminate the delivered ZIP."""
from pathlib import Path
import tempfile
import unittest
import zipfile

from artifacts import ASSEMBLY_NAMES, export_names, package_files


class PackageInventoryTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"assemblies": list(ASSEMBLY_NAMES), "parts": [{"name": "body_1"}]}

    def test_stale_exports_and_unverified_sources_are_not_packaged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            current = {"build.py", "reference/photo.png"}
            current.update(f"exports/{name}" for name in export_names(self.manifest))
            stale = {
                "scratch.txt", "exports/assembly/old.step", "exports/images/old.png",
                "exports/old.svg", "exports/old-report.json", "exports/stl/old.stl",
                "exports/step/old.step", "exports/delivery-checks.json",
                "exports/F1-kouzelny-kufr.zip",
            }
            for name in current | stale:
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("test", encoding="utf-8")
            files = package_files(root, ["build.py", "reference/photo.png"], self.manifest)
            with zipfile.ZipFile(root / "result.zip", "w") as archive:
                for path in files:
                    archive.write(path, path.relative_to(root).as_posix())
            with zipfile.ZipFile(root / "result.zip") as archive:
                self.assertEqual(set(archive.namelist()), current)
                self.assertIsNone(archive.testzip())
            (root / "exports/images/open.png").unlink()
            with self.assertRaises(FileNotFoundError):
                package_files(root, ["build.py", "reference/photo.png"], self.manifest)

    def test_wrong_or_duplicate_assembly_inventory_is_rejected(self):
        for names in (list(ASSEMBLY_NAMES[:-1]), [*ASSEMBLY_NAMES, ASSEMBLY_NAMES[0]],
                      [*ASSEMBLY_NAMES[:-1], "old_assembly"]):
            with self.subTest(names=names), self.assertRaises(ValueError):
                export_names({**self.manifest, "assemblies": names})

    def test_unsafe_or_duplicate_part_names_are_rejected(self):
        for names in (("../old",), ("body_1", "body_1"), ()):
            with self.subTest(names=names), self.assertRaises(ValueError):
                export_names({**self.manifest, "parts": [{"name": name} for name in names]})

    def test_source_path_cannot_escape_package_root(self):
        with tempfile.TemporaryDirectory() as directory, self.assertRaises(ValueError):
            package_files(directory, ["../outside.py"], self.manifest)


if __name__ == "__main__":
    unittest.main()
