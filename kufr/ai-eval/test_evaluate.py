import json
import unittest

from evaluate import (
    MODEL_PRICES,
    build_request,
    load_case,
    parse_response,
    upper_bound_micro_usd,
    validate_result,
)


class EvalTests(unittest.TestCase):
    def test_each_case_is_bounded_and_under_session_cap(self):
        cases = json.loads(__import__("pathlib").Path(__file__).with_name("cases.json").read_text(encoding="utf-8"))
        self.assertEqual(62, len(cases))
        self.assertEqual(len(cases), len({case["id"] for case in cases}))
        for case in cases:
            loaded = load_case(case["id"])
            for model in MODEL_PRICES:
                request = build_request(loaded, model)
                self.assertFalse(request["store"])
                self.assertLess(upper_bound_micro_usd(request, model), 500_000)

    def test_unavailable_proposal_is_rejected(self):
        result = {
            "reply": "Navrhuji hůlku šest.",
            "decision": "propose",
            "compartment": 6,
            "reason": "Hravá volba",
        }
        with self.assertRaisesRegex(ValueError, "unavailable"):
            validate_result(result, 1)

    def test_non_proposal_must_have_null_compartment(self):
        result = {
            "reply": "Mohu se ještě zeptat?",
            "decision": "ask",
            "compartment": 1,
            "reason": "Málo údajů",
        }
        with self.assertRaisesRegex(ValueError, "Non-proposal"):
            validate_result(result, 63)

    def test_incomplete_response_is_rejected(self):
        response = {
            "status": "incomplete",
            "output": [],
            "usage": {"input_tokens": 100, "output_tokens": 1200},
        }
        with self.assertRaisesRegex(ValueError, "not completed"):
            parse_response(response, 63)

    def test_valid_structured_response(self):
        result = {
            "reply": "Co by se ti líbilo?",
            "decision": "ask",
            "compartment": None,
            "reason": "Potřebuji upřesnění",
        }
        response = {
            "status": "completed",
            "output": [{
                "type": "message",
                "content": [{"type": "output_text", "text": json.dumps(result)}],
            }],
            "usage": {"input_tokens": 100, "output_tokens": 20},
        }
        self.assertEqual(result, parse_response(response, 63)[0])


if __name__ == "__main__":
    unittest.main()
