import io
import json
import queue
import unittest
from unittest.mock import patch

import realtime_live_smoke as live


class LiveSmokeSafetyTests(unittest.TestCase):
    def test_legacy_monetary_settings_do_not_gate_token_exchange(self):
        for cap in (None, "0", "3000001", "invalid-ignored"):
            settings = {"PUBLIC_IPV4": "8.8.8.8", "DEV_DEVICE_CREDENTIAL": "fixture", "DEVICE_ID": "fixture"}
            if cap is not None:
                settings["SESSION_CAP_MICRO_USD"] = cap
            with self.subTest(cap=cap), patch.dict(live.os.environ, settings, clear=True), \
                    patch.object(live, "ApiClient", side_effect=RuntimeError("offline fixture reached")) as client:
                with self.assertRaisesRegex(RuntimeError, "offline fixture reached"):
                    live.run()
                client.assert_called_once()

    def test_stream_rejects_oversized_and_unterminated_events(self):
        for content in (b"x" * 65537, b'{"type":"heartbeat"}'):
            with self.subTest(length=len(content)):
                events = queue.Queue()
                live.read_stream(io.BytesIO(content), events)
                self.assertEqual(events.get_nowait(), {"type": "reader_failed"})
                self.assertTrue(events.empty())

    def test_latency_timestamp_is_captured_before_consumer_wait(self):
        events = queue.Queue()
        stream = io.BytesIO(json.dumps({"type": "heartbeat"}).encode() + b"\n")
        with patch.object(live.time, "monotonic", return_value=123.5):
            live.read_stream(stream, events)
        self.assertEqual(events.get_nowait()["_receivedAt"], 123.5)


if __name__ == "__main__":
    unittest.main()
