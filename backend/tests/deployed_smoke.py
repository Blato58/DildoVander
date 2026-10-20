"""Exercise the installed backend without exposing its test credential."""

import json
import ipaddress
import os
import urllib.error
import urllib.request
from engine_capabilities import validate_engine_capabilities
from backend_api_client import NoRedirect


BASE_URL = "http://127.0.0.1:5080"
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())


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

    status, diagnostics = call("GET", "/v1/diagnostics", bearer=token)
    check(status == 200 and isinstance(diagnostics, dict), "diagnostics_failed")
    validate_engine_capabilities(diagnostics.get("engines"))
    # A fixed nonexistent ID proves retired routes reject before conversation
    # creation, provider generation, operation polling or physical preparation.
    retired_id = "00000000-0000-4000-8000-000000000001"
    for route in ("opening", "turns", "transcriptions", "speech", "realtime-turns"):
        status, problem = call("POST", f"/v1/conversations/{retired_id}/{route}", bearer=token, body={})
        check(status == 410 and isinstance(problem, dict)
              and problem.get("code") == "conversation_engine_upgrade_required", "retired_route_not_rejected")
    print(f"deployed_engine_contract=ok; transport={transport}; token, capabilities and retired route upgrade verified; no inference or conversation created")


if __name__ == "__main__":
    main()
