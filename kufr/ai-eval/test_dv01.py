import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from dv01_replay import validate_inputs, validate_skills, run_offline


class CharacterReplayTests(unittest.TestCase):
    def test_complete_ids_actual_references_and_preserved_baseline(self):
        corpus, _, _ = validate_inputs()
        self.assertEqual(len(corpus["scenarios"]), 80)
        self.assertEqual(len(corpus["requirements"]), 34)

    def test_skill_metadata_and_references(self):
        self.assertTrue(validate_skills())

    def test_dry_run_never_spawns_provider_or_claims_behavior(self):
        with tempfile.TemporaryDirectory() as directory, patch("subprocess.run") as run:
            report = run_offline(Path(directory) / "check.json", check_only=True)
            run.assert_not_called()
            self.assertEqual(report["providerCalls"], 0)
            self.assertFalse(report["offline_verified"])
            self.assertFalse(report["event_ready"])

    def test_offline_main_bypasses_provider_even_when_key_present(self):
        from evaluate_party import main
        with tempfile.TemporaryDirectory() as directory, patch.dict("os.environ", {"OPENAI_API_KEY": "synthetic-never-used"}), \
             patch("sys.argv", ["evaluate_party.py", "--dv01-check", "--output", str(Path(directory) / "check.json")]), \
             patch("evaluate_party.call_openai") as provider:
            main()
            provider.assert_not_called()


if __name__ == "__main__":
    unittest.main()
