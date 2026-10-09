"""Inspection metadata regression tests; no CadQuery installation is needed."""
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from dataclasses import dataclass
from pathlib import Path
from unittest.mock import patch

import inspect_data as data


@dataclass
class FakeItem:
    name: str
    group: str = "hardware"
    source: str = "design"


class InspectDataTests(unittest.TestCase):
    def test_purchased_rows_keep_confirmed_dimensions_and_unknowns(self):
        pca = data.describe(FakeItem("pca9685"))
        self.assertIn("25 × 70 × 15 – údaj majitele", " ".join(pca["details"]))
        self.assertIn("Druhá deska je rezerva", " ".join(pca["details"]))
        self.assertIn("Zakoupeno: 2", pca["details"])
        for name, expected in (("microphone", "Výšku majitel pro rozvržení nepožaduje"),
                               ("amplifier_left", "Výšku majitel pro rozvržení nepožaduje"),
                               ("perfboard_1", "Tloušťka a montážní otvory neuvedené"),
                               ("camera", "Rozměry skutečně objednané varianty nejsou doložené"),
                               ("degson_1", "Vnější rozměry neuvedené")):
            with self.subTest(name=name):
                self.assertIn(expected, " ".join(data.describe(FakeItem(name))["details"]))
        source = "18 x 18, provisional height 8"
        self.assertIn(source, " ".join(data.describe(FakeItem("amplifier_left", source=source))["details"]))

    def test_all_documented_part_prefixes_have_human_titles(self):
        for prefix in list(data._MECHANICAL) + list(data._HARDWARE):
            name = prefix + "1" if prefix.endswith("_") else prefix
            with self.subTest(name=name):
                description = data.describe(FakeItem(name))
                self.assertNotEqual(name, description["title"])
                self.assertEqual({"title", "description", "details", "source"}, set(description))

    def test_camera_reservations_and_holder_are_mechanical_descriptions(self):
        for name in ("camera_angle", "camera_fpc"):
            self.assertEqual(data._MECHANICAL[name][0], data.describe(FakeItem(name))["title"])
        self.assertEqual(data._HARDWARE["camera"][0], data.describe(FakeItem("camera"))["title"])
        self.assertEqual(data._MECHANICAL["lid_lining"][0], data.describe(FakeItem("lid_lining", "fabric"))["title"])

    def test_endpoints_must_exist(self):
        names = {"esp32", "pca9685", "servo_1"}
        edges = data.connection_data(names)
        self.assertTrue(edges)
        for edge in edges:
            self.assertIn(edge["from"], names)
            self.assertIn(edge["to"], names)
            self.assertIn(edge["kind"], ("signal", "power", "ground"))
        self.assertEqual([], data.connection_data(set()))

    def test_spares_unassigned_nets_and_mechanical_parts_are_disconnected(self):
        disconnected = {"spare_pca", "spare_red_rocker", "spare_degson_1", "spare_capacitor_1", "degson_1", "degson_2", "degson_3", "capacitor_1", "tray_1", "body_1", "power_trunk"}
        names = disconnected | {"esp32", "pca9685", "red_main"}
        for edge in data.connection_data(names):
            self.assertNotIn(edge["from"], disconnected)
            self.assertNotIn(edge["to"], disconnected)

    def test_pca_channel_order_and_separate_logic_power(self):
        names = {"esp32", "pca9685", "green_drives"}
        names |= {f"{prefix}_{index}" for prefix in ("servo", "u059") for index in range(1, 7)}
        edges = data.connection_data(names)
        for prefix, offset in (("servo", -1), ("u059", 5)):
            for index in range(1, 7):
                channel = [edge for edge in edges if edge["from"] == "pca9685" and edge["to"] == f"{prefix}_{index}" and edge["kind"] == "signal"]
                self.assertEqual(1, len(channel))
                self.assertIn(f"kanál {index + offset} →", channel[0]["label"])
        self.assertTrue(any(edge["label"] == "3V3 → VCC" for edge in edges))
        self.assertTrue(any(edge["label"] == "5 V → PCA V+" for edge in edges))
        self.assertFalse(any(edge["from"] == "pca9685" and edge["to"].startswith("u059_") and edge["kind"] == "power" for edge in edges))

    def test_speaker_negative_is_not_ground(self):
        names = {"esp32", "red_main", "amplifier_left", "amplifier_right", "speaker_left", "speaker_right"}
        edges = data.connection_data(names)
        for side in ("left", "right"):
            speaker_edges = [edge for edge in edges if f"speaker_{side}" in (edge["from"], edge["to"])]
            self.assertEqual(1, len(speaker_edges))
            self.assertEqual(f"amplifier_{side}", speaker_edges[0]["from"])
            self.assertEqual("signal", speaker_edges[0]["kind"])
            self.assertIn("SPK− není GND", speaker_edges[0]["note"])

    def test_power_chain_and_led_order_are_explicit(self):
        chain = ["powerbank", "pd_trigger", "buck", "breaker", "red_main"]
        names = set(chain) | {"pixel_shifter", "led_1", "led_2"}
        edges = data.connection_data(names)
        for start, end in zip(chain, chain[1:]):
            self.assertTrue(any(edge["from"] == start and edge["to"] == end and edge["kind"] == "power" for edge in edges))
        led_edges = [edge for edge in edges if edge["kind"] == "signal" and edge["to"].startswith("led_")]
        self.assertTrue(led_edges)
        self.assertTrue(all("konvence vizualizace" in edge["note"] for edge in led_edges))

    def test_evidence_hashes_exact_source_bytes(self):
        evidence = data.source_evidence()
        self.assertEqual(2, len(evidence))
        for row, source in zip(evidence, data._source_records()):
            self.assertEqual(source["name"], row["name"])
            self.assertEqual(hashlib.sha256(source["text"].encode("utf-8")).hexdigest(), row["sha256"])

    def test_microphone_data_flows_into_esp32(self):
        signals = [edge for edge in data.connection_data({"esp32", "microphone"})
                   if edge["kind"] == "signal"]
        self.assertEqual(2, len(signals))
        clocks = next(edge for edge in signals if edge["from"] == "esp32")
        samples = next(edge for edge in signals if edge["from"] == "microphone")
        self.assertIn("GPIO21", clocks["label"])
        self.assertIn("GPIO41", clocks["label"])
        self.assertEqual("esp32", samples["to"])
        self.assertEqual("SD → GPIO42", samples["label"])


