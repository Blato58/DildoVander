import json
import os
import sys
import tempfile
import unittest
from unittest.mock import patch

from evaluate import build_request, upper_bound_micro_usd
from evaluate_party import Meter, main, read_visitor


class PartyTests(unittest.TestCase):
    def test_opening_does_not_treat_archive_as_visitor(self):
        history = [{"VisitorText": "Old guest", "Reply": "Old reply"}]
        case = {"readyMask": 63, "visitorText": ""}
        opening = build_request(case, "gpt-6.1-sol", history, "Private archive", opening=True)
        self.assertNotIn("Private archive", opening["input"])
        self.assertNotIn("Old guest", opening["input"])
        self.assertIn("NOVÝ ROZHOVOR", opening["input"])
        follow_up = build_request({**case, "visitorText": "Jsem Petr"},
                                  "gpt-6.1-sol", history, "Private archive")
        self.assertIn("Private archive", follow_up["input"])
        self.assertIn("Old guest", follow_up["input"])

    def test_usage_above_three_dollars_does_not_block_later_calls(self):
        calls = []
        def send(request, _):
            calls.append(request)
            return {"model": request["model"], "service_tier": "default",
                    "usage": {"input_tokens": 1_300_000, "output_tokens": 10}}

        meter = Meter("fake", send=send)
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        meter.invoke(request, "gpt-6.1-sol", "first")
        meter.invoke(request, "gpt-6.1-sol", "second")
        self.assertEqual(2, len(calls))
        self.assertGreater(meter.estimated_micro_usd, 6_000_000)

    def test_legacy_budget_flag_is_accepted_and_ignored(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            output_path = os.path.join(temp_dir, "evaluation.json")
            with patch.dict(os.environ, {"OPENAI_API_KEY": "fake", "SESSION_CAP_MICRO_USD": "1"}), \
                    patch("evaluate_party.run_dialogue", return_value={"upperCostMicroUsd": 4_000_000}), \
                    patch.object(sys, "argv", ["evaluate_party.py", "--budget-micro-usd", "1",
                                                "--output", output_path]), \
                    patch("builtins.print"):
                main()

            with open(output_path, encoding="utf-8") as output_file:
                report = json.load(output_file)

        self.assertTrue(report["completed"])
        self.assertNotIn("approvedRunCapMicroUsd", report)
        self.assertEqual(4_000_000, report["dialogues"][0]["upperCostMicroUsd"])

    def test_ambiguous_network_failure_retains_reservation(self):
        def fail(*_):
            raise RuntimeError("network")
        meter = Meter("fake", send=fail)
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        with self.assertRaisesRegex(RuntimeError, "network"):
            meter.invoke(request, "gpt-6.1-sol", "test")
        self.assertGreater(meter.estimated_micro_usd, 0)
        self.assertEqual(meter.estimated_micro_usd, meter.session_estimated_micro_usd)

    def test_token_estimate_is_shared_across_dialogues_and_models(self):
        meter = Meter("fake", send=lambda request, _: {
            "id": "resp_test", "service_tier": "default",
            "model": request["model"], "usage": {"input_tokens": 100, "output_tokens": 10,
                                                   "input_tokens_details": {"cached_tokens": 64}}})
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        meter.invoke(request, "gpt-6.1-sol", "first")
        self.assertEqual(350, meter.estimated_micro_usd)
        self.assertIsNone(meter.calls[0]["billedCostMicroUsd"])
        self.assertEqual(64, meter.calls[0]["providerUsage"]["input_tokens_details"]["cached_tokens"])
        self.assertEqual("resp_test", meter.calls[0]["responseId"])
        meter.session_estimated_micro_usd = 0
        meter.invoke(request, "gpt-6.1-sol", "second")
        self.assertEqual(700, meter.estimated_micro_usd)
        self.assertEqual(350, meter.session_estimated_micro_usd)

    def test_known_tier_mismatch_is_priced_and_stops_run(self):
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        meter = Meter("fake", send=lambda *_: {"model": "gpt-6.1-sol", "service_tier": "fast",
                                              "usage": {"input_tokens": 100, "output_tokens": 10}})
        with self.assertRaisesRegex(RuntimeError, "unexpected service tier"):
            meter.invoke(request, "gpt-6.1-sol", "test")
        self.assertEqual(700, meter.estimated_micro_usd)
        self.assertEqual(700, meter.calls[0]["upperCostMicroUsd"])

    def test_invalid_output_or_cache_keeps_safe_estimate(self):
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        reservation = upper_bound_micro_usd(request, "gpt-6.1-sol")
        for details, expected_estimate in (({}, 350),
                                           ({"cached_tokens": 101, "cache_write_tokens": 0}, reservation)):
            meter = Meter("fake", send=lambda *_: {
                "model": "gpt-6.1-sol", "service_tier": "default", "status": "incomplete",
                "usage": {"input_tokens": 100, "output_tokens": 10, "input_tokens_details": details}})
            with self.assertRaises(ValueError):
                meter.invoke(request, "gpt-6.1-sol", "test", validate=read_visitor)
            self.assertEqual(expected_estimate, meter.estimated_micro_usd)
            self.assertEqual([], meter.calls)

    def test_incomplete_visitor_is_not_a_real_reply(self):
        with self.assertRaisesRegex(ValueError, "incomplete"):
            read_visitor({"status": "incomplete"})


if __name__ == "__main__":
    unittest.main()
