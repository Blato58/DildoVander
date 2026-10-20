"""Retained stream diagnostics keep the original bounded transport protections."""
import io
import unittest
from unittest.mock import Mock

import backend_api_client as api


class TransportTests(unittest.TestCase):
    def test_public_address_validation_and_authorized_redirect_rejection(self):
        for address in ("127.0.0.1", "example.test", "https://8.8.8.8"):
            with self.subTest(address=address), self.assertRaises(api.DialogueError):
                api.validate_base_url(address)
        self.assertEqual(api.validate_base_url("8.8.8.8"), "https://8.8.8.8")
        with self.assertRaisesRegex(api.DialogueError, "redirect_rejected"):
            api.NoRedirect().redirect_request(None, None, 302, "Found", {}, "https://other.example")

    def test_transport_error_never_exports_private_details(self):
        client = api.ApiClient("https://8.8.8.8")
        client.opener = Mock()
        client.opener.open.side_effect = TimeoutError("private-token-private-provider")
        with self.assertRaises(api.DialogueError) as error:
            client.call("GET", "/v1/diagnostics", token="private-token")
        self.assertEqual(str(error.exception), "transport_failed")

    def test_oversized_or_invalid_json_response_is_rejected(self):
        for payload, code in ((b"x" * 65537, "response_too_large"), (b"private-bad-json", "invalid_json_response")):
            client = api.ApiClient("https://8.8.8.8")
            response = io.BytesIO(payload)
            response.status = 200
            client.opener = Mock()
            client.opener.open.return_value = response
            with self.subTest(code=code), self.assertRaisesRegex(api.DialogueError, code):
                client.call("GET", "/v1/diagnostics")


if __name__ == "__main__":
    unittest.main()
