"""One explicitly approved, bounded synthetic hands-free conversation."""

import array
import base64
import json
import os
import queue
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import wave
from pathlib import Path
from uuid import uuid4

from backend_api_client import ApiClient, DialogueError, check, issue_token, validate_base_url
from engine_capabilities import validate_engine_capabilities


ANSWERS = (
    "Jsem Karel. Dneska slavím narozeniny a přišel jsem s manželkou.",
    "Manželka by řekla, že jsem král výmluv. Včera jsem tvrdil, že zácpa trvala dvě hodiny.",
    "Ve skutečnosti jsem se zakecal v hospodě. Nejvíc mě práskne kamarád Petr.",
    "Petr si pak bez mého dovolení objedná další pivo. Vyber mi už nějakou hůlku, prosím.",
)
SAFE_CODES = {
    "cost_limit", "turn_limit", "dialogue_busy", "realtime_unavailable", "ai_unavailable",
    "operation_cancelled", "conversation_not_found", "conversation_finished", "invalid_request",
}


def audio_fixture(text, directory):
    path = Path(directory) / "visitor.wav"
    subprocess.run(["espeak-ng", "-v", "cs", "-s", "160", "-w", str(path), text],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15)
    with wave.open(str(path), "rb") as source:
        check(source.getnchannels() == 1 and source.getsampwidth() == 2, "invalid_fixture")
        rate = source.getframerate()
        samples = array.array("h", source.readframes(source.getnframes()))
    converted = array.array("h")
    for index in range(int(len(samples) * 24_000 / rate)):
        position = index * rate / 24_000
        left = int(position)
        fraction = position - left
        right = min(left + 1, len(samples) - 1)
        converted.append(round(samples[left] * (1 - fraction) + samples[right] * fraction))
    pcm = bytes(4800) + converted.tobytes() + bytes(48_000)
    check(len(pcm) <= 1_440_000, "fixture_too_long")
    return pcm


def safe_open(client, request, timeout=60):
    try:
        return client.opener.open(request, timeout=timeout)
    except urllib.error.HTTPError as error:
        try:
            value = json.loads(error.read(4097))
            code = value.get("code")
        except (ValueError, AttributeError):
            code = None
        raise DialogueError(code if code in SAFE_CODES else f"http_status_{error.code}") from error


def read_stream(response, events):
    try:
        wire = 0
        for _ in range(20_000):
            line = response.readline(65_537)
            if not line:
                events.put({"type": "eof"}, timeout=5)
                return
            wire += len(line)
            check(len(line) <= 65_536 and line.endswith(b"\n") and wire <= 20_000_000, "invalid_stream_bounds")
            value = json.loads(line)
            value["_receivedAt"] = time.monotonic()
            events.put(value, timeout=5)
        raise DialogueError("too_many_events")
    except Exception:
        events.put({"type": "reader_failed"}, timeout=5)


