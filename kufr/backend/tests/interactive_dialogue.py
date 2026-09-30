"""Run one carefully bounded, human-directed turn against the public backend."""

import ipaddress
import json
import os
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from uuid import UUID, uuid5


CATALOG_VERSION = "ollivander-private-party-v1"
# Start/opening uses one token exchange, one create, one turn and at most twelve polls:
# fifteen requests total. Five-second polling gives a provider roughly 65 seconds.
POLL_ATTEMPTS = 12
POLL_DELAY_SECONDS = 5
POLL_DEADLINE_SECONDS = 65
REQUEST_TIMEOUT_SECONDS = 10
MAX_RESPONSE_BYTES = 65_536
UUID_NAMESPACE = UUID("d8da49d2-59ad-489d-8354-9cdf57c3b02a")


class DialogueError(RuntimeError):
    """A safe-to-publish failure label for the manual workflow report."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise DialogueError("redirect_rejected")


def check(condition, label):
    if not condition:
        raise DialogueError(label)


def parse_uuid(value, label):
    check(isinstance(value, str), label)
    try:
        parsed = UUID(value)
    except (AttributeError, ValueError, TypeError) as error:
        raise DialogueError(label) from error
    check(str(parsed) == value.lower(), label)
    return parsed


def derived_uuid(request_id, name):
    return str(uuid5(UUID_NAMESPACE, f"{request_id}:{name}"))


def validate_base_url(value):
    try:
        address = ipaddress.ip_address(value)
    except ValueError as error:
        raise DialogueError("invalid_public_ipv4") from error
    check(isinstance(address, ipaddress.IPv4Address) and address.is_global, "public_ipv4_not_global")
    return f"https://{address}"


def parse_state(raw, *, required=False):
    if not raw:
        check(not required, "state_required")
        return None
    try:
        state = json.loads(raw)
    except json.JSONDecodeError as error:
        raise DialogueError("invalid_state") from error
    expected_keys = {"conversationId", "bootId", "generation", "readyMask", "deviceRevision", "catalogVersion"}
    check(isinstance(state, dict) and set(state) == expected_keys, "invalid_state")
    parse_uuid(state["conversationId"], "invalid_state")
    parse_uuid(state["bootId"], "invalid_state")
    check(type(state["generation"]) is int and state["generation"] == 0
          and type(state["readyMask"]) is int and state["readyMask"] == 63
          and type(state["deviceRevision"]) is int and state["deviceRevision"] == 0
          and type(state["catalogVersion"]) is str and state["catalogVersion"] == CATALOG_VERSION,
          "invalid_state")
    return state


def new_state(conversation_id, request_id):
    conversation_id = str(parse_uuid(conversation_id, "conversation_create_failed"))
    return {
        "conversationId": conversation_id,
        "bootId": derived_uuid(request_id, "boot"),
        "generation": 0,
        "readyMask": 63,
        "deviceRevision": 0,
        "catalogVersion": CATALOG_VERSION,
    }


class ApiClient:
    def __init__(self, base_url):
        self.base_url = base_url
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())

    def call(self, method, path, *, token=None, key=None, body=None):
        headers = {"Accept": "application/json"}
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        if key is not None:
            headers["Idempotency-Key"] = key
        data = None
        if body is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(body, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(self.base_url + path, data=data, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=REQUEST_TIMEOUT_SECONDS)
        except urllib.error.HTTPError as error:
            # Error bodies can include deployment diagnostics. Deliberately never expose them.
            raise DialogueError(f"http_status_{error.code}") from error
        except (urllib.error.URLError, TimeoutError, socket.timeout, OSError) as error:
            raise DialogueError("transport_failed") from error

        try:
            with response:
                check(200 <= response.status < 300, f"http_status_{response.status}")
                content = response.read(MAX_RESPONSE_BYTES + 1)
                check(len(content) <= MAX_RESPONSE_BYTES, "response_too_large")
                if not content:
                    return response.status, None
                try:
                    return response.status, json.loads(content)
                except json.JSONDecodeError as error:
                    raise DialogueError("invalid_json_response") from error
        except (TimeoutError, socket.timeout, OSError) as error:
            raise DialogueError("transport_failed") from error


def issue_token(client, credential, device_id):
    status, result = client.call("POST", "/v1/device-access/token", token=credential)
    check(status == 200 and isinstance(result, dict) and result.get("deviceId") == device_id,
          "token_exchange_failed")
    token = result.get("accessToken")
    check(isinstance(token, str) and token, "token_exchange_failed")
    return token


def submit_and_poll(client, token, state, request_id, device_id, *, opening, text=None):
    client_turn_id = derived_uuid(request_id, "opening-turn" if opening else "reply-turn")
    body = {
        "clientTurnId": client_turn_id,
        "bootId": state["bootId"],
        "generation": state["generation"],
        "readyMask": state["readyMask"],
        "deviceRevision": state["deviceRevision"],
        "catalogVersion": state["catalogVersion"],
    }
    if not opening:
        check(isinstance(text, str) and text.strip() and len(text) <= 1000, "invalid_text")
        body["text"] = text
    path = f"/v1/conversations/{state['conversationId']}/{'opening' if opening else 'turns'}"
    status, accepted = client.call(
        "POST", path, token=token,
        key=derived_uuid(request_id, "opening-key" if opening else "reply-key"), body=body)
    check(status == 202 and isinstance(accepted, dict) and isinstance(accepted.get("operationId"), str),
          "turn_submit_failed")
    operation_id = str(parse_uuid(accepted["operationId"], "turn_submit_failed"))
    deadline = time.monotonic() + POLL_DEADLINE_SECONDS
    for attempt in range(POLL_ATTEMPTS):
        status, operation = client.call("GET", f"/v1/operations/{operation_id}", token=token)
        check(status == 200 and isinstance(operation, dict), "operation_poll_failed")
        correlation = operation.get("correlation")
        check(operation.get("kind") == "turn"
              and operation.get("operationId") == operation_id
              and isinstance(correlation, dict)
              and correlation.get("deviceId") == device_id
              and correlation.get("conversationId") == state["conversationId"]
              and correlation.get("bootId") == state["bootId"]
              and correlation.get("clientTurnId") == client_turn_id
              and correlation.get("generation") == state["generation"]
              and correlation.get("deviceRevision") == state["deviceRevision"], "operation_correlation_failed")
        operation_status = operation.get("status")
        if operation_status not in {"pending", "running"}:
            check(operation_status == "completed", f"operation_{operation_status}")
            result = operation.get("result")
            check(isinstance(result, dict) and isinstance(result.get("reply"), str) and result["reply"],
                  "operation_reply_missing")
            return {"operationId": operation_id, "status": operation_status,
                    "correlation": correlation, "reply": result["reply"],
                    "decision": result.get("decision"), "proposal": result.get("proposal"),
                    "operation": operation, "receivedAtUnixSeconds": int(time.time())}
        if attempt + 1 < POLL_ATTEMPTS:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(POLL_DELAY_SECONDS, remaining))
    raise DialogueError("operation_poll_timeout")


def write_report(report, environment, *, publish=True):
    report_path = Path(environment.get("RUNNER_TEMP", ".")) / "interactive-dialogue.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if not publish:
        return
    output_path = environment.get("GITHUB_OUTPUT")
    if output_path:
        delimiter = derived_uuid(report["requestId"], "output")
        with Path(output_path).open("a", encoding="utf-8") as output:
            output.write(f"report_path={report_path}\nreport_json<<{delimiter}\n")
            output.write(json.dumps(report, ensure_ascii=False) + f"\n{delimiter}\n")
    # Keep local Windows console execution reliable; the artifact remains UTF-8.
    print(json.dumps(report, ensure_ascii=True))


def run(environment=None, client_factory=ApiClient):
    environment = os.environ if environment is None else environment
    action = environment.get("INPUT_ACTION", "")
    request_id = environment.get("INPUT_REQUEST_ID", "")
    state = None
    report = {"requestId": request_id, "action": action, "status": "failed", "state": state}
    try:
        check(action in {"start", "reply", "finish", "status", "clear"}, "invalid_action")
        request_id = str(parse_uuid(request_id, "invalid_request_id"))
        report["requestId"] = request_id
        state = parse_state(environment.get("INPUT_STATE", ""), required=action in {"reply", "finish"})
        report["state"] = state
        base_url = validate_base_url(environment.get("KUFR_BACKEND_PUBLIC_IPV4", ""))
        device_id = environment.get("DEVICE_ID", "")
        credential = environment.get("DEV_DEVICE_CREDENTIAL", "")
        check(device_id and credential, "development_identity_missing")
        client = client_factory(base_url)
        token = issue_token(client, credential, device_id)

        if action == "start":
            if state is None:
                create_body = {"deviceId": device_id, "bootId": derived_uuid(request_id, "boot"), "generation": 0,
                               "catalogVersion": CATALOG_VERSION, "locale": "cs-CZ",
                               "consent": {"audio": False, "image": False}}
                status, created = client.call("POST", "/v1/conversations", token=token,
                                              key=derived_uuid(request_id, "create-key"), body=create_body)
                check(status == 201 and isinstance(created, dict) and isinstance(created.get("conversationId"), str),
                      "conversation_create_failed")
                state = new_state(created["conversationId"], request_id)
                report["state"] = state  # Persist before opening so an operator can finish it after failure.
                report["status"] = "pending"
                write_report(report, environment, publish=False)
                report["status"] = "failed"
            report["turn"] = submit_and_poll(client, token, state, request_id, device_id, opening=True)
        elif action == "reply":
            report["turn"] = submit_and_poll(client, token, state, request_id, device_id, opening=False,
                                               text=environment.get("INPUT_TEXT", ""))
        elif action == "finish":
            status, _ = client.call("DELETE", f"/v1/conversations/{state['conversationId']}", token=token,
                                    key=derived_uuid(request_id, "finish-key"))
            check(status == 204, "conversation_finish_failed")
            report["state"] = None
        elif action == "status":
            status, evening = client.call("GET", "/v1/evening-memory", token=token)
            check(status == 200 and isinstance(evening, dict), "evening_status_failed")
            report["evening"] = evening
        else:
            expected = str(parse_uuid(environment.get("INPUT_EXPECTED_EVENING_ID"), "expected_evening_id_required"))
            status, evening = client.call("GET", "/v1/evening-memory", token=token)
            check(status == 200 and isinstance(evening, dict) and evening.get("eveningId") == expected,
                  "expected_evening_mismatch")
            status, _ = client.call("DELETE", f"/v1/evening-memory/{expected}", token=token,
                                    key=derived_uuid(request_id, "clear-key"))
            check(status == 204, "evening_clear_failed")
            report["clearedEveningId"] = expected
        report["status"] = "ok"
    except DialogueError as error:
        report["error"] = str(error)
    write_report(report, environment)
    return report


def main():
    report = run()
    return 0 if report["status"] == "ok" else 1


if __name__ == "__main__":
    sys.exit(main())
