import copy
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from dv01_replay import (HERE, capture_current_reference, read_current_reference, text_sha256,
                         validate_inputs, validate_skills, run_offline)


class CharacterReplayTests(unittest.TestCase):
    def test_complete_ids_actual_references_and_preserved_historical_baseline(self):
        corpus, _, _ = validate_inputs()
        self.assertEqual(len(corpus["scenarios"]), 80)
        self.assertEqual(len(corpus["requirements"]), 34)

    def test_current_reference_cannot_override_another_baseline_file(self):
        baseline = json.loads((HERE / "dv01-baseline.json").read_text(encoding="utf-8"))
        reference = json.loads((HERE / "dv01-current-reference.json").read_text(encoding="utf-8"))
        reference["files"]["ai-eval/wand-catalog.json"] = {"sha256": "0" * 64}
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            (temporary / "dv01-current-reference.json").write_text(json.dumps(reference), encoding="utf-8")
            with patch("dv01_replay.HERE", temporary), self.assertRaisesRegex(ValueError, "Invalid DV01 current"):
                read_current_reference(baseline)

    def test_changed_current_prompt_pin_is_rejected(self):
        baseline = json.loads((HERE / "dv01-baseline.json").read_text(encoding="utf-8"))
        reference = copy.deepcopy(read_current_reference(baseline))
        reference["files"]["ai-eval/voice-persona.txt"]["sha256"] = "0" * 64
        with patch("dv01_replay.read_current_reference", return_value=reference), \
             self.assertRaisesRegex(ValueError, "Changed DV01 current source reference"):
            validate_inputs()

    def test_explicit_capture_cannot_repin_changed_catalog_or_modify_history(self):
        history = (HERE / "dv01-baseline.json").read_bytes()
        current = (HERE / "dv01-current-reference.json").read_bytes()
        original_read = Path.read_bytes
        def altered_catalog(path):
            return b"changed catalog fixture" if path.name == "wand-catalog.json" else original_read(path)
        with patch.object(Path, "read_bytes", altered_catalog), \
             self.assertRaisesRegex(ValueError, "Changed DV01 baseline"):
            capture_current_reference("A deliberately rejected fixture capture")
        self.assertEqual((HERE / "dv01-baseline.json").read_bytes(), history)
        self.assertEqual((HERE / "dv01-current-reference.json").read_bytes(), current)

    def test_current_text_hash_matches_lf_and_crlf_checkouts(self):
        with tempfile.TemporaryDirectory() as directory:
            lf, crlf = Path(directory) / "lf.txt", Path(directory) / "crlf.txt"
            lf.write_bytes(b"authored persona\nsecond line\n")
            crlf.write_bytes(b"authored persona\r\nsecond line\r\n")
            self.assertEqual(text_sha256(lf), text_sha256(crlf))

    def test_skill_metadata_and_references(self):
        self.assertTrue(validate_skills())

    def test_dry_run_never_spawns_provider_or_claims_behavior(self):
        with tempfile.TemporaryDirectory() as directory, patch("subprocess.run") as run:
            report = run_offline(Path(directory) / "check.json", check_only=True)
            run.assert_not_called()
            self.assertEqual(report["providerCalls"], 0)
            self.assertFalse(report["offline_verified"])
            self.assertFalse(report["event_ready"])
            self.assertEqual(report["currentReferenceId"], "original-personas-v1")
            self.assertEqual(report["currentReferenceSha256"], text_sha256(HERE / "dv01-current-reference.json"))
            self.assertFalse(report["currentProviderBehaviorVerified"])

    def test_offline_main_bypasses_provider_even_when_key_present(self):
        from evaluate_party import main
        with tempfile.TemporaryDirectory() as directory, patch.dict("os.environ", {"OPENAI_API_KEY": "synthetic-never-used"}), \
             patch("sys.argv", ["evaluate_party.py", "--dv01-check", "--output", str(Path(directory) / "check.json")]), \
             patch("evaluate_party.call_openai") as provider:
            main()
            provider.assert_not_called()


if __name__ == "__main__":
    unittest.main()