def run():
    report = {"kind": "synthetic_hands_free", "spendingAuthority": "openai_project",
              "model": "gpt-realtime-2.1", "turns": [], "completed": False}
    output = Path(os.environ.get("REPORT_PATH", "realtime-live-report.json"))
    client = ApiClient(validate_base_url(os.environ["PUBLIC_IPV4"]))
    token = issue_token(client, os.environ["DEV_DEVICE_CREDENTIAL"], os.environ["DEVICE_ID"])
    conversation = None
    previous_evening = None
    test_evening = None
    response = None
    try:
        _, modes = client.call("GET", "/v1/diagnostics", token=token)
        validate_engine_capabilities(modes["engines"], require_ready=True)
        _, evenings = client.call("GET", "/v1/evenings", token=token)
        previous_evening = evenings["activeEveningId"]
        _, created_evening = client.call("POST", "/v1/evenings", token=token, key=str(uuid4()), body={
            "name": f"Realtime synthetic {os.environ.get('GITHUB_RUN_ID', 'manual')}",
            "expectedEveningId": previous_evening,
        })
        test_evening = created_evening["activeEveningId"]
        boot = str(uuid4())
        _, created = client.call("POST", "/v1/conversations", token=token, key=str(uuid4()), body={
            "deviceId": os.environ["DEVICE_ID"], "bootId": boot, "generation": 0,
            "catalogVersion": "ollivander-private-party-v1", "locale": "cs-CZ",
            "consent": {"audio": True, "image": False},
        })
        conversation = created["conversationId"]
        opening = str(uuid4())
        start = {"clientTurnId": opening, "bootId": boot, "generation": 0, "readyMask": 63,
                 "deviceRevision": 0, "catalogVersion": "ollivander-private-party-v1", "opening": True,
                 "text": "", "wav": None, "voice": "cedar", "speed": 1.1}
        path = f"/v1/conversations/{conversation}/realtime-live"
        started_at = time.monotonic()
        response = safe_open(client, urllib.request.Request(client.base_url + path,
            data=json.dumps(start).encode(), method="POST", headers={"Authorization": f"Bearer {token}",
            "Idempotency-Key": str(uuid4()), "Content-Type": "application/json", "Accept": "application/x-ndjson"}))
        check(response.status == 200 and response.headers.get_content_type() == "application/x-ndjson", "invalid_stream_response")
        events = queue.Queue(maxsize=64)
        threading.Thread(target=read_stream, args=(response, events), daemon=True).start()
        stream_id = None
        first_audio = None
        audio_bytes = 0
        answers = 0
        charged = 0
        transcripts = []
        deadline = time.monotonic() + 240
        with tempfile.TemporaryDirectory(prefix="kufr-realtime-live-") as directory:
            fixtures = [audio_fixture(text, directory) for text in ANSWERS]
            while time.monotonic() < deadline:
                try:
                    event = events.get(timeout=50)
                except queue.Empty as error:
                    raise DialogueError("live_event_timeout") from error
                kind = event.get("type")
                received_at = event.get("_receivedAt", time.monotonic())
                if kind == "session":
                    check(stream_id is None and event["audioFormat"] == "pcm16-mono-24000", "invalid_session")
                    stream_id = event["streamId"]
                elif kind == "listening":
                    check(answers < len(fixtures), "unexpected_extra_input")
                    pcm = fixtures[answers]
                    answers += 1
                    upload_started = time.monotonic()
                    for sequence, offset in enumerate(range(0, len(pcm), 9600)):
                        frame = pcm[offset:offset + 9600]
                        request = urllib.request.Request(client.base_url + f"{path}/{stream_id}/audio", data=frame,
                            method="POST", headers={"Authorization": f"Bearer {token}", "Content-Type": "application/octet-stream",
                            "Input-Id": event["inputId"], "Input-Sequence": str(sequence)})
                        with safe_open(client, request, timeout=10) as uploaded:
                            check(uploaded.status == 204 and uploaded.read(1) == b"", "audio_upload_failed")
                        remaining = upload_started + (offset + len(frame)) / 48_000 - time.monotonic()
                        if remaining > 0:
                            time.sleep(remaining)
                elif kind == "speech_stopped":
                    started_at = received_at
                    first_audio = None
                    audio_bytes = 0
                elif kind == "visitor":
                    transcript = event.get("text")
                    check(isinstance(transcript, str) and 0 < len(transcript) <= 1000, "invalid_visitor_transcript")
                    transcripts.append(transcript)
                elif kind == "started":
                    check(event["correlation"]["deviceId"] == os.environ["DEVICE_ID"]
                          and event["correlation"]["conversationId"] == conversation
                          and event["correlation"]["bootId"] == boot, "invalid_correlation")
                elif kind == "audio":
                    if first_audio is None:
                        first_audio = received_at
                    pcm = base64.b64decode(event["delta"], validate=True)
                    check(0 < len(pcm) <= 32_768 and len(pcm) % 2 == 0, "invalid_audio")
                    audio_bytes += len(pcm)
                    check(audio_bytes <= 1_440_000, "audio_too_long")
                elif kind == "completed":
                    cost = event["costMicroUsd"]
                    check(type(cost) is int and cost > 0 and first_audio is not None, "missing_cost_or_audio")
                    charged += cost
                    report["conservativeTotalUsd"] = charged / 1_000_000
                    result = event["result"]
                    report["turns"].append({"reply": result["reply"], "decision": result["decision"],
                        "visitor": transcripts[-1] if transcripts else None,
                        "firstAudioMs": round((first_audio - started_at) * 1000),
                        "totalMs": round((received_at - started_at) * 1000),
                        "pcmBytes": audio_bytes, "conservativeCostUsd": cost / 1_000_000})
                    if len(report["turns"]) >= len(ANSWERS) + 1 or result["decision"] != "ask":
                        check(len(report["turns"]) >= 3, "conversation_ended_early")
                        check(len(transcripts) >= 2, "visitor_transcripts_missing")
                        report["completed"] = True
                        report["conservativeTotalUsd"] = charged / 1_000_000
                        break
                    client.call("POST", f"{path}/{stream_id}/played", token=token,
                                body={"operationId": event["operationId"]})
                    started_at = time.monotonic()
                    first_audio = None
                    audio_bytes = 0
                elif kind == "failed":
                    code = event.get("code")
                    raise DialogueError(code if code in SAFE_CODES else "live_provider_failed")
                elif kind in ("eof", "reader_failed", "closed"):
                    raise DialogueError("live_stream_ended_early")
        check(report["completed"], "live_deadline")
    except DialogueError as error:
        report["failure"] = str(error)
        raise
    except Exception:
        report["failure"] = "bounded_test_error"
        raise
    finally:
        cleanup_failed = False
        try:
            if conversation is not None:
                client.call("DELETE", f"/v1/conversations/{conversation}", token=token, key=str(uuid4()))
        except Exception:
            cleanup_failed = True
        finally:
            if response is not None:
                response.close()
            try:
                if previous_evening is not None and test_evening is not None:
                    client.call("PUT", f"/v1/evenings/{previous_evening}/active", token=token, key=str(uuid4()),
                                body={"expectedEveningId": test_evening})
            except Exception:
                cleanup_failed = True
            if cleanup_failed:
                report["cleanupFailure"] = "cleanup_failed"
            output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        check(not cleanup_failed, "cleanup_failed")
    print("Synthetic hands-free Realtime test completed; report saved.")


if __name__ == "__main__":
    try:
        run()
    except DialogueError as error:
        raise SystemExit(f"Realtime test failed: {error}") from None
    except Exception:
        raise SystemExit("Realtime test failed: bounded_test_error") from None
