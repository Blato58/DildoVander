"""Exercise the installed backend without exposing its test credential."""

import json
import ipaddress
import os
import time
import urllib.error
import urllib.request
from uuid import uuid4


BASE_URL = "http://127.0.0.1:5080"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def check(condition, label):
    if not condition:
        raise RuntimeError(label)


def call(method, path, *, bearer=None, key=None, body=None):
    headers = {}
    if bearer is not None:
        headers["Authorization"] = f"Bearer {bearer}"
    if key is not None:
        headers["Idempotency-Key"] = key
    payload = None
    if body is not None:
        headers["Content-Type"] = "application/json"
        payload = json.dumps(body).encode("utf-8")

    request = urllib.request.Request(
        BASE_URL + path,
        data=payload,
        headers=headers,
        method=method,
    )
    try:
        response = OPENER.open(request, timeout=5)
    except urllib.error.HTTPError as error:
        response = error

    with response:
        content = response.read(65_537)
        check(len(content) <= 65_536, "response_too_large")
        if path.startswith("/v1/"):
            check(response.headers.get("Cache-Control") == "no-store", "cache_policy_failed")
        result = json.loads(content) if content else None
        return response.status, result


def main():
    global BASE_URL
    transport = os.environ.get("SMOKE_TRANSPORT", "loopback")
    check(transport in {"loopback", "public"}, "invalid_smoke_transport")
    if transport == "public":
        public_ip = os.environ.get("PUBLIC_IPV4", "")
        try:
            address = ipaddress.IPv4Address(public_ip)
        except ipaddress.AddressValueError as error:
            raise RuntimeError("invalid_public_ipv4") from error
        check(address.is_global, "public_ipv4_not_global")
        BASE_URL = f"https://{address}"

    expectation = os.environ.get("DIALOG_EXPECTATION", "fake")
    check(expectation in {"fake", "responses"}, "invalid_dialog_expectation")
    catalog_version = (
        "ollivander-private-party-v1" if expectation == "responses" else "neutral-1"
    )
    device_id = os.environ.get("DEVICE_ID")
    credential = os.environ.get("DEV_DEVICE_CREDENTIAL")
    check(bool(device_id and credential), "development_identity_missing")

    status, health = call("GET", "/healthz")
    check(status == 200 and health == {"status": "ok"}, "health_failed")

    status, _ = call("POST", "/v1/device-access/token")
    check(status == 401, "unauthenticated_token_accepted")
    status, issued = call("POST", "/v1/device-access/token", bearer=credential)
    check(status == 200 and issued.get("deviceId") == device_id, "token_exchange_failed")
    token = issued["accessToken"]

    boot_id = str(uuid4())
    conversation_body = {
        "deviceId": device_id,
        "bootId": boot_id,
        "generation": 0,
        "catalogVersion": catalog_version,
        "locale": "cs-CZ",
        "consent": {"audio": False, "image": False},
    }
    create_key = str(uuid4())
    status, created = call(
        "POST", "/v1/conversations", bearer=token, key=create_key, body=conversation_body
    )
    check(status == 201 and created.get("conversationId"), "conversation_create_failed")
    conversation_id = created["conversationId"]

    # Content quality and adaptive conversation are exercised by interactive_dialogue.py.
    visitor_turns = [""] if expectation == "responses" else ["Syntetický test nasazené vývojové instance."]
    turn_path = f"/v1/conversations/{conversation_id}/{'opening' if expectation == 'responses' else 'turns'}"
    for index, visitor_text in enumerate(visitor_turns):
        turn_body = {
            "clientTurnId": str(uuid4()),
            "bootId": boot_id,
            "generation": 0,
            "text": visitor_text,
            "readyMask": 1,
            "deviceRevision": 0,
            "catalogVersion": catalog_version,
        }
        if expectation == "responses":
            del turn_body["text"]
        turn_key = str(uuid4())
        status, accepted = call("POST", turn_path, bearer=token, key=turn_key, body=turn_body)
        check(status == 202 and accepted.get("operationId"), "turn_submit_failed")
        if index == 0:
            status, repeated = call(
                "POST", turn_path, bearer=token, key=turn_key, body=turn_body
            )
            check(
                status == 202 and repeated.get("operationId") == accepted["operationId"],
                "turn_idempotency_failed",
            )

        operation = None
        for _ in range(260):
            status, operation = call(
                "GET", f"/v1/operations/{accepted['operationId']}", bearer=token
            )
            check(status == 200, "operation_poll_failed")
            if operation.get("status") not in {"pending", "running"}:
                break
            time.sleep(0.25)

        check(operation is not None and operation.get("status") == "completed", "turn_failed")
        result = operation.get("result", {})
        if expectation == "fake":
            check(
                result.get("decision") == "ask" and result.get("proposal") is None,
                "fake_result_failed",
            )
            continue

        check(
            isinstance(result.get("reply"), str)
            and bool(result["reply"])
            and result["reply"] != "Toto je testovací odpověď bez AI.",
            "responses_result_failed",
        )
        check(result.get("decision") == "ask" and result.get("proposal") is None,
              "responses_opening_failed")
        print(f"responses_turn_{index + 1}_reply=" + json.dumps(result["reply"], ensure_ascii=False))
    status, _ = call(
        "DELETE", f"/v1/conversations/{conversation_id}", bearer=token, key=str(uuid4())
    )
    check(status == 204, "conversation_cleanup_failed")
    print(f"deployed_{expectation}_dialogue=ok; transport={transport}; {len(visitor_turns)} turns, token, idempotency, poll and cleanup verified")


if __name__ == "__main__":
    main()
