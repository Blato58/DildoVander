#!/usr/bin/env python3
"""Synthetic file-only tests. No recording, provider, flash or deployment."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import wave

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_offline_bank", ROOT / "scripts/prepare_offline_bank.py")
BANK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BANK)


class OfflineBankTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads((ROOT / "offline-bank/manifest.json").read_text(encoding="utf-8"))

    def test_delivered_bank_explicitly_missing_and_catalog_complete(self):
        generated, status = BANK.generate(ROOT / "offline-bank/manifest.json", None)
        self.assertEqual(status["state"], "missing_assets")
        self.assertEqual(status["totalBytes"], 0)
        self.assertEqual(len(status["missing"]), 24)
        self.assertEqual(generated, (ROOT / "src/features/offline_show/OfflineBank.generated.h").read_text(encoding="utf-8"))

    def recordings(self, directory, pcm=b"\x00\x00" * 24):
        manifest = copy.deepcopy(self.manifest)
        for entry in manifest["clips"]:
            encoded = BANK.encode_ima(pcm)
            entry["sha256"] = hashlib.sha256(encoded).hexdigest()
            entry["sizeBytes"] = len(encoded)
            entry["sampleCount"] = len(pcm) // 2
            entry["durationMs"] = len(pcm) * 1000 // 48000
            with wave.open(str(directory / (entry["id"] + ".wav")), "wb") as wav:
                wav.setparams((1, 2, 24000, 0, "NONE", "not compressed"))
                wav.writeframes(pcm)
        path = directory / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        return manifest, path

    def test_supported_pcm_hash_and_capacity(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            manifest, path = self.recordings(directory)
            _, status = BANK.generate(path, directory)
            self.assertEqual(status["state"], "ready")
            self.assertEqual(status["totalBytes"], 16 * 24)
            manifest["clips"][0]["sha256"] = "0" * 64
            path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaises(AssertionError):
                BANK.generate(path, directory)
            manifest, path = self.recordings(directory)
            pcm = b"\x00\x00" * 192001
            entry = manifest["clips"][0]
            encoded = BANK.encode_ima(pcm)
            entry.update(sha256=hashlib.sha256(encoded).hexdigest(), sizeBytes=len(encoded), durationMs=8000, sampleCount=192001)
            with wave.open(str(directory / (entry["id"] + ".wav")), "wb") as wav:
                wav.setparams((1, 2, 24000, 0, "NONE", "not compressed"))
                wav.writeframes(pcm)
            path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaises(AssertionError):
                BANK.generate(path, directory)
            _, path = self.recordings(directory, b"\x00\x00" * 192000)
            with self.assertRaisesRegex(AssertionError, "capacity"):
                BANK.generate(path, directory)

    def test_failed_install_preserves_previous_complete_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            manifest, path = self.recordings(directory)
            output = directory / "bank.h"
            subprocess.run([sys.executable, str(ROOT / "scripts/prepare_offline_bank.py"),
                "--manifest", str(path), "--audio-dir", str(directory), "--output", str(output)],
                check=True, capture_output=True)
            original = output.read_bytes()
            manifest["clips"][0]["sha256"] = "bad"
            path.write_text(json.dumps(manifest), encoding="utf-8")
            attempt = subprocess.run([sys.executable, str(ROOT / "scripts/prepare_offline_bank.py"),
                "--manifest", str(path), "--audio-dir", str(directory), "--output", str(output)], capture_output=True)
            self.assertNotEqual(attempt.returncode, 0)
            self.assertEqual(output.read_bytes(), original)
            manifest, path = self.recordings(directory)
            (directory / (manifest["clips"][0]["id"] + ".wav")).unlink()
            attempt = subprocess.run([sys.executable, str(ROOT / "scripts/prepare_offline_bank.py"),
                "--manifest", str(path), "--audio-dir", str(directory), "--output", str(output)], capture_output=True)
            self.assertNotEqual(attempt.returncode, 0)
            self.assertEqual(output.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
