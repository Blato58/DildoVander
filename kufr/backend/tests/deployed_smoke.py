"""Exercise the installed fake backend without exposing its test credential."""

import json
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
    expectation = os.environ.get("DIALOG_EXPECTATION", "fake")
    check(expectation in {"fake", "responses"}, "invalid_dialog_expectation")
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
        "catalogVersion": "neutral-1",
        "locale": "cs-CZ",
        "consent": {"audio": False, "image": False},
    }
    create_key = str(uuid4())
    status, created = call(
        "POST", "/v1/conversations", bearer=token, key=create_key, body=conversation_body
    )
    check(status == 201 and created.get("conversationId"), "conversation_create_failed")
    conversation_id = created["conversationId"]

    turn_body = {
        "clientTurnId": str(uuid4()),
        "bootId": boot_id,
        "generation": 0,
        "text": (
            "Na večírku chci Hůlku 1. Klidně mě lehce popíchni. "
            "Po výběru potvrdím fyzickým tlačítkem."
            if expectation == "responses"
            else "Syntetický test nasazené vývojové instance."
        ),
        "readyMask": 1,
        "deviceRevision": 0,
        "catalogVersion": "neutral-1",
    }
    turn_path = f"/v1/conversations/{conversation_id}/turns"
    turn_key = str(uuid4())
    status, accepted = call("POST", turn_path, bearer=token, key=turn_key, body=turn_body)
    check(status == 202 and accepted.get("operationId"), "turn_submit_failed")
    status, repeated = call("POST", turn_path, bearer=token, key=turn_key, body=turn_body)
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
    else:
        check(
            isinstance(result.get("reply"), str)
            and bool(result["reply"])
            and result["reply"] != "Toto je testovací odpověď bez AI."
            and result.get("decision") in {"ask", "propose", "no_selection"},
            "responses_result_failed",
        )
        proposal = result.get("proposal")
        check(
            (result["decision"] in {"ask", "no_selection"} and proposal is None)
            or (
                result["decision"] == "propose"
                and isinstance(proposal, dict)
                and proposal.get("compartment") == 1
                and proposal.get("deviceRevision") == turn_body["deviceRevision"]
                and proposal.get("turnId") == turn_body["clientTurnId"]
            ),
            "responses_proposal_failed",
        )
    status, _ = call(
        "DELETE", f"/v1/conversations/{conversation_id}", bearer=token, key=str(uuid4())
    )
    check(status == 204, "conversation_cleanup_failed")
    print(f"deployed_{expectation}_dialogue=ok; token, idempotency, poll and cleanup verified")


if __name__ == "__main__":
    main()
