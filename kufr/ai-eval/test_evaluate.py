import json
import unittest

from evaluate import (
    CATALOG,
    MODEL_PRICES,
    build_dialogue_requests,
    build_request,
    load_case,
    load_dialogue_case,
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

    def test_catalog_has_six_fixed_compartments_and_filters_unavailable_wands(self):
        self.assertEqual("ollivander-private-party-v1", CATALOG["catalogVersion"])
        self.assertEqual(list(range(1, 7)),
                         [wand["compartment"] for wand in CATALOG["wands"]])
        request = build_request({"readyMask": 16, "visitorText": "Chci hůlku."},
                                "gpt-6.1-sol")
        self.assertIn("Kaštanová hůlka Kocovina věštců", request["input"])
        self.assertNotIn("Jasanová hůlka Poslední slovo", request["input"])
        self.assertIn("Ollivanderova obchodu", request["instructions"])
        self.assertIn("V první odpovědi", request["instructions"])

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

    def test_empty_inventory_requires_no_selection(self):
        result = {
            "reply": "Nevím, co nabídnout.",
            "decision": "ask",
            "compartment": None,
            "reason": "Žádná hůlka",
        }
        with self.assertRaisesRegex(ValueError, "Empty inventory"):
            validate_result(result, 0)

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

    def test_offline_multiturn_fixture_carries_two_answers_into_proposal(self):
        case = load_dialogue_case("sparkle-chaos")
        requests = build_dialogue_requests(case, "gpt-6.1-sol")
        self.assertEqual(3, len(requests))
        self.assertIn("Na parketu mi spadla koruna.", requests[2]["input"])
        self.assertIn("Jaký trapas má hůlka zachránit?", requests[2]["input"])
        self.assertIn("koruna", case["turns"][2]["mockResult"]["reply"].lower())
        self.assertIn("třpytivý chaos", case["turns"][2]["mockResult"]["reply"].lower())
        self.assertTrue(all(not request["store"] for request in requests))

    def test_offline_multiturn_rejects_early_proposal(self):
        case = load_dialogue_case("sparkle-chaos")
        case["turns"][0]["mockResult"] = {
            "reply": "Hůlka jedna.",
            "decision": "propose",
            "compartment": 1,
            "reason": "Brzy",
        }
        with self.assertRaisesRegex(ValueError, "before two completed questions"):
            build_dialogue_requests(case, "gpt-6.1-sol")

    def test_offline_empty_inventory_has_no_proposal(self):
        requests = build_dialogue_requests(load_dialogue_case("no-inventory"), "gpt-6.1-sol")
        self.assertEqual(1, len(requests))

    def test_offline_political_roast_uses_stated_position(self):
        case = load_dialogue_case("political-roast")
        requests = build_dialogue_requests(case, "gpt-6.1-sol")
        self.assertIn("zrušit pondělky", requests[2]["input"])
        self.assertIn("třpytem", case["turns"][2]["mockResult"]["reply"])


if __name__ == "__main__":
    unittest.main()
