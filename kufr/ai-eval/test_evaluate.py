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
    estimate_usage_micro_usd,
)


class EvalTests(unittest.TestCase):
    def test_each_case_has_bounded_reservation(self):
        cases = json.loads(__import__("pathlib").Path(__file__).with_name("cases.json").read_text(encoding="utf-8"))
        self.assertEqual(62, len(cases))
        self.assertEqual(len(cases), len({case["id"] for case in cases}))
        for case in cases:
            loaded = load_case(case["id"])
            for model in MODEL_PRICES:
                request = build_request(loaded, model)
                self.assertFalse(request["store"])
                self.assertEqual("default", request["service_tier"])
                self.assertLess(upper_bound_micro_usd(request, model), 1_000_000)

    def test_usage_estimate_accounts_for_cache_and_returned_tier(self):
        response = {"model": "gpt-6.1-sol", "service_tier": "fast", "usage": {
            "input_tokens": 100, "output_tokens": 10,
            "input_tokens_details": {"cached_tokens": 60, "cache_write_tokens": 30}}}
        self.assertEqual(700, estimate_usage_micro_usd(response, "gpt-6.1-sol"))
        self.assertEqual(402, estimate_usage_micro_usd(response, "gpt-6.1-sol", include_cache=True))
        for tier in (None, "unknown"):
            response["service_tier"] = tier
            with self.assertRaisesRegex(ValueError, "tier"):
                estimate_usage_micro_usd(response, "gpt-6.1-sol")

    def test_shared_voice_persona_precedes_decision_tools(self):
        from pathlib import Path
        persona = Path(__file__).with_name("voice-persona.txt").read_text(encoding="utf-8").strip()
        request = build_request({"readyMask": 63, "visitorText": "Chci hůlku."}, "gpt-6.1-sol")
        instructions = request["instructions"]
        self.assertTrue(instructions.startswith(persona + "\n\n"))
        self.assertIn("Backchannel policy:", instructions)
        self.assertNotIn("propose_wand", persona)
        self.assertIn("propose_wand", instructions[len(persona):])

    def test_catalog_has_six_fixed_compartments_and_filters_unavailable_wands(self):
        self.assertEqual("ollivander-private-party-v1", CATALOG["catalogVersion"])
        self.assertEqual(list(range(1, 7)),
                         [wand["compartment"] for wand in CATALOG["wands"]])
        request = build_request({"readyMask": 16, "visitorText": "Chci hůlku."},
                                "gpt-6.1-sol")
        self.assertIn("Kaštanová hůlka Kocovina věštců", request["input"])
        self.assertNotIn("Jasanová hůlka Poslední slovo", request["input"])
        self.assertIn("Ollivanderova obchodu", request["instructions"])
        self.assertIn("Nepředstavuj se znovu", request["instructions"])

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

    def test_suitcase_opens_without_a_visitor_message(self):
        requests = build_dialogue_requests(load_dialogue_case("taxi-petr"), "gpt-6.1-sol")
        self.assertIn("opening", requests[0]["input"])
        self.assertNotIn("Text návštěvníka:", requests[0]["input"])
        decisions = requests[0]["text"]["format"]["schema"]["properties"]["decision"]["enum"]
        self.assertNotIn("propose", decisions)
        self.assertIn("Petr.", requests[2]["input"])

    def test_returning_person_has_prior_evening_evidence(self):
        requests = build_dialogue_requests(load_dialogue_case("returning-petr"), "gpt-6.1-sol")
        self.assertIn("taxík k sousedům", requests[1]["input"])
        self.assertIn("earlier-petr", requests[1]["input"])

    def test_offline_multiturn_rejects_early_proposal(self):
        case = load_dialogue_case("taxi-petr")
        case["turns"][0]["mockResult"] = {
            "reply": "Hůlka jedna.", "decision": "propose",
            "compartment": 1, "reason": "Brzy",
        }
        with self.assertRaisesRegex(ValueError, "before four completed questions"):
            build_dialogue_requests(case, "gpt-6.1-sol")

    def test_boundary_dialogue_can_take_an_extra_turn(self):
        requests = build_dialogue_requests(load_dialogue_case("boundary-lenka"), "gpt-6.1-sol")
        self.assertEqual(5, len(requests))
        self.assertIn("Ex fakt neřeš", requests[3]["input"])

    def test_offline_empty_inventory_has_no_proposal(self):
        requests = build_dialogue_requests(load_dialogue_case("no-inventory"), "gpt-6.1-sol")
        self.assertEqual(1, len(requests))

    def test_memory_has_a_hard_context_bound(self):
        with self.assertRaisesRegex(ValueError, "Evening context"):
            build_request({"readyMask": 63, "visitorText": "Petr"}, "gpt-6.1-sol",
                          evening_context="x" * 16001)


if __name__ == "__main__":
    unittest.main()
