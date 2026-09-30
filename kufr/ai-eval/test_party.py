import unittest

from evaluate import build_request
from evaluate_party import Meter, read_visitor


class PartyTests(unittest.TestCase):
    def test_rejected_reservation_never_calls_provider(self):
        calls = []
        meter = Meter("fake", limit=1, send=lambda *args: calls.append(args))
        request = build_request({"readyMask": 63, "visitorText": ""}, "gpt-6.1-sol", opening=True)
        with self.assertRaisesRegex(RuntimeError, "cap"):
            meter.invoke(request, "gpt-6.1-sol", "test")
        self.assertEqual([], calls)

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

    def test_tier_mismatch_retains_reservation_and_stops_run(self):
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        meter = Meter("fake", send=lambda *_: {"model": "gpt-6.1-sol", "service_tier": "fast",
                                              "usage": {"input_tokens": 100, "output_tokens": 10}})
        with self.assertRaisesRegex(RuntimeError, "unexpected service tier"):
            meter.invoke(request, "gpt-6.1-sol", "test")
        self.assertGreater(meter.estimated_micro_usd, 700)
        self.assertEqual(700, meter.calls[0]["upperCostMicroUsd"])

    def test_malformed_output_or_cache_retains_full_reservation(self):
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        for details in ({}, {"cached_tokens": 101, "cache_write_tokens": 0}):
            meter = Meter("fake", send=lambda *_: {
                "model": "gpt-6.1-sol", "service_tier": "default", "status": "incomplete",
                "usage": {"input_tokens": 100, "output_tokens": 10, "input_tokens_details": details}})
            with self.assertRaises(ValueError):
                meter.invoke(request, "gpt-6.1-sol", "test", validate=read_visitor)
            self.assertGreater(meter.estimated_micro_usd, 350)
            self.assertEqual([], meter.calls)

    def test_incomplete_visitor_is_not_a_real_reply(self):
        with self.assertRaisesRegex(ValueError, "incomplete"):
            read_visitor({"status": "incomplete"})


if __name__ == "__main__":
    unittest.main()
