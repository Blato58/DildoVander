import io
import json
import queue
import unittest
from unittest.mock import patch

import realtime_live_smoke as live


class LiveSmokeSafetyTests(unittest.TestCase):
    def test_invalid_budget_stops_before_token_exchange(self):
        for cap in ("0", "1000001"):
            with self.subTest(cap=cap), patch.dict(live.os.environ, {
                "SESSION_CAP_MICRO_USD": cap,
                "DAILY_CAP_MICRO_USD": "10000000",
                "MONTHLY_CAP_MICRO_USD": "10000000",
                "APPROVED_BUDGET_USD": "1",
            }, clear=True), patch.object(live, "ApiClient") as client:
                with self.assertRaises(live.DialogueError):
                    live.run()
                client.assert_not_called()

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
