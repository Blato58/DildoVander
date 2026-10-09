"""Bounded synthetic conversations with an adaptive visitor; no device access."""

import argparse
import json
import os
import time
from pathlib import Path

from evaluate import MODEL_PRICES, MINIMUM_QUESTIONS, build_request, call_openai, parse_response, upper_bound_micro_usd, estimate_usage_micro_usd

VISITOR_MODEL = "gpt-6.1-sol"
PERSONAS = [
    {
        "id": "petr-taxi",
        "facts": "Jsi Petr, 31, na party s kamarády. Řekl jsi doma, že jdeš na jedno. "
                 "Minule jsi objednal taxík k sousedům a čekal před vlastním domem. "
                 "Děláš si ze sebe srandu. Když se ptá na jméno, odpověz jen Petr. "
                 "Na jinou otázku odpověz konkrétně podle těchto faktů, klidně hovorově a nedokonale.",
        "memory": "",
    },
    {
        "id": "returning-petr",
        "facts": "Jsi Petr, vracíš se ke kufru podruhé. Dřív jsi mu řekl o taxíku k sousedům. "
                 "Na jméno odpověz: Petr, už jsem tu byl. Na ověření taxíku řekni ano, "
                 "teď už jdeš pěšky. Odpovídej přímo na otázku, neodříkávej všechny fakty najednou.",
        "memory": json.dumps([{
            "ConversationId": "earlier-petr",
            "VisitorText": "Jsem Petr. Objednal jsem taxík k sousedům a čekal u sebe jak idiot.",
            "Reply": "Taxikář našel sousedy. Tebe zatím hledáme.",
        }], ensure_ascii=False),
    },
    {
        "id": "lenka-boundary",
        "facts": "Jsi Lenka, 29, máš ráda ostrý humor. Na jméno odpověz jen Lenka. "
                 "Na PRVNÍ otázku po jménu odmítni téma: Tohle neřeš, radši se mě zeptej na tanec. "
                 "Na DALŠÍ otázku odpověz přímo na to, na co se ptá. Umíš říct například, "
                 "že na parketu machruješ, i když vypadáš jak rozbitej věšák, nebo že sis "
                 "myslela, že tančíš dobře, dokud jsi neviděla video. Vyber jen relevantní fakt. "
                 "Mluv krátce jako kamarádka, neodříkávej nesouvisející historku.",
        "memory": "",
    },
]


class Meter:
    def __init__(self, api_key, send=call_openai):
        self.api_key = api_key
        self.estimated_micro_usd = 0
        self.session_estimated_micro_usd = 0
        self.send = send
        self.calls = []

    def invoke(self, request, model, purpose, validate=None):
        reservation = upper_bound_micro_usd(request, model)
        # Track a conservative estimate on ambiguous failures; never retry a POST.
        self.estimated_micro_usd += reservation
        self.session_estimated_micro_usd += reservation
        started = time.perf_counter()
        response = self.send(request, self.api_key)
        usage = response.get("usage", {})
        inputs, outputs = usage.get("input_tokens"), usage.get("output_tokens")
        estimated = estimate_usage_micro_usd(response, model)
        cache_estimate = estimate_usage_micro_usd(response, model, include_cache=True)
        expected_tier = response.get("service_tier") == "default"
        self.estimated_micro_usd += estimated - reservation
        self.session_estimated_micro_usd += estimated - reservation
        if validate is not None:
            validate(response)
        self.calls.append({"model": model, "purpose": purpose,
                           "latencyMs": round((time.perf_counter() - started) * 1000),
                           "inputTokens": inputs, "outputTokens": outputs,
                           "responseId": response.get("id"), "serviceTier": response.get("service_tier"),
                           "providerUsage": usage, "billedCostMicroUsd": None,
                           "cacheAdjustedEstimateMicroUsd": cache_estimate,
                           "upperCostMicroUsd": estimated})
        if not expected_tier:
            raise RuntimeError("Provider returned an unexpected service tier; retained estimate")
        return response


