"""Bounded transport shared by explicitly invoked backend diagnostics."""
import ipaddress
import json
import socket
import urllib.error
import urllib.request

REQUEST_TIMEOUT_SECONDS = 10
MAX_RESPONSE_BYTES = 65_536

class DialogueError(RuntimeError):
    """Safe diagnostic code; never includes a provider response or credential."""

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise DialogueError("redirect_rejected")

def check(condition, label):
    if not condition:
        raise DialogueError(label)

def validate_base_url(value):
    try:
        address = ipaddress.ip_address(value)
    except ValueError as error:
        raise DialogueError("invalid_public_ipv4") from error
    check(isinstance(address, ipaddress.IPv4Address) and address.is_global, "public_ipv4_not_global")
    return f"https://{address}"

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
            error.close()
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


