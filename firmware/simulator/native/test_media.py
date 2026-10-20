#!/usr/bin/env python3
"""Offline browser HAL acceptance against the persistent native firmware host."""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import uuid

EXECUTABLE = None


class Wire:
    def __init__(self, folder):
        self.boot = str(uuid.uuid4())
        self.process = subprocess.Popen([str(EXECUTABLE), "--instance", "native-media", "--boot", self.boot,
            "--journal", str(folder / "journal.bin"), "--build", "media-test"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.services = []
        self.media = []
        self.command("SNAPSHOT")
        self.command("INSPECT")

    def command(self, command, payload=None):
        self.process.stdin.write(command.encode("ascii") + b"\n")
        if payload is not None:
            self.process.stdin.write(payload + b"\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise AssertionError(self.process.stderr.read().decode())
        result = json.loads(line)
        extension = result.get("mediaServiceRequest") or result.get("media")
        if extension is not None:
            body = self.process.stdout.read(extension["byteLength"])
            assert len(body) == extension["byteLength"]
            assert self.process.stdout.read(1) == b"\n"
            if "mediaServiceRequest" in result:
                self.services.append((extension, body))
            else:
                self.media.append((extension, body))
        self.state = result["mediaState"]
        self.snapshot = result
        return result

    def service(self, kind, timeout=5):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for index, (header, body) in enumerate(self.services):
                if header["kind"] == kind:
                    return self.services.pop(index)
            self.command("SNAPSHOT")
            time.sleep(.005)
        raise AssertionError(f"missing service {kind}; state {self.state}; services {self.services}")

    def output(self, kind, timeout=5):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for index, (header, body) in enumerate(self.media):
                if header["kind"] == kind:
                    return self.media.pop(index)
            self.command(f"MEDIA_TAKE {self.session} {self.state['epoch']}")
            time.sleep(.005)
        raise AssertionError(f"missing HAL {kind}; state {self.state}")

    def result(self, request, status, body=b"", mime="application/json"):
        if isinstance(body, dict):
            body = json.dumps(body, separators=(",", ":")).encode()
        return self.command(f"MEDIA_RESULT {request['requestId']} {status} {mime} {len(body)}", body)

    def event(self, event):
        result = self.event_result(event)
        assert result["commandResult"]["accepted"], result
        return result

    def event_result(self, event):
        body = json.dumps(event, separators=(",", ":")).encode()
        return self.command(f"MEDIA_EVENT {self.session} {self.state['epoch']} {len(body)}", body)

    def start(self, engine="realtime", photo=False, camera=True):
        self.session = str(uuid.uuid4())
        self.request = str(uuid.uuid4())
        started = self.command(f"MEDIA_START {self.session} {self.request} {engine} cedar 1 {str(photo).lower()} main 1 1 {int(camera)}")
        assert started["commandResult"]["accepted"], started
        request, body = self.service("conversation-create")
        actual = json.loads(body)
        assert actual["deviceId"] == "native-media" and actual["bootId"] == self.boot
        assert actual["catalogVersion"] == "ollivander-private-party-v1"
        assert actual["consent"] == {"audio": True, "image": photo}
        self.conversation = str(uuid.uuid4())
        self.result(request, 201, {"conversationId": self.conversation, "expiresAt": "2099-01-01T00:00:00Z",
            "limits": {"maxTurns": 10, "maxTextCharacters": 1000, "idleSeconds": 900, "maxLifetimeSeconds": 1800}})
        return actual

    def stream(self, opening=True):
        request, body = self.service("realtime-start")
        actual = json.loads(body)
        assert actual["bootId"] == self.boot and actual["opening"] and actual["wav"] is None
        self.correlation = {"deviceId": "native-media", "bootId": self.boot, "conversationId": self.conversation,
            "clientTurnId": actual["clientTurnId"], "generation": actual["generation"], "deviceRevision": actual["deviceRevision"]}
        self.stream_id = str(uuid.uuid4())
        self.operation = str(uuid.uuid4())
        self.result(request, 200, mime="application/x-ndjson")
        self.event({"type": "session", "streamId": self.stream_id, "audioFormat": "pcm16-mono-24000"})
        if opening:
            self.event({"type": "started", "operationId": self.operation, "correlation": self.correlation,
                "responseGeneration": 0})
        return actual

    def audio_complete(self, proposal=None):
        self.event({"type": "audio", "delta": base64.b64encode(bytes(6)).decode(), "responseGeneration": 0})
        decision = "propose" if proposal else "no_selection"
        self.event({"type": "completed", "operationId": self.operation, "correlation": self.correlation, "responseGeneration": 0,
            "result": {"reply": "Ahoj", "decision": decision, "reason": "", "proposal": proposal}, "costMicroUsd": 100})
        self.output("pcm")
        return self.output("drain")

    def ack(self, header, **overrides):
        fields = dict(header, **overrides)
        return self.command(f"MEDIA_PLAYED {self.session} {fields['epoch']} {fields['outputEpoch']} {fields['sequence']} {fields['pcmBytes']} true")

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=5)
        stderr = self.process.stderr.read().decode()
        self.process.stdout.close()
        self.process.stderr.close()
        assert self.process.returncode == 0, stderr


class MediaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="twin-browser-media-")
        self.wire = Wire(Path(self.temp.name))

    def tearDown(self):
        self.wire.close()
        self.temp.cleanup()

    def test_real_codec_drain_before_played_and_completed(self):
        self.wire.start()
        self.wire.stream()
        drain, _ = self.wire.audio_complete()
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-pcm-enqueued")
        self.assertFalse(self.wire.ack(drain, pcmBytes="4")["commandResult"]["accepted"])
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)
        self.wire.command("TICK 60000")
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-pcm-enqueued")
        self.assertFalse(any(header["kind"] == "realtime-played" for header, _ in self.wire.services))
        self.assertTrue(self.wire.ack(drain)["commandResult"]["accepted"])
        played, body = self.wire.service("realtime-played")
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-playback-drained")
        self.assertEqual(json.loads(body), {"operationId": self.wire.operation, "responseGeneration": 0})
        self.wire.result(played, 204)
        dialogue, _ = self.wire.output("dialogue")
        self.assertEqual(dialogue["text"], "Ahoj")
        self.wire.event({"type": "closed", "reason": "conversation_terminal"})
        delete, _ = self.wire.service("conversation-delete")
        self.wire.result(delete, 204)
        self.wire.output("completed")
        self.assertEqual(self.wire.state["phase"], "completed")
        state = dict(self.wire.state)
        retired = self.wire.command(f"MEDIA_FRAME {self.wire.session} {state['epoch']} {state['inputEpoch']} 0 pcm 2", b"\0\0")
        self.assertFalse(retired["commandResult"]["accepted"])
        self.assertEqual(retired["commandResult"]["code"], "media-input-retired")
        self.assertEqual(self.wire.state, state)

    def test_started_never_publishes_pending_dialogue(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        for _ in range(5):
            self.wire.command(f"MEDIA_TAKE {self.wire.session} {self.wire.state['epoch']}")
            time.sleep(.005)
        self.assertFalse(any(header["kind"] == "dialogue" for header, _ in self.wire.media))
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")

    def test_real_size_burst_with_slow_consumer_preserves_bytes_and_ack(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        frames = [bytes([index % 251]) * 4800 for index in range(260)]
        received = bytearray()
        headers = []
        kinds = []

        def consume():
            self.wire.command(f"MEDIA_TAKE {self.wire.session} {self.wire.state['epoch']}")
            while self.wire.media:
                header, body = self.wire.media.pop(0)
                kinds.append(header["kind"])
                if header["kind"] == "pcm":
                    self.assertLessEqual(len(body), 32768)
                    self.assertGreater(len(body), 0)
                    if headers:
                        self.assertGreater(int(header["sequence"]), int(headers[-1]["sequence"]))
                    received.extend(body)
                    self.assertEqual(int(header["pcmBytes"]), len(received))
                    headers.append(header)
            time.sleep(.005)

        backpressure = False
        for index, frame in enumerate(frames):
            event = {"type": "audio", "delta": base64.b64encode(frame).decode()}
            deadline = time.monotonic() + 4
            while True:
                result = self.wire.event_result(event)
                if result["commandResult"]["accepted"]:
                    break
                self.assertEqual(result["commandResult"]["code"], "media-event-backpressure")
                self.assertTrue(self.wire.state["active"])
                self.assertNotIn("error", self.wire.state)
                backpressure = True
                self.assertLess(time.monotonic(), deadline)
                consume()
            # Let the worker consume events without draining browser output.
            # Once full, consume slowly; retry only rejected events.
            time.sleep(.005)
            if index > 210:
                consume()
        expected = b"".join(frames)
        deadline = time.monotonic() + 4
        while len(received) < len(expected):
            self.assertLess(time.monotonic(), deadline)
            consume()
        self.assertTrue(backpressure, "burst exercises worker/event queue backpressure")
        self.assertEqual(bytes(received), expected)
        self.assertLess(len(headers), len(frames), "queued PCM tail was coalesced")
        self.assertEqual(kinds.count("speaking"), 1)
        self.assertNotIn("dialogue", kinds)
        self.wire.event({"type": "playback_checkpoint", "playbackId": str(uuid.uuid4()),
            "pcmBytes": len(expected), "decisionRevision": 1})
        drain, _ = self.wire.output("drain")
        self.assertEqual(int(drain["pcmBytes"]), len(expected))
        self.assertFalse(self.wire.ack(drain, pcmBytes=str(len(expected) - 2))["commandResult"]["accepted"])
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-pcm-enqueued")
        self.assertTrue(self.wire.ack(drain)["commandResult"]["accepted"])
        playback, _ = self.wire.service("realtime-playback")
        self.wire.result(playback, 204)
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-playback-drained")
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")

    def test_event_capacity_rejection_is_nonmutating_and_only_current_busy(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        self.wire.event({"type": "audio", "delta": base64.b64encode(bytes(4800)).decode()})
        self.wire.output("pcm")
        self.wire.event({"type": "playback_checkpoint", "playbackId": str(uuid.uuid4()),
            "pcmBytes": 4800, "decisionRevision": 1})
        drain, _ = self.wire.output("drain")
        for _ in range(8):
            self.wire.event({"type": "heartbeat"})
        rejected = self.wire.event_result({"type": "heartbeat"})
        self.assertFalse(rejected["commandResult"]["accepted"])
        self.assertEqual(rejected["commandResult"]["code"], "media-event-backpressure")
        self.assertTrue(rejected["mediaState"]["active"])
        self.assertNotIn("error", rejected["mediaState"])
        self.assertEqual(rejected["reported"]["audio"], "browser-pcm-enqueued")
        wrong = self.wire.command(f"MEDIA_EVENT {self.wire.session} 0 2", b"{}")
        self.assertEqual(wrong["commandResult"]["code"], "media-rejected")
        self.assertTrue(self.wire.ack(drain)["commandResult"]["accepted"])
        playback, _ = self.wire.service("realtime-playback")
        self.wire.result(playback, 204)
        # Accepted heartbeats remain valid. A capacity rejection can be
        # retried after consumption resumes without poisoning the actor.
        deadline = time.monotonic() + 2
        while not self.wire.event_result({"type": "heartbeat"})["commandResult"]["accepted"]:
            self.assertLess(time.monotonic(), deadline)
            time.sleep(.005)
        self.assertTrue(self.wire.state["active"])
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")

    def test_cancel_wakes_worker_waiting_for_output_capacity(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        input_id = str(uuid.uuid4())
        self.wire.event({"type": "listening", "inputId": input_id})
        self.wire.output("listening")
        self.assertTrue(self.wire.state["inputEnabled"])
        event = {"type": "audio", "delta": base64.b64encode(bytes(4800)).decode()}
        blocked = False
        for _ in range(400):
            result = self.wire.event_result(event)
            if not result["commandResult"]["accepted"]:
                self.assertEqual(result["commandResult"]["code"], "media-event-backpressure")
                # Give a decoding worker time to consume. Persistent pressure
                # means it is waiting for output rather than executing a frame.
                time.sleep(.03)
                result = self.wire.event_result(event)
                if not result["commandResult"]["accepted"]:
                    self.assertEqual(result["commandResult"]["code"], "media-event-backpressure")
                    blocked = True
                    break
            time.sleep(.005)
        self.assertTrue(blocked)
        self.assertTrue(self.wire.state["active"])
        pcm = bytes(4800)
        input_epoch = self.wire.state["inputEpoch"]
        uploaded = self.wire.command(f"MEDIA_FRAME {self.wire.session} {self.wire.state['epoch']} {input_epoch} 0 pcm {len(pcm)}", pcm)
        self.assertTrue(uploaded["commandResult"]["accepted"])
        began = time.monotonic()
        upload, body = self.wire.service("realtime-input", timeout=1)
        self.assertLess(time.monotonic() - began, 1)
        self.assertEqual(upload["contentType"], "application/octet-stream")
        self.assertEqual(body, pcm)
        self.wire.result(upload, 204)
        began = time.monotonic()
        cancelled = self.wire.command(f"MEDIA_CANCEL {self.wire.session}")
        self.assertTrue(cancelled["commandResult"]["accepted"])
        self.assertLess(time.monotonic() - began, 1)
        self.assertFalse(self.wire.state["active"])
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-playback-cancelled")
        self.assertEqual(self.wire.snapshot["reported"]["slot"], 0)
        # A new Start joins the cancelled worker; it must not wait for the old
        # capacity deadline or require any playback acknowledgement.
        self.wire.session = str(uuid.uuid4())
        began = time.monotonic()
        request_id = str(uuid.uuid4())
        deadline = began + 1
        while True:
            restarted = self.wire.command(f"MEDIA_START {self.wire.session} {request_id} gpt-live cedar 1 false main 1 1 0")
            if restarted["commandResult"]["accepted"]:
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(.005)
        self.assertLess(time.monotonic() - began, 1)
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")

    def test_stop_during_drain_rejects_old_epoch_and_keeps_owner_responsive(self):
        self.wire.start()
        self.wire.stream()
        drain, _ = self.wire.audio_complete()
        before = time.monotonic()
        stopped = self.wire.command("STOP")
        self.assertLess(time.monotonic() - before, .5)
        self.assertTrue(stopped["reported"]["needsRecovery"])
        self.assertIn(stopped["reported"]["audio"], ("browser-playback-unknown", "browser-playback-cancelled"))
        self.assertFalse(self.wire.ack(drain)["commandResult"]["accepted"])
        self.assertFalse(self.wire.state["active"])
        self.assertNotEqual(drain["epoch"], self.wire.state["epoch"])

    def test_cancel_pending_http_wakes_and_late_result_cannot_restart(self):
        self.wire.session = str(uuid.uuid4())
        request_id = str(uuid.uuid4())
        self.wire.command(f"MEDIA_START {self.wire.session} {request_id} realtime cedar 1 false main 1 1 0")
        pending, _ = self.wire.service("conversation-create")
        before = time.monotonic()
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")
        self.assertLess(time.monotonic() - before, .5)
        self.assertFalse(self.wire.result(pending, 201, {"invalid": "late"})["commandResult"]["accepted"])
        self.assertFalse(self.wire.state["active"])
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)

    def test_camera_unavailable_skips_photo_before_audio(self):
        self.wire.start(photo=True, camera=False)
        self.wire.stream()
        self.assertEqual(self.wire.state["photoWarning"], "photo_unavailable")
        self.assertFalse(any(header["kind"] == "image-upload" for header, _ in self.wire.services))

    def test_wrong_service_response_uuid_and_invalid_event_fail_closed(self):
        self.wire.start()
        self.wire.stream()
        self.assertFalse(self.wire.result({"requestId": str(uuid.uuid4())}, 204)["commandResult"]["accepted"])
        self.wire.event({"type": "completed", "operationId": str(uuid.uuid4()), "correlation": self.wire.correlation,
            "result": {"reply": "No", "decision": "no_selection", "reason": "", "proposal": None}, "costMicroUsd": 1})
        delete, _ = self.wire.service("conversation-delete")
        self.wire.result(delete, 204)
        self.wire.output("failed")
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)

    def test_input_epoch_sequence_and_400ms_overflow(self):
        self.wire.start()
        self.wire.stream(opening=False)
        input_id = str(uuid.uuid4())
        self.wire.event({"type": "listening", "inputId": input_id})
        listening, _ = self.wire.output("listening")
        epoch, input_epoch = listening["epoch"], listening["inputEpoch"]
        pcm = bytes(9600)
        def frame(sequence, expected_epoch=input_epoch):
            return self.wire.command(f"MEDIA_FRAME {self.wire.session} {epoch} {expected_epoch} {sequence} pcm {len(pcm)}", pcm)
        retired = frame(0, "0")
        self.assertFalse(retired["commandResult"]["accepted"])
        self.assertEqual(retired["commandResult"]["code"], "media-input-retired")
        self.assertTrue(frame(0)["commandResult"]["accepted"])
        request, body = self.wire.service("realtime-input")
        self.assertEqual(body, pcm)
        self.assertEqual(request["headers"], {"Input-Id": input_id, "Input-Sequence": "0"})
        # The uploader is now awaiting its actual HTTP result. Exactly400ms may
        # queue behind it; the next block fails closed rather than growing.
        duplicate = frame(0)
        self.assertFalse(duplicate["commandResult"]["accepted"])
        self.assertEqual(duplicate["commandResult"]["code"], "media-input-sequence-mismatch")
        self.assertTrue(frame(1)["commandResult"]["accepted"])
        self.assertTrue(frame(2)["commandResult"]["accepted"])
        overflow = frame(3)
        self.assertFalse(overflow["commandResult"]["accepted"])
        self.assertEqual(overflow["commandResult"]["code"], "media-input-overflow")
        self.assertEqual(frame(3, "0")["commandResult"]["code"], "media-rejected")
        self.assertFalse(self.wire.result(request, 204)["commandResult"]["accepted"])
        self.wire.output("failed")
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)

    def test_vad_retires_inflight_pcm_without_mutation_and_new_gate_starts_at_zero(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream(opening=False)
        input_id = str(uuid.uuid4())
        self.wire.event({"type": "listening", "inputId": input_id})
        first, _ = self.wire.output("listening")
        pcm = bytes(4800)

        def frame(input_epoch, sequence, payload=pcm):
            return self.wire.command(f"MEDIA_FRAME {self.wire.session} {first['epoch']} {input_epoch} {sequence} pcm {len(payload)}", payload)

        self.assertTrue(frame(first["inputEpoch"], 0)["commandResult"]["accepted"])
        request, _ = self.wire.service("realtime-input")
        self.wire.result(request, 204)
        self.wire.event({"type": "speech_stopped", "inputId": input_id})
        closed, _ = self.wire.output("thinking")
        self.assertGreater(int(closed["inputEpoch"]), int(first["inputEpoch"]))
        self.assertFalse(self.wire.state["inputEnabled"])
        before = dict(self.wire.state)
        for input_epoch in (first["inputEpoch"], closed["inputEpoch"]):
            retired = frame(input_epoch, 1)
            self.assertFalse(retired["commandResult"]["accepted"])
            self.assertEqual(retired["commandResult"]["code"], "media-input-retired")
            self.assertEqual(self.wire.state, before)
        self.assertEqual(frame(closed["inputEpoch"], 1, b"x")["commandResult"]["code"], "media-rejected")
        self.assertEqual(frame(str(int(closed["inputEpoch"]) + 1), 0)["commandResult"]["code"], "media-rejected")
        self.assertEqual(self.wire.state, before)
        next_input = str(uuid.uuid4())
        self.wire.event({"type": "listening", "inputId": next_input})
        resumed, _ = self.wire.output("listening")
        self.assertTrue(self.wire.state["inputEnabled"])
        self.assertGreater(int(resumed["inputEpoch"]), int(closed["inputEpoch"]))
        wrong = frame(resumed["inputEpoch"], 1)
        self.assertFalse(wrong["commandResult"]["accepted"])
        self.assertEqual(wrong["commandResult"]["code"], "media-input-sequence-mismatch")
        self.assertTrue(self.wire.state["active"])
        self.assertNotIn("error", self.wire.state)
        self.assertEqual(frame(first["inputEpoch"], 99)["commandResult"]["code"], "media-input-retired")
        self.assertTrue(frame(resumed["inputEpoch"], 0)["commandResult"]["accepted"])
        request, body = self.wire.service("realtime-input")
        self.assertEqual(request["headers"], {"Input-Id": next_input, "Input-Sequence": "0"})
        self.assertEqual(body, pcm)
        self.wire.result(request, 204)
        self.assertEqual(self.wire.snapshot["reported"]["slot"], 0)
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")
        self.assertEqual(frame(resumed["inputEpoch"], 1)["commandResult"]["code"], "media-rejected")

    def test_valid_photo_uncertain_upload_closes_and_creates_without_replay(self):
        self.wire.start(photo=True)
        photo, _ = self.wire.output("photo_request")
        jpeg = bytes([255,216,255,219,0,3,1,255,196,0,3,1,255,192,0,11,8,0,2,0,2,1,1,17,0,
            255,218,0,8,1,1,0,0,63,0,1,255,217])
        for sequence, chunk in enumerate((jpeg[:20], jpeg[20:])):
            result = self.wire.command(f"MEDIA_FRAME {self.wire.session} {photo['epoch']} {photo['inputEpoch']} {sequence} jpeg {len(chunk)}", chunk)
            self.assertTrue(result["commandResult"]["accepted"])
        upload, body = self.wire.service("image-upload")
        self.assertEqual(body, jpeg)
        self.assertEqual(upload["headers"]["Boot-Id"], self.wire.boot)
        self.wire.result(upload, 0)
        delete, _ = self.wire.service("conversation-delete")
        self.wire.result(delete, 204)
        fresh, body = self.wire.service("conversation-create")
        self.assertFalse(json.loads(body)["consent"]["image"])
        self.assertNotEqual(fresh["idempotencyKey"], self.wire.request)
        self.wire.conversation = str(uuid.uuid4())
        self.wire.result(fresh, 201, {"conversationId": self.wire.conversation, "expiresAt": "2099-01-01T00:00:00Z",
            "limits": {"maxTurns": 10, "maxTextCharacters": 1000, "idleSeconds": 900, "maxLifetimeSeconds": 1800}})
        self.wire.stream()
        self.assertEqual(self.wire.state["photoWarning"], "photo_failed")
        self.assertFalse(any(header["kind"] == "image-upload" for header, _ in self.wire.services))

    def test_photo_denial_is_failure_not_fake_jpeg_or_audio(self):
        self.wire.start(photo=True)
        photo, _ = self.wire.output("photo_request")
        result = self.wire.command(f"MEDIA_FRAME {self.wire.session} {photo['epoch']} {photo['inputEpoch']} 0 jpeg 0", b"")
        self.assertTrue(result["commandResult"]["accepted"])
        delete, _ = self.wire.service("conversation-delete")
        self.wire.result(delete, 204)
        fresh, body = self.wire.service("conversation-create")
        self.assertFalse(json.loads(body)["consent"]["image"])
        self.assertFalse(any(header["kind"] == "image-upload" for header, _ in self.wire.services))
        self.wire.command("STOP")

    def test_proposal_requires_drain_ack_and_closed_before_owner_admission(self):
        self.wire.start()
        request = self.wire.stream()
        correlation = self.wire.correlation
        proposal = {"proposalId": str(uuid.uuid4()), "conversationId": self.wire.conversation,
            "turnId": correlation["clientTurnId"], "catalogVersion": request["catalogVersion"],
            "deviceRevision": correlation["deviceRevision"], "compartment": 3, "reason": "Offline fixture",
            "bootId": self.wire.boot, "generation": correlation["generation"],
            "expiresAt": (datetime.now(timezone.utc) + timedelta(seconds=30)).strftime("%Y-%m-%dT%H:%M:%SZ")}
        drain, _ = self.wire.audio_complete(proposal)
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)
        self.wire.ack(drain)
        played, _ = self.wire.service("realtime-played")
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)
        self.wire.result(played, 204)
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)
        self.wire.event({"type": "closed", "reason": "conversation_terminal"})
        self.wire.output("completed")
        self.wire.command("SNAPSHOT")
        self.assertEqual(self.wire.snapshot["reported"]["slot"], 3)
        self.assertEqual(self.wire.snapshot["trace"]["state"], "running")
        # Media cancellation cannot become a physical Stop after handoff.
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")
        self.assertFalse(self.wire.snapshot["reported"]["needsRecovery"])

    def test_gpt_pause_refreshes_hal_gate_without_resetting_engine_input_turn(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        input_id = str(uuid.uuid4())
        self.wire.event({"type": "listening", "inputId": input_id})
        initial, _ = self.wire.output("listening")
        pcm = bytes(4800)

        def frame(gate):
            return self.wire.command(f"MEDIA_FRAME {self.wire.session} {gate['epoch']} {gate['inputEpoch']} 0 pcm {len(pcm)}", pcm)

        self.assertTrue(frame(initial)["commandResult"]["accepted"])
        request, _ = self.wire.service("realtime-input")
        self.assertEqual(request["headers"]["Input-Sequence"], "0")
        self.wire.result(request, 204)
        self.wire.event({"type": "audio", "delta": "AAAAAAAA"})
        self.wire.output("pcm")
        self.wire.event({"type": "pause"})
        drain, _ = self.wire.output("drain")
        self.assertTrue(self.wire.ack(drain)["commandResult"]["accepted"])
        refreshed, _ = self.wire.output("listening")
        self.assertGreater(int(refreshed["inputEpoch"]), int(initial["inputEpoch"]))
        self.assertTrue(refreshed["inputEnabled"])
        self.assertTrue(frame(refreshed)["commandResult"]["accepted"])
        request, body = self.wire.service("realtime-input")
        self.assertEqual(request["headers"], {"Input-Id": input_id, "Input-Sequence": "1"})
        self.assertEqual(body, pcm)
        self.wire.result(request, 204)
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")
        self.assertFalse(self.wire.snapshot["reported"]["needsRecovery"])

    def test_stop_does_not_admit_already_queued_terminal_completion(self):
        self.wire.start()
        request = self.wire.stream()
        correlation = self.wire.correlation
        proposal = {"proposalId": str(uuid.uuid4()), "conversationId": self.wire.conversation,
            "turnId": correlation["clientTurnId"], "catalogVersion": request["catalogVersion"],
            "deviceRevision": correlation["deviceRevision"], "compartment": 2, "reason": "Offline fixture",
            "bootId": self.wire.boot, "generation": correlation["generation"],
            "expiresAt": (datetime.now(timezone.utc) + timedelta(seconds=30)).strftime("%Y-%m-%dT%H:%M:%SZ")}
        drain, _ = self.wire.audio_complete(proposal)
        self.wire.ack(drain)
        played, _ = self.wire.service("realtime-played")
        self.wire.result(played, 204)
        self.wire.event({"type": "closed", "reason": "conversation_terminal"})
        # Let the independent worker queue its completion; do not issue a poll
        # that gives the owner an opportunity to admit it before this Stop.
        time.sleep(.05)
        stopped = self.wire.command("STOP")
        self.assertTrue(stopped["reported"]["needsRecovery"])
        self.assertEqual(stopped["reported"]["slot"], 0)
        self.assertNotEqual(stopped["trace"]["state"], "running")

    def test_next_session_closes_retained_proposal_conversation_before_create(self):
        self.wire.start()
        request = self.wire.stream()
        correlation = self.wire.correlation
        previous_conversation = self.wire.conversation
        proposal = {"proposalId": str(uuid.uuid4()), "conversationId": previous_conversation,
            "turnId": correlation["clientTurnId"], "catalogVersion": request["catalogVersion"],
            "deviceRevision": correlation["deviceRevision"], "compartment": 4, "reason": "Offline fixture",
            "bootId": self.wire.boot, "generation": correlation["generation"],
            "expiresAt": (datetime.now(timezone.utc) + timedelta(seconds=30)).strftime("%Y-%m-%dT%H:%M:%SZ")}
        drain, _ = self.wire.audio_complete(proposal)
        self.wire.ack(drain)
        played, _ = self.wire.service("realtime-played")
        self.wire.result(played, 204)
        self.wire.event({"type": "closed", "reason": "conversation_terminal"})
        self.wire.output("completed")
        self.wire.command("TICK 12000")
        self.wire.session = str(uuid.uuid4())
        request_id = str(uuid.uuid4())
        started = self.wire.command(f"MEDIA_START {self.wire.session} {request_id} realtime cedar 1.15 false short 1 1 0")
        self.assertTrue(started["commandResult"]["accepted"])
        delete, _ = self.wire.service("conversation-delete")
        self.assertEqual(delete["path"], f"/v1/conversations/{previous_conversation}")
        self.assertFalse(any(header["kind"] == "conversation-create" for header, _ in self.wire.services))
        self.wire.result(delete, 404)
        create, body = self.wire.service("conversation-create")
        self.assertEqual(create["idempotencyKey"], request_id)
        self.assertEqual(json.loads(body)["bootId"], self.wire.boot)
        self.wire.command(f"MEDIA_CANCEL {self.wire.session}")

    def test_gpt_checkpoint_and_cancel_use_actual_browser_drain_epoch(self):
        self.wire.start(engine="gpt-live")
        self.wire.stream()
        self.wire.event({"type": "audio", "delta": "AAAAAAAA"})
        self.wire.output("pcm")
        playback_id = str(uuid.uuid4())
        self.wire.event({"type": "playback_checkpoint", "playbackId": playback_id, "pcmBytes": 6,
            "decisionRevision": 1, "responseGeneration": 0})
        drain, _ = self.wire.output("drain")
        self.wire.ack(drain)
        request, body = self.wire.service("realtime-playback")
        self.assertEqual(json.loads(body), {"playbackId": playback_id, "pcmBytes": 6, "drained": True})
        self.wire.result(request, 204)
        self.wire.event({"type": "audio_cancel", "responseGeneration": 1, "visualRevision": 3})
        cancelled, _ = self.wire.output("audio_cancel")
        self.assertNotEqual(cancelled["outputEpoch"], drain["outputEpoch"])
        self.assertEqual(self.wire.snapshot["reported"]["audio"], "browser-playback-cancelled")
        self.assertFalse(cancelled["inputEnabled"])
        self.assertFalse(self.wire.ack(drain)["commandResult"]["accepted"])
        self.wire.command("STOP")

    def test_wrong_completed_boot_never_drains_or_admits(self):
        self.wire.start()
        self.wire.stream()
        self.wire.event({"type": "audio", "delta": "AAAAAAAA"})
        self.wire.output("pcm")
        correlation = dict(self.wire.correlation, bootId=str(uuid.uuid4()))
        self.wire.event({"type": "completed", "operationId": self.wire.operation, "correlation": correlation,
            "result": {"reply": "No", "decision": "no_selection", "reason": "", "proposal": None}, "costMicroUsd": 100})
        delete, _ = self.wire.service("conversation-delete")
        self.wire.result(delete, 204)
        self.wire.output("failed")
        self.assertFalse(any(header["kind"] == "drain" for header, _ in self.wire.media))
        self.assertEqual(self.wire.snapshot["reported"]["slot"], 0)

    def test_invalid_binary_framing_exits_without_interpreting_payload(self):
        def run(payload):
            return subprocess.run([str(EXECUTABLE), "--instance", "framing-test", "--boot", str(uuid.uuid4()),
                "--journal", str(Path(self.temp.name) / (str(uuid.uuid4()) + ".bin")), "--build", "media-test"],
                input=payload, capture_output=True, timeout=5)
        session = str(uuid.uuid4())
        for payload in (
            f"MEDIA_EVENT {session} 1 65537\nSTOP\n".encode(),
            f"MEDIA_FRAME {session} 1 0 0 jpeg 32769\nSTOP\n".encode(),
            f"MEDIA_EVENT {session} 1 4\nSTOP".encode(),
        ):
            result = run(payload)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            self.assertIn(b"media", result.stderr)

    def test_hardware_availability_and_identity_validation(self):
        session, request = str(uuid.uuid4()), str(uuid.uuid4())
        result = self.wire.command(f"MEDIA_START {session} {request} realtime cedar 1 false main 0 1 0")
        self.assertFalse(result["commandResult"]["accepted"])
        result = self.wire.command(f"MEDIA_START {session} {request} gpt-live coral 1.2 false main 1 1 0")
        self.assertFalse(result["commandResult"]["accepted"])
        self.assertEqual(self.wire.snapshot["reported"]["readyMask"], 63)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", type=Path, required=True)
    args, remaining = parser.parse_known_args()
    EXECUTABLE = args.executable.resolve()
    unittest.main(argv=[__file__, *remaining], verbosity=2)