def visitor_request(persona, dialogue):
    return {
        "model": VISITOR_MODEL, "store": False, "service_tier": "default",
        "reasoning": {"effort": "low"}, "max_output_tokens": 600,
        "instructions": "Jsi syntetický testovací návštěvník, ne kufr. " + persona["facts"]
                        + " Odpověz jen na poslední repliku kufru, nejvýše dvěma větami. "
                        + "Nevymýšlej nové životní historky, nesnaž se test dokončit ani hodnotit.",
        "input": json.dumps(dialogue, ensure_ascii=False),
        "text": {"format": {"type": "json_schema", "name": "visitor_reply", "strict": True,
                            "schema": {"type": "object", "additionalProperties": False,
                                       "properties": {"reply": {"type": "string"}}, "required": ["reply"]}}},
    }


def read_visitor(response):
    if response.get("status") != "completed":
        raise ValueError("Visitor simulation is incomplete")
    texts = [part["text"] for item in response.get("output", []) if item.get("type") == "message"
             for part in item.get("content", []) if part.get("type") == "output_text"]
    if len(texts) != 1:
        raise ValueError("Visitor simulation has no unique reply")
    reply = json.loads(texts[0])["reply"]
    if not isinstance(reply, str) or not 1 <= len(reply) <= 500:
        raise ValueError("Visitor reply exceeds bounds")
    return reply


def run_dialogue(meter, model, persona):
    meter.session_estimated_micro_usd = 0
    history, dialogue, completed = [], [], []
    visitor = ""
    completed_questions = 0
    for index in range(8):
        memory_context = persona["memory"]
        if completed:
            memory_context += json.dumps(completed, ensure_ascii=False)
        request = build_request({"readyMask": 63, "visitorText": visitor}, model,
                                history, memory_context, opening=index == 0,
                                require_question=completed_questions < MINIMUM_QUESTIONS)
        response = meter.invoke(request, model, f"{persona['id']}:suitcase",
                                validate=lambda response: parse_response(response, 63))
        result, _ = parse_response(response, 63)
        if result["decision"] == "propose" and completed_questions < MINIMUM_QUESTIONS:
            raise ValueError("Premature proposal")
        dialogue.append({"speaker": "kufr", **result})
        completed.append({"ConversationId": persona["id"] + "-current",
                          "VisitorText": visitor, "Reply": result["reply"]})
        history = (history + [{"VisitorText": visitor, "Reply": result["reply"][:300]}])[-2:]
        if result["decision"] != "ask":
            return {"model": model, "persona": persona["id"], "dialogue": dialogue,
                    "terminated": True, "upperCostMicroUsd": meter.session_estimated_micro_usd}
        completed_questions += 1
        if index < 7:
            visitor = read_visitor(meter.invoke(visitor_request(persona, dialogue), VISITOR_MODEL,
                                               f"{persona['id']}:visitor", validate=read_visitor))
            dialogue.append({"speaker": "visitor", "reply": visitor})
    return {"model": model, "persona": persona["id"], "dialogue": dialogue,
            "terminated": False, "upperCostMicroUsd": meter.session_estimated_micro_usd}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", nargs="+", choices=MODEL_PRICES, default=["gpt-6.1-sol"])
    parser.add_argument(
        "--budget-micro-usd",
        metavar="MICRO_USD",
        help="Deprecated and ignored; account-level OpenAI budget controls spend.",
    )
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        raise ValueError("OPENAI_API_KEY is missing")
    meter = Meter(key)
    report = {"synthetic": True, "requiresHumanStyleReview": True, "actuatorCommandIssued": False,
              "dialogues": [], "completed": False,
              "costBasis": "List-price estimate at returned service tier; all input at cache-write rate; not billing",
              "pricingSource": "https://developers.openai.com/api/docs/pricing",
              "billedCostMicroUsd": None, "billingVerified": False}
    try:
        for model in args.models:
            for persona in PERSONAS:
                report["dialogues"].append(run_dialogue(meter, model, persona))
        report["completed"] = True
    finally:
        report["upperCostMicroUsd"] = meter.estimated_micro_usd
        report["calls"] = meter.calls
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
