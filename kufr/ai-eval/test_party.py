import unittest

from evaluate import build_request
from evaluate_party import Meter, read_visitor


class PartyTests(unittest.TestCase):
    def test_rejected_reservation_never_calls_provider(self):
        calls = []
        meter = Meter("fake", limit=1, send=lambda *args: calls.append(args))
        request = build_request({"readyMask": 63, "visitorText": ""}, "gpt-6-astra", opening=True)
        with self.assertRaisesRegex(RuntimeError, "cap"):
            meter.invoke(request, "gpt-6-astra", "test")
        self.assertEqual([], calls)

    def test_ambiguous_network_failure_retains_reservation(self):
        def fail(*_):
            raise RuntimeError("network")
        meter = Meter("fake", send=fail)
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        with self.assertRaisesRegex(RuntimeError, "network"):
            meter.invoke(request, "gpt-6.1-sol", "test")
        self.assertGreater(meter.spent, 0)
        self.assertEqual(meter.spent, meter.session_spent)

    def test_actual_usage_is_shared_across_dialogues_and_models(self):
        meter = Meter("fake", send=lambda request, _: {
            "model": request["model"], "usage": {"input_tokens": 100, "output_tokens": 10}})
        request = build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol")
        meter.invoke(request, "gpt-6.1-sol", "first")
        self.assertEqual(350, meter.spent)
        meter.session_spent = 0
        meter.invoke(request, "gpt-6.1-sol", "second")
        self.assertEqual(700, meter.spent)
        self.assertEqual(350, meter.session_spent)

    def test_incomplete_visitor_is_not_a_real_reply(self):
        with self.assertRaisesRegex(ValueError, "incomplete"):
            read_visitor({"status": "incomplete"})


if __name__ == "__main__":
    unittest.main()
