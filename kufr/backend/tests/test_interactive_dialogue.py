import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import interactive_dialogue as dialogue


REQUEST_ID = "8150c7f7-9644-4eff-914e-47e1b216daf1"
CONVERSATION_ID = "fbb5ba16-8ca0-4bf5-a881-c1820a0e692e"


class FakeClient:
    def __init__(self, base_url, *, fail_opening=False, opening_error=None,
                 invalid_correlation=None, invalid_operation_id=False):
        self.base_url = base_url
        self.fail_opening = fail_opening
        self.opening_error = opening_error
        self.invalid_correlation = invalid_correlation
        self.invalid_operation_id = invalid_operation_id
        self.calls = []
        self.operation_id = "c761fd86-ddf4-4c3b-9ae3-20dd48344d67"

    def call(self, method, path, *, token=None, key=None, body=None):
        self.calls.append((method, path, key, body))
        if path == "/v1/device-access/token":
            return 200, {"deviceId": "dev-simulator-1", "accessToken": "private-token"}
        if method == "POST" and path == "/v1/conversations":
            return 201, {"conversationId": CONVERSATION_ID}
        if self.fail_opening and path.endswith("/opening"):
            raise dialogue.DialogueError("http_status_503")
        if self.opening_error and path.endswith("/opening"):
            raise dialogue.DialogueError(self.opening_error)
        if method == "POST" and (path.endswith("/opening") or path.endswith("/turns")):
            return 202, {"operationId": self.operation_id}
        if path == "/v1/operations/c761fd86-ddf4-4c3b-9ae3-20dd48344d67":
            body = self.calls[-2][3]
            correlation = {"deviceId": "dev-simulator-1", "conversationId": CONVERSATION_ID,
                "bootId": body["bootId"],
                "clientTurnId": body["clientTurnId"], "generation": 0, "deviceRevision": 0}
            if self.invalid_correlation:
                correlation[self.invalid_correlation] = "wrong-value"
            operation_id = "other-operation" if self.invalid_operation_id else self.operation_id
            return 200, {"operationId": operation_id, "status": "completed", "kind": "turn", "correlation": correlation,
                "result": {"reply": "Syntetická odpověď.", "decision": "ask", "proposal": None}}
        raise AssertionError((method, path))


