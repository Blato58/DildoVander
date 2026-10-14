#!/usr/bin/env python3
"""Native twin protocol, durable journal and evidence-boundary acceptance."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid

from build_host import build

EXECUTABLE = None
FIXTURE = None


class Client:
    def __init__(self, path, boot=None, cad=None):
        arguments = [
            str(EXECUTABLE), "--instance", "native-test", "--boot", boot or str(uuid.uuid4()),
            "--journal", str(path), "--build", "test-build",
        ]
        if cad is not None:
            arguments += ["--cad", cad]
        self.process = subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8")
        self.sequence = 0
        # A spawned process has not necessarily acquired its journal lock yet.
        # Complete the protocol handshake before any concurrent-owner check.
        self.command("SNAPSHOT")

    def command(self, command):
        self.process.stdin.write(command + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise AssertionError(self.process.stderr.read())
        document = json.loads(line)
        assert int(document["sequence"]) == self.sequence + 1
        self.sequence += 1
        assert len(document["reported"]["servo"]) == 6
        assert len(document["reported"]["ledGrb"]) == 108
        assert document["commandResult"]["orderingHealthy"]
        if document["model"] is not None:
            assert document["model"]["evidence"] == "simulated"
        assert document["reported"]["physicalVerification"] == "unavailable"
        return document

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=10)
        stderr = self.process.stderr.read()
        self.process.stdout.close()
        self.process.stderr.close()
        assert self.process.returncode == 0, stderr


class HostTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="kufr-native-test-", ignore_cleanup_errors=True)
        self.path = Path(self.temp.name) / "journal.bin"
        self.client = Client(self.path)

    def tearDown(self):
        self.client.close()
        self.temp.cleanup()

    def inspect(self):
        document = self.client.command("INSPECT")
        self.assertTrue(document["commandResult"]["accepted"])
        self.assertFalse(document["reported"]["needsRecovery"])
        self.assertEqual(document["reported"]["readyMask"], 63)
        return document

    def start(self, slot=3, variant="main", operation=None):
        operation = operation or str(uuid.uuid4())
        document = self.client.command(f"TEXT {operation} {slot} {variant}")
        self.assertTrue(document["commandResult"]["accepted"], document["commandResult"])
        self.assertEqual(document["trace"]["variant"], variant)
        self.assertFalse(document["reported"]["needsRecovery"])
        return operation, document

    def test_all_slots_variants(self):
        self.inspect()
        now = 0
        for variant in ("main", "short"):
            for slot in range(1, 7):
                operation, _ = self.start(slot, variant)
                now += 12000
                document = self.client.command(f"TICK {now}")
                self.assertEqual(document["trace"]["state"], "completed")
                self.assertEqual(document["trace"]["operationId"], operation)
                self.assertEqual(document["reported"]["state"], "idle")
                self.assertEqual(document["reported"]["readyMask"], 63)
                self.assertFalse(document["reported"]["outputsEnabled"])
                self.assertEqual(document["reported"]["vibrationMask"], 0)
                self.assertTrue(all(s["position"] == 0 for s in document["model"]["slots"]))
                self.assertTrue(all(s["estimate"] == "rest" for s in document["reported"]["servo"]))
                if FIXTURE and slot == 3 and variant == "main":
                    FIXTURE.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")

    def test_stop_freezes_motion_and_requires_inspect(self):
        self.inspect()
        self.start()
        now = 0
        while True:
            now += 10
            document = self.client.command(f"TICK {now}")
            position = document["model"]["slots"][2]["position"]
            if 0 < position < 1:
                break
            self.assertLess(now, 7000)
        document = self.client.command("STOP")
        self.assertTrue(document["reported"]["needsRecovery"])
        self.assertEqual(document["reported"]["servo"][2]["estimate"], "unknown")
        document = self.client.command(f"TICK {now + 3000}")
        self.assertEqual(document["model"]["slots"][2]["position"], position)
        self.assertFalse(document["reported"]["outputsEnabled"])
        rejected = self.client.command(f"TEXT {uuid.uuid4()} 3 short")
        self.assertFalse(rejected["commandResult"]["accepted"])
        self.inspect()

    def test_jam_does_not_fabricate_feedback(self):
        self.inspect()
        self.client.command("FAULT jam 3")
        self.start()
        raised = self.client.command("TICK 6500")
        self.assertEqual(raised["reported"]["servo"][2]["estimate"], "raised")
        self.assertEqual(raised["model"]["slots"][2]["position"], 0)
        document = self.client.command("TICK 12000")
        self.assertEqual(document["trace"]["state"], "completed")
        self.assertTrue(document["model"]["slots"][2]["jammed"])
        self.assertEqual(document["reported"]["physicalVerification"], "unavailable")

    def test_duplicate_conflict_persist_across_restart(self):
        self.inspect()
        operation, _ = self.start()
        completed = self.client.command("TICK 12000")
        writes = completed["commandResult"]["servoWrites"]
        duplicate = self.client.command(f"TEXT {operation} 3 main")
        self.assertEqual(duplicate["commandResult"]["code"], "duplicate")
        self.assertEqual(duplicate["commandResult"]["servoWrites"], writes)
        conflict = self.client.command(f"TEXT {operation} 3 short")
        self.assertEqual(conflict["commandResult"]["code"], "operation-conflict")
        self.client.close()
        self.client = Client(self.path)
        self.assertTrue(self.client.command("SNAPSHOT")["reported"]["needsRecovery"])
        self.inspect()
        self.assertEqual(self.client.command(f"TEXT {operation} 3 main")["commandResult"]["code"], "duplicate")

    def test_pending_restart_never_replays(self):
        self.inspect()
        operation, _ = self.start()
        before_restart = self.client.command("TICK 6500")
        self.assertEqual(before_restart["model"]["slots"][2]["position"], 1)
        self.client.close()
        self.client = Client(self.path)
        document = self.client.command("SNAPSHOT")
        self.assertTrue(document["reported"]["needsRecovery"])
        self.assertIsNone(document["model"])
        self.assertEqual(document["commandResult"]["servoWrites"], "0")
        self.assertFalse(self.client.command(f"TEXT {uuid.uuid4()} 2 main")["commandResult"]["accepted"])
        inspected = self.inspect()
        self.assertEqual(inspected["model"]["slots"][2]["position"], 0)
        self.assertEqual(self.client.command(f"TEXT {operation} 3 main")["commandResult"]["code"], "duplicate")

    def test_journal_failure_no_output_and_restart(self):
        self.inspect()
        self.client.command("FAULT journal 0")
        document = self.client.command(f"TEXT {uuid.uuid4()} 3 main")
        self.assertFalse(document["commandResult"]["accepted"])
        self.assertTrue(document["reported"]["needsRecovery"])
        self.assertEqual(document["commandResult"]["servoWrites"], "0")
        self.assertEqual(document["commandResult"]["vibrationWrites"], "0")
        # Admission failed before a scene existed; retain the untouched native
        # trace instead of inventing an accepted operation/fault event.
        self.assertEqual(document["trace"]["state"], "idle")
        self.client.close()
        self.client = Client(self.path)
        self.assertTrue(self.client.command("SNAPSHOT")["reported"]["needsRecovery"])
        self.inspect()
        self.start()

    def test_completion_journal_failure_retains_pending(self):
        self.inspect()
        operation, _ = self.start()
        self.client.command("TICK 6000")
        self.client.command("FAULT journal 0")
        document = self.client.command("TICK 12000")
        self.assertEqual(document["trace"]["state"], "fault")
        self.assertTrue(document["reported"]["needsRecovery"])
        self.client.close()
        self.client = Client(self.path)
        self.assertTrue(self.client.command("SNAPSHOT")["reported"]["needsRecovery"])
        self.inspect()
        self.assertEqual(self.client.command(f"TEXT {operation} 3 main")["commandResult"]["code"], "duplicate")

    def test_servo_led_faults_and_accepted_scaling(self):
        self.inspect()
        _, document = self.start()
        for now in range(100, 2100, 100):
            document = self.client.command(f"TICK {now}")
            led = document["reported"]["ledGrb"]
            self.assertLessEqual(max(led), 64)
            self.assertLessEqual(sum(any(led[i:i+18]) for i in range(0, 108, 18)), 1)
        self.client.command("FAULT servo 3")
        failed = self.client.command("TICK 12000")
        self.assertEqual(failed["trace"]["state"], "fault")
        self.assertTrue(failed["reported"]["needsRecovery"])
        self.client.command("FAULT none 0")
        self.inspect()
        self.client.command("FAULT led 0")
        failed = self.client.command(f"TEXT {uuid.uuid4()} 3 short")
        self.assertFalse(failed["commandResult"]["accepted"])
        self.assertTrue(failed["reported"]["needsRecovery"])

    def test_dance_synthetic_media_owner(self):
        self.inspect()
        document = self.client.command(f"DANCE {uuid.uuid4()}")
        self.assertTrue(document["commandResult"]["accepted"])
        self.assertEqual(document["reported"]["state"], "dance")
        self.assertEqual(document["reported"]["audio"], "simulated-track-enqueued")
        self.assertFalse(self.client.command(f"TEXT {uuid.uuid4()} 3 main")["commandResult"]["accepted"])
        document = self.client.command("TICK 8000")
        self.assertEqual(document["trace"]["state"], "completed")
        self.assertEqual(document["trace"]["audioPositionMs"], 8000)
        self.assertEqual(document["reported"]["audio"], "simulated-track-drained")
        self.assertEqual(document["commandResult"]["servoWrites"], "0")
        self.assertGreater(int(document["commandResult"]["vibrationWrites"]), 0)
        self.client.command(f"DANCE {uuid.uuid4()}")
        self.client.command("DANCE_STOP")
        document = self.client.command("TICK 8010")
        self.assertEqual(document["trace"]["state"], "cancelled")
        self.assertFalse(document["reported"]["outputsEnabled"])

    def test_bounded_protocol_and_stale_clock(self):
        for command in ("X" * 100000, "TICK -1", "TICK 01", "TICK 18446744073709551616",
                        "TICK 60001", "TEXT bad 2 short", "SNAPSHOT extra", "SPEECH hello"):
            self.assertFalse(self.client.command(command)["commandResult"]["accepted"])
        self.assertTrue(self.client.command("SNAPSHOT")["commandResult"]["accepted"])
        self.client.command("TICK 50")
        self.assertFalse(self.client.command("TICK 49")["commandResult"]["accepted"])

    def test_corrupt_journal_fails_closed(self):
        self.client.close()
        self.path.write_bytes(b"bad persistent journal")
        self.client = Client(self.path)
        self.assertFalse(self.client.command("INSPECT")["commandResult"]["accepted"])
        document = self.client.command(f"TEXT {uuid.uuid4()} 2 short")
        self.assertFalse(document["commandResult"]["accepted"])
        self.assertEqual(document["commandResult"]["servoWrites"], "0")

    def test_exclusive_storage_owner(self):
        duplicate = subprocess.run([str(EXECUTABLE), "--instance", "native-test", "--boot", "other",
            "--journal", str(self.path), "--build", "test"], input="SNAPSHOT\n", text=True,
            capture_output=True, timeout=10)
        self.assertNotEqual(duplicate.returncode, 0)
        self.assertIn("journal-lock-unavailable", duplicate.stderr)

    def test_optional_cad_hash_bound_metadata(self):
        self.assertIsNone(self.client.command("SNAPSHOT")["source"]["cadManifestRevision"])
        self.client.close()
        cad = "0123456789abcdef" * 4
        self.client = Client(self.path, cad=cad)
        for command in ("SNAPSHOT", "INSPECT", "TICK 50", "STOP"):
            self.assertEqual(self.client.command(command)["source"]["cadManifestRevision"], cad)
        for invalid in ("a" * 63, "A" * 64, "g" * 64):
            result = subprocess.run([str(EXECUTABLE), "--instance", "native-invalid", "--boot", "boot-invalid",
                "--journal", str(self.path), "--build", "test", "--cad", invalid],
                input="SNAPSHOT\n", text=True, capture_output=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("invalid-cad-hash", result.stderr)

    def prepare_integration(self, text='Which wand? "quoted" \\ newline\n', utc=1791936000):
        turn = str(uuid.uuid4())
        conversation = str(uuid.uuid4())
        document = self.client.command(f"INTEGRATION_PREPARE {turn} {conversation} {utc} {text.encode('utf-8').hex()}")
        self.assertTrue(document["commandResult"]["accepted"], document["commandResult"])
        request = document["externalRequest"]
        self.assertEqual(request["body"]["text"], text)
        self.assertEqual(request["body"]["clientTurnId"], turn)
        self.assertEqual(request["body"]["catalogVersion"], "ollivander-private-party-v1")
        self.assertEqual(request["body"]["deviceRevision"], int(document["revision"]))
        self.assertEqual(request["correlation"]["conversationId"], conversation)
        self.assertEqual(request["correlation"]["bootId"], document["source"]["bootId"])
        self.assertNotIn("externalRequest", self.client.command("SNAPSHOT"))
        operation = str(uuid.uuid4())
        self.assertTrue(self.client.command(f"INTEGRATION_BIND {operation}")["commandResult"]["accepted"])
        return operation, request["correlation"]

    @staticmethod
    def integration_result(operation, correlation, utc=1791936000, status="completed", decision="propose"):
        proposal = {
            "proposalId": str(uuid.uuid4()), "conversationId": correlation["conversationId"],
            "turnId": correlation["clientTurnId"], "catalogVersion": correlation["catalogVersion"],
            "deviceRevision": correlation["deviceRevision"], "compartment": 3,
            "reason": "Synthetic offline codec fixture", "bootId": correlation["bootId"],
            "generation": correlation["generation"],
            "expiresAt": datetime.fromtimestamp(utc + 30, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        }
        return {"operationId": operation, "kind": "turn", "status": status,
            "correlation": {key: value for key, value in correlation.items() if key != "catalogVersion"},
            "result": {"reply": "Synthetic response", "decision": decision, "reason": "",
                "proposal": proposal if decision == "propose" else None} if status == "completed" else None,
            "error": None}

    def submit_integration_result(self, response, utc=1791936000):
        encoded = json.dumps(response, separators=(",", ":")).encode("utf-8").hex()
        return self.client.command(f"INTEGRATION_RESULT {utc} {encoded}")

    def test_integration_real_codecs_proposal_path(self):
        self.inspect()
        operation, correlation = self.prepare_integration('Hello \u010carod\u011bj "quote" \\')
        response = self.integration_result(operation, correlation)
        admitted = self.submit_integration_result(response)
        self.assertTrue(admitted["commandResult"]["accepted"], admitted["commandResult"])
        self.assertEqual(admitted["trace"]["operationId"], response["result"]["proposal"]["proposalId"])
        self.assertEqual(admitted["commandResult"]["code"], "integration-presentation-admitted")
        self.assertNotIn("externalRequest", admitted)
        self.assertNotIn("Synthetic response", json.dumps(admitted))
        self.assertEqual(self.client.command("TICK 12000")["trace"]["state"], "completed")

    def test_integration_bind_busy_noncompleted_reply(self):
        self.inspect()
        operation, correlation = self.prepare_integration()
        busy = self.client.command(f"INTEGRATION_PREPARE {uuid.uuid4()} {uuid.uuid4()} 1791936000 6869")
        self.assertEqual(busy["commandResult"]["code"], "integration-busy")
        self.assertFalse(self.client.command(f"INTEGRATION_BIND {uuid.uuid4()}")["commandResult"]["accepted"])
        running = self.submit_integration_result(self.integration_result(operation, correlation, status="running"))
        self.assertEqual(running["commandResult"]["code"], "integration-not-completed")
        reply = self.submit_integration_result(self.integration_result(operation, correlation, decision="ask"))
        self.assertEqual(reply["commandResult"]["code"], "integration-reply-ready-no-audio")
        self.assertTrue(reply["commandResult"]["accepted"])
        self.assertEqual(reply["commandResult"]["servoWrites"], "0")
        self.assertFalse(self.submit_integration_result(self.integration_result(operation, correlation))["commandResult"]["accepted"])

    def test_integration_stale_stop_changed_snapshot(self):
        self.inspect()
        operation, correlation = self.prepare_integration()
        self.client.command("STOP")
        self.assertFalse(self.submit_integration_result(self.integration_result(operation, correlation))["commandResult"]["accepted"])
        self.inspect()
        operation, correlation = self.prepare_integration()
        response = self.integration_result(operation, correlation)
        self.client.command("INSPECT")
        self.assertFalse(self.submit_integration_result(response)["commandResult"]["accepted"])
        operation, correlation = self.prepare_integration()
        late = self.submit_integration_result(self.integration_result(operation, correlation), utc=1791936061)
        self.assertEqual(late["commandResult"]["code"], "integration-stale")
        self.assertEqual(late["commandResult"]["servoWrites"], "0")

    def test_integration_invalid_and_unrelated_response(self):
        self.inspect()
        invalid = self.client.command(f"INTEGRATION_PREPARE {uuid.uuid4()} {uuid.uuid4()} 1791936000 ff")
        self.assertFalse(invalid["commandResult"]["accepted"])
        operation, correlation = self.prepare_integration()
        bad = self.client.command("INTEGRATION_RESULT 1791936000 7b7d")
        self.assertEqual(bad["commandResult"]["code"], "integration-codec-rejected")
        response = self.integration_result(operation, correlation)
        response["operationId"] = str(uuid.uuid4())
        rejected = self.submit_integration_result(response)
        self.assertEqual(rejected["commandResult"]["code"], "integration-proposal-rejected")
        self.assertEqual(rejected["commandResult"]["servoWrites"], "0")
        for field in ("deviceId", "bootId", "clientTurnId", "deviceRevision", "generation"):
            operation, correlation = self.prepare_integration()
            response = self.integration_result(operation, correlation)
            response["correlation"][field] = str(uuid.uuid4()) if field.endswith("Id") else 999
            rejected = self.submit_integration_result(response)
            self.assertFalse(rejected["commandResult"]["accepted"])
            self.assertEqual(rejected["commandResult"]["servoWrites"], "0")

    def test_boot_pose_unknown_and_integration_cancel_no_actions(self):
        boot = self.client.command("SNAPSHOT")
        self.assertIsNone(boot["model"])
        self.assertGreater(int(boot["commandResult"]["lightWrites"]), 0)
        self.assertEqual(boot["reported"]["ledGrb"], [0] * 108)
        self.assertFalse(boot["reported"]["outputsEnabled"])
        self.assertTrue(all(servo["estimate"] == "unknown" for servo in boot["reported"]["servo"]))
        self.inspect()
        operation, correlation = self.prepare_integration()
        cancelled = self.client.command("INTEGRATION_CANCEL")
        self.assertTrue(cancelled["commandResult"]["accepted"])
        rejected = self.submit_integration_result(self.integration_result(operation, correlation))
        self.assertEqual(rejected["commandResult"]["code"], "integration-not-pending")
        operation, correlation = self.prepare_integration()
        admitted = self.submit_integration_result(self.integration_result(operation, correlation))
        self.assertTrue(admitted["commandResult"]["accepted"])
        before = self.client.command("TICK 6500")
        cancelled = self.client.command("INTEGRATION_CANCEL")
        self.assertEqual(cancelled["trace"]["state"], "running")
        self.assertEqual(cancelled["reported"], before["reported"])
        self.assertEqual(cancelled["model"], before["model"])
        self.assertEqual(cancelled["commandResult"]["servoWrites"], before["commandResult"]["servoWrites"])
        self.assertEqual(self.client.command("TICK 12000")["trace"]["state"], "completed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", type=Path)
    parser.add_argument("--fixture", type=Path)
    args = parser.parse_args()
    FIXTURE = args.fixture
    with tempfile.TemporaryDirectory(prefix="kufr-native-build-", ignore_cleanup_errors=True) as directory:
        EXECUTABLE = args.executable.resolve() if args.executable else build(Path(directory) / ("native-twin.exe" if os.name == "nt" else "native-twin"))
        variant = build(Path(directory) / ("variant-test.exe" if os.name == "nt" else "variant-test"), "variant_test.cpp")
        subprocess.run([str(variant)], check=True, timeout=10)
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(HostTests)
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        raise SystemExit(0 if result.wasSuccessful() else 1)