class SourceSnapshotTests(unittest.TestCase):
    def setUp(self):
        texts = [row["text"].encode("utf-8") for row in data._source_records()]
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        kufr = Path(temporary.name) / "kufr"
        dimensions = kufr / "mechanika/rozmery-nakoupenych-soucastek.md"
        wiring = kufr / "docs/zapojeni-potvrzeneho-hardwaru.md"
        snapshot = kufr / "mechanika/final/reference/inspect-sources.json"
        for path, content in zip((dimensions, wiring), texts):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        overrides = patch.multiple(data, KUFR=kufr, DIMENSIONS=dimensions,
                                   WIRING=wiring, SNAPSHOT=snapshot)
        overrides.start()
        self.addCleanup(overrides.stop)

    def test_extracted_package_uses_exact_snapshot_without_external_docs(self):
        expected = data.describe(FakeItem("pca9685"))
        evidence = data.source_evidence()
        original_bytes = [path.read_bytes() for path in (data.DIMENSIONS, data.WIRING)]
        data.snapshot_sources()
        stored = json.loads(data.SNAPSHOT.read_text(encoding="utf-8"))
        self.assertEqual(original_bytes, [row["text"].encode("utf-8") for row in stored["sources"]])
        for path in (data.DIMENSIONS, data.WIRING):
            path.unlink()
        before = data.SNAPSHOT.read_bytes()
        data.snapshot_sources()
        data.assert_snapshot_current()
        self.assertEqual(expected, data.describe(FakeItem("pca9685")))
        self.assertEqual(evidence, data.source_evidence())
        self.assertEqual(before, data.SNAPSHOT.read_bytes())

    def test_stale_source_is_rejected_without_rewriting_snapshot(self):
        data.snapshot_sources()
        before = data.SNAPSHOT.read_bytes()
        data.DIMENSIONS.write_bytes(data.DIMENSIONS.read_bytes() + "\nNový podklad.\n".encode("utf-8"))
        with self.assertRaisesRegex(ValueError, "Stale inspection source snapshot"):
            data.assert_snapshot_current()
        self.assertEqual(before, data.SNAPSHOT.read_bytes())
        data.snapshot_sources()
        data.assert_snapshot_current()
        self.assertNotEqual(before, data.SNAPSHOT.read_bytes())

    def test_dimension_rows_follow_changed_authoritative_contents(self):
        initial = data.describe(FakeItem("pca9685"))
        content = data.DIMENSIONS.read_bytes().replace("25 × 70 × 15".encode("utf-8"),
                                                     "25 × 70 × 16".encode("utf-8"))
        data.DIMENSIONS.write_bytes(content)
        current = data.describe(FakeItem("pca9685"))
        self.assertNotEqual(initial, current)
        self.assertIn("25 × 70 × 16", " ".join(current["details"]))

    def test_missing_or_corrupt_snapshot_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Invalid or missing"):
            data.assert_snapshot_current()
        data.snapshot_sources()
        payload = json.loads(data.SNAPSHOT.read_text(encoding="utf-8"))
        payload["sources"][0]["text"] += "tampered"
        data.SNAPSHOT.write_text(json.dumps(payload), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            data.assert_snapshot_current()
        data.DIMENSIONS.unlink()
        data.WIRING.unlink()
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            data.source_evidence()
        with self.assertRaisesRegex(ValueError, "digest mismatch"):
            data.snapshot_sources()

    def test_snapshot_preserves_utf8_and_line_endings(self):
        content = "První řádek\r\nDruhý řádek\r\n".encode("utf-8")
        data.WIRING.write_bytes(content)
        data.snapshot_sources()
        data.WIRING.unlink()
        record = data._source_records()[1]
        self.assertEqual(content, record["text"].encode("utf-8"))
        self.assertEqual(hashlib.sha256(content).hexdigest(), record["sha256"])

    def test_isolated_package_runs_metadata_suite_without_external_docs(self):
        data.snapshot_sources()
        # The child suite includes this test. It only skips this nested subprocess
        # when already inside an extracted package, where both source docs lack files.
        # Here the fixture docs exist even when the enclosing suite was extracted,
        # so determine the child boundary from the copied module's own location.
        module_root = Path(data.__file__).resolve().parent
        original_kufr = module_root.parents[1]
        if not (original_kufr / "mechanika/rozmery-nakoupenych-soucastek.md").is_file():
            return
        with tempfile.TemporaryDirectory() as directory:
            final = Path(directory) / "kufr/mechanika/final"
            reference = final / "reference"
            reference.mkdir(parents=True)
            shutil.copyfile(Path(data.__file__), final / "inspect_data.py")
            shutil.copyfile(Path(__file__), final / "test_inspect_data.py")
            shutil.copyfile(data.SNAPSHOT, reference / "inspect-sources.json")
            completed = subprocess.run([sys.executable, "-m", "unittest", "-v", "test_inspect_data"],
                                       cwd=final, text=True, capture_output=True, timeout=60)
            self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
            self.assertIn("OK", completed.stderr)


if __name__ == "__main__":
    unittest.main()
