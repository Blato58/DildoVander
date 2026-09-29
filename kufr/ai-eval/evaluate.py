"""One synthetic, device-free dialogue evaluation against the Responses API."""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from decimal import Decimal
from pathlib import Path

CASES = Path(__file__).with_name("cases.json")
MODEL_PRICES = {
    # USD per million tokens; input uses the higher cache-write rate.
    "gpt-6.1-sol": (Decimal("2.50"), Decimal("10")),
    "gpt-6-astra": (Decimal("12.50"), Decimal("50")),
    "gpt-6-luna": (Decimal("0.125"), Decimal("0.50")),
}
MAX_OUTPUT_TOKENS = 1200
MAX_SESSION_MICRO_USD = 500_000

RESULT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "reply": {"type": "string"},
        "decision": {"type": "string", "enum": ["ask", "propose", "no_selection"]},
        "compartment": {
            "type": ["integer", "null"],
            "enum": [1, 2, 3, 4, 5, 6, None],
        },
        "reason": {"type": "string"},
    },
    "required": ["reply", "decision", "compartment", "reason"],
}

INSTRUCTIONS = Path(__file__).with_name("prompt.txt").read_text(encoding="utf-8").strip()


def load_case(case_id):
    cases = json.loads(CASES.read_text(encoding="utf-8"))
    matches = [item for item in cases if item["id"] == case_id]
    if len(matches) != 1:
        raise ValueError(f"Unknown case: {case_id}")
    case = matches[0]
    mask = case["readyMask"]
    if type(mask) is not int or not 0 <= mask <= 63:
        raise ValueError("Invalid readyMask")
    if not isinstance(case["visitorText"], str) or not 1 <= len(case["visitorText"]) <= 1000:
        raise ValueError("Invalid visitorText")
    return case


def build_request(case, model):
    mask = case["readyMask"]
    available = [number for number in range(1, 7) if mask & (1 << (number - 1))]
    prompt = (
        f"Dostupné přihrádky: {available}. Nejsou-li dostupné žádné, "
        "vrať no_selection. Katalog: neutrální Hůlka 1 až Hůlka 6. "
        f"Text návštěvníka: {case['visitorText']}"
    )
    return {
        "model": model,
        "store": False,
        "reasoning": {"effort": "low"},
        "max_output_tokens": MAX_OUTPUT_TOKENS,
        "instructions": INSTRUCTIONS,
        "input": prompt,
        "text": {
            "format": {
                "type": "json_schema",
                "name": "wand_decision",
                "strict": True,
                "schema": RESULT_SCHEMA,
            }
        },
    }


def upper_bound_micro_usd(request, model):
    encoded = json.dumps(request, ensure_ascii=False).encode("utf-8")
    # Deliberately overestimate prompt/schema tokenization and count all input
    # at cache-write pricing. The Platform project hard limit is the backstop.
    input_token_bound = len(encoded) * 2 + 4096
    input_price, output_price = MODEL_PRICES[model]
    amount = input_token_bound * input_price + MAX_OUTPUT_TOKENS * output_price
    return int(amount.to_integral_value(rounding="ROUND_CEILING"))


def validate_result(result, ready_mask):
    if not isinstance(result, dict) or set(result) != set(RESULT_SCHEMA["required"]):
        raise ValueError("Unexpected result fields")
    reply = result["reply"]
    reason = result["reason"]
    decision = result["decision"]
    compartment = result["compartment"]
    if not isinstance(reply, str) or not 1 <= len(reply) <= 500:
        raise ValueError("Invalid reply length")
    if not isinstance(reason, str) or len(reason) > 200:
        raise ValueError("Invalid reason length")
    if decision not in ("ask", "propose", "no_selection"):
        raise ValueError("Invalid decision")
    if decision == "propose":
        if type(compartment) is not int or compartment not in range(1, 7):
            raise ValueError("Invalid proposed compartment")
        if ready_mask & (1 << (compartment - 1)) == 0:
            raise ValueError("Proposed compartment is unavailable")
    elif compartment is not None:
        raise ValueError("Non-proposal has a compartment")
    if ready_mask == 0 and decision != "no_selection":
        raise ValueError("Empty inventory requires no_selection")
    return result


def parse_response(response, ready_mask):
    if response.get("status") != "completed":
        raise ValueError("Response not completed")
    output = response.get("output", [])
    texts = [
        content["text"]
        for item in output
        if item.get("type") == "message"
        for content in item.get("content", [])
        if content.get("type") == "output_text"
    ]
    if len(texts) != 1:
        raise ValueError("Missing or ambiguous structured output")
    result = validate_result(json.loads(texts[0]), ready_mask)
    usage = response.get("usage")
    if not isinstance(usage, dict):
        raise ValueError("Missing token usage")
    return result, usage


def call_openai(request, api_key):
    payload = json.dumps(request, ensure_ascii=False).encode("utf-8")
    http_request = urllib.request.Request(
        "https://api.openai.com/v1/responses",
        data=payload,
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(http_request, timeout=45) as response:
            body = response.read(65_537)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"OpenAI HTTP {error.code}") from None
    if len(body) > 65_536:
        raise RuntimeError("OpenAI response exceeds local size limit")
    return json.loads(body)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--case", required=True)
    parser.add_argument("--model", choices=MODEL_PRICES, default="gpt-6.1-sol")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    case = load_case(args.case)
    request = build_request(case, args.model)
    maximum = upper_bound_micro_usd(request, args.model)
    configured_cap = int(os.getenv("SESSION_CAP_MICRO_USD", MAX_SESSION_MICRO_USD))
    if configured_cap <= 0 or maximum > min(configured_cap, MAX_SESSION_MICRO_USD):
        raise ValueError("Conservative one-call estimate exceeds $0.50 session cap")
    api_key = os.getenv("OPENAI_API_KEY")
    if not api_key:
        raise ValueError("OPENAI_API_KEY is missing")
    started = time.perf_counter()
    response = call_openai(request, api_key)
    latency_ms = round((time.perf_counter() - started) * 1000)
    result, usage = parse_response(response, case["readyMask"])
    input_price, output_price = MODEL_PRICES[args.model]
    input_tokens = usage.get("input_tokens")
    output_tokens = usage.get("output_tokens")
    if type(input_tokens) is not int or type(output_tokens) is not int:
        raise ValueError("Invalid token usage")
    estimated_micro_usd = int(
        (input_tokens * input_price + output_tokens * output_price)
        .to_integral_value(rounding="ROUND_CEILING")
    )
    report = {
        "case": case["id"],
        "requestedModel": args.model,
        "returnedModel": response.get("model"),
        "readyMask": case["readyMask"],
        "latencyMs": latency_ms,
        "result": result,
        "usage": {"inputTokens": input_tokens, "outputTokens": output_tokens},
        "estimatedUpperCostMicroUsd": estimated_micro_usd,
        "requiresHumanStyleReview": True,
        "actuatorCommandIssued": False,
    }
    rendered = json.dumps(report, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"AI eval failed safely: {error}", file=sys.stderr)
        sys.exit(1)