class InteractiveDialogueTests(unittest.TestCase):
    def environment(self, **values):
        result = {"INPUT_ACTION": "start", "INPUT_REQUEST_ID": REQUEST_ID,
                  "KUFR_BACKEND_PUBLIC_IPV4": "8.8.8.8", "DEVICE_ID": "dev-simulator-1",
                  "DEV_DEVICE_CREDENTIAL": "private-credential"}
        result.update(values)
        return result

    def test_start_uses_opening_without_text_and_deterministic_ids(self):
        clients = []
        def factory(url):
            client = FakeClient(url)
            clients.append(client)
            return client
        with tempfile.TemporaryDirectory() as temporary:
            environment = self.environment(RUNNER_TEMP=temporary)
            first = dialogue.run(environment, factory)
            second = dialogue.run(environment, factory)
        self.assertEqual("ok", first["status"])
        self.assertEqual(first["state"]["bootId"], second["state"]["bootId"])
        opening = next(call for call in clients[0].calls if call[1].endswith("/opening"))
        self.assertNotIn("text", opening[3])
        self.assertEqual(opening[2], dialogue.derived_uuid(REQUEST_ID, "opening-key"))

    def test_reply_uses_turns_and_text(self):
        state = dialogue.new_state(CONVERSATION_ID, REQUEST_ID)
        client = FakeClient("https://8.8.8.8")
        with tempfile.TemporaryDirectory() as temporary:
            report = dialogue.run(self.environment(INPUT_ACTION="reply", INPUT_STATE=dialogue.json.dumps(state),
                                                   INPUT_TEXT="Syntetický návštěvník.", RUNNER_TEMP=temporary),
                                  lambda _: client)
        turn = next(call for call in client.calls if call[1].endswith("/turns"))
        self.assertEqual("ok", report["status"])
        self.assertEqual("Syntetický návštěvník.", turn[3]["text"])

    def test_rejects_non_global_or_invalid_public_ipv4(self):
        for value in ("127.0.0.1", "example.test", "https://8.8.8.8"):
            with self.assertRaises(dialogue.DialogueError):
                dialogue.validate_base_url(value)

    def test_reply_requires_opaque_state(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = dialogue.run(self.environment(INPUT_ACTION="reply", RUNNER_TEMP=temporary),
                                  lambda _: self.fail("client must not be created"))
        self.assertEqual("failed", report["status"])
        self.assertEqual("state_required", report["error"])

    def test_state_rejects_extra_keys_path_like_ids_and_boolean_integers(self):
        state = dialogue.new_state(CONVERSATION_ID, REQUEST_ID)
        for invalid_state in (
            {**state, "unexpected": "value"},
            {**state, "conversationId": "../../operations/1"},
            {**state, "generation": True},
        ):
            with self.assertRaises(dialogue.DialogueError) as context:
                dialogue.parse_state(dialogue.json.dumps(invalid_state), required=True)
            self.assertEqual("invalid_state", str(context.exception))

    def test_redirects_are_rejected_before_authorized_follow_up(self):
        with self.assertRaises(dialogue.DialogueError) as context:
            dialogue.NoRedirect().redirect_request(None, None, 302, "Found", {}, "https://other.example")
        self.assertEqual("redirect_rejected", str(context.exception))

    def test_operation_must_match_accepted_id_and_device_correlation(self):
        state = dialogue.new_state(CONVERSATION_ID, REQUEST_ID)
        for mismatch in ("deviceId",):
            with self.assertRaises(dialogue.DialogueError) as context:
                dialogue.submit_and_poll(FakeClient("https://8.8.8.8", invalid_correlation=mismatch),
                                         "private-token", state, REQUEST_ID, "dev-simulator-1", opening=True)
            self.assertEqual("operation_correlation_failed", str(context.exception))
        with self.assertRaises(dialogue.DialogueError) as context:
            dialogue.submit_and_poll(FakeClient("https://8.8.8.8", invalid_operation_id=True),
                                     "private-token", state, REQUEST_ID, "dev-simulator-1", opening=True)
        self.assertEqual("operation_correlation_failed", str(context.exception))

    def test_start_failure_retains_created_state_in_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            report = dialogue.run(self.environment(RUNNER_TEMP=temporary),
                                  lambda url: FakeClient(url, fail_opening=True))
            saved = dialogue.json.loads((Path(temporary) / "interactive-dialogue.json").read_text(encoding="utf-8"))
        self.assertEqual("failed", report["status"])
        self.assertEqual(CONVERSATION_ID, report["state"]["conversationId"])
        self.assertEqual(report["state"], saved["state"])

    def test_network_timeout_is_sanitized_and_keeps_created_state(self):
        class TimeoutOpener:
            def open(self, request, timeout):
                raise TimeoutError("private network detail")

        api_client = dialogue.ApiClient("https://8.8.8.8")
        api_client.opener = TimeoutOpener()
        with self.assertRaises(dialogue.DialogueError) as context:
            api_client.call("GET", "/healthz")
        self.assertEqual("transport_failed", str(context.exception))

        with tempfile.TemporaryDirectory() as temporary:
            report = dialogue.run(self.environment(RUNNER_TEMP=temporary),
                                  lambda url: FakeClient(url, opening_error="transport_failed"))
            saved = dialogue.json.loads((Path(temporary) / "interactive-dialogue.json").read_text(encoding="utf-8"))
        self.assertEqual("transport_failed", report["error"])
        self.assertEqual(CONVERSATION_ID, saved["state"]["conversationId"])


if __name__ == "__main__":
    unittest.main()
