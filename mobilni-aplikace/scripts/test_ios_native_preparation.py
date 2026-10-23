import contextlib
import io
import plistlib
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPTS = Path(__file__).parent


def inline_python(script, invocation):
    source = (SCRIPTS / script).read_text(encoding="utf-8")
    return source.split(invocation + " <<'PY'\n", 1)[1].split("\nPY\n", 1)[0]


ENTITLEMENTS = inline_python(
    "build-ios-ipa.sh",
    '  python3 - "${work_dir}/signed-entitlements.plist" "${IOS_TEAM_ID}.${IOS_BUNDLE_ID}"',
)
CAPTURE = inline_python(
    "capture-ios-store-preview.sh",
    '  python3 - "$device" "$app_pid" "$output_dir/$slot-home-native.png" "$work_dir/check-ios-home-render"',
)


class SignedEntitlementTests(unittest.TestCase):
    def validate(self, contents):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "entitlements.plist"
            path.write_bytes(contents)
            with patch.object(sys, "argv", ["-", str(path), "TEAM.bundle"]):
                exec(compile(ENTITLEMENTS, "signed-entitlements", "exec"), {})

    def test_codesign_requests_machine_readable_xml(self):
        source = (SCRIPTS / "build-ios-ipa.sh").read_text(encoding="utf-8")
        self.assertIn('codesign --display --entitlements - --xml "${app_path}"', source)
        self.assertIn('codesign --verify --deep --strict --verbose=2 "${app_path}"', source)

    def test_distribution_xml_passes(self):
        self.validate(plistlib.dumps({"application-identifier": "TEAM.bundle", "get-task-allow": False}))

    def test_debug_or_wrong_identifier_fails(self):
        for values in (
            {"application-identifier": "TEAM.bundle", "get-task-allow": True},
            {"application-identifier": "OTHER.bundle", "get-task-allow": False},
            {"application-identifier": "TEAM.bundle", "get-task-allow": 0},
            {},
        ):
            with self.subTest(values=values), self.assertRaises(SystemExit):
                self.validate(plistlib.dumps(values))

    def test_human_readable_output_is_rejected(self):
        with self.assertRaises(plistlib.InvalidFileException):
            self.validate(b'[Dict]\n\t[Key] application-identifier\n\t[Value] TEAM.bundle\n')


class NativeScreenshotTests(unittest.TestCase):
    def run_capture(
        self, directory, outcomes, alive=None, trace=None,
        render_outcomes=("ready",), capture_seconds=0,
    ):
        output = Path(directory) / "native.png"
        now = [0.0]
        attempts = []
        render_attempts = []
        if trace is not None:
            trace.update(now=now, attempts=attempts, render_attempts=render_attempts)

        def run(command, *, check, timeout):
            self.assertTrue(check)
            if command[0] == "render-check":
                self.assertEqual(["render-check", str(output)], command)
                self.assertTrue(output.exists())
                render_attempts.append(timeout)
                outcome = render_outcomes[min(len(render_attempts) - 1, len(render_outcomes) - 1)]
                if outcome == "timeout":
                    now[0] += timeout
                    raise subprocess.TimeoutExpired(command, timeout)
                if outcome == "unready":
                    raise subprocess.CalledProcessError(1, command)
                return
            self.assertEqual(["xcrun", "simctl", "io", "SIMULATOR", "screenshot", "--type=png", str(output)], command)
            self.assertFalse(output.exists(), "A previous partial capture must be removed before retry")
            attempts.append(timeout)
            outcome = outcomes[min(len(attempts) - 1, len(outcomes) - 1)]
            output.write_bytes(b"native output" if outcome == "success" else b"partial")
            if outcome == "timeout":
                now[0] += timeout
                raise subprocess.TimeoutExpired(command, timeout)
            if outcome == "error":
                raise subprocess.CalledProcessError(1, command)
            now[0] += capture_seconds

        def sleep(seconds):
            now[0] += seconds

        with patch.object(sys, "argv", ["-", "SIMULATOR", "123", str(output), "render-check"]), \
                patch("subprocess.run", side_effect=run), \
                patch("time.monotonic", side_effect=lambda: now[0]), \
                patch("time.sleep", side_effect=sleep), \
                patch("os.kill", side_effect=alive) as live_check, \
                contextlib.redirect_stderr(io.StringIO()):
            exec(compile(CAPTURE, "native-screenshot", "exec"), {})
        return output, attempts, now[0], live_check.call_count

    def test_cold_service_retries_timeout_and_command_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            output, attempts, elapsed, checks = self.run_capture(directory, ["timeout", "error", "success"])
            self.assertEqual(b"native output", output.read_bytes())
            self.assertEqual([60, 60, 60], attempts)
            self.assertEqual(70, elapsed)
            self.assertEqual(5, checks)

    def test_service_deadline_fails_and_removes_partial_png(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = {}
            with self.assertRaisesRegex(SystemExit, "within 180 seconds"):
                self.run_capture(directory, ["timeout"], trace=trace)
            self.assertEqual([60, 60, 50], trace["attempts"])
            self.assertEqual([180], trace["now"])
            self.assertFalse((Path(directory) / "native.png").exists())

    def test_dead_app_prevents_capture_and_invalidates_completed_capture(self):
        for alive in ([ProcessLookupError()], [None, ProcessLookupError()]):
            with self.subTest(alive=alive), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ProcessLookupError):
                    self.run_capture(directory, ["success"], alive=alive)

    def test_unrendered_capture_is_removed_and_retried_until_heading_ready(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = {}
            output, attempts, elapsed, _ = self.run_capture(
                directory, ["success"], trace=trace, render_outcomes=["unready", "ready"],
            )
            self.assertEqual(b"native output", output.read_bytes())
            self.assertEqual([60, 60], attempts)
            self.assertEqual([15, 15], trace["render_attempts"])
            self.assertEqual(5, elapsed)

    def test_ocr_timeout_uses_same_deadline_and_leaves_no_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = {}
            with self.assertRaisesRegex(SystemExit, "within 180 seconds"):
                self.run_capture(directory, ["success"], trace=trace, render_outcomes=["timeout"])
            self.assertEqual([180], trace["now"])
            self.assertEqual([15] * 9, trace["render_attempts"])
            self.assertFalse((Path(directory) / "native.png").exists())

    def test_ocr_timeout_is_capped_by_remaining_capture_budget(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = {}
            with self.assertRaisesRegex(SystemExit, "within 180 seconds"):
                self.run_capture(
                    directory, ["timeout", "timeout", "success"], trace=trace,
                    render_outcomes=["timeout"], capture_seconds=45,
                )
            self.assertEqual([60, 60, 50], trace["attempts"])
            self.assertEqual([5], trace["render_attempts"])
            self.assertEqual([180], trace["now"])
            self.assertFalse((Path(directory) / "native.png").exists())


if __name__ == "__main__":
    unittest.main()
