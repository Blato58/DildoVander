#!/usr/bin/env python3
"""Bounded offline DV02 host checks; never talks to hardware or providers."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CORE = [
    "safety/SafetyController", "wand_selection/SelectionSequence",
    "device_control/DeviceControl", "device_control/DeviceControlRuntime",
    "conversation/ConversationRuntimeState", "conversation/PendingProposal",
    "wand_presentation/WandPresentation", "physical_effects/PhysicalEffects",
    "physical_effects/PhysicalPresentation", "physical_effects/PresentationDiagnostics",
    "scene_intent/SceneIntentBridge",
    "offline_show/OfflineShow",
    "offline_show/ImaAdpcm",
]


def compiler_environment():
    env = os.environ.copy()
    if shutil.which("g++"):
        return "g++", env, False
    if shutil.which("cl"):
        return "cl", env, True
    if os.name == "nt":
        vswhere = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
        if vswhere.exists():
            install = subprocess.check_output([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], text=True).strip()
            setup = Path(install) / "Common7/Tools/VsDevCmd.bat"
            if setup.exists():
                # Keep shell syntax in a private cmd file; no source/user text
                # is interpolated into a shell command.
                with tempfile.TemporaryDirectory(prefix="dv02-msvc-") as temp:
                    script = Path(temp) / "environment.cmd"
                    script.write_text(f'@echo off\ncall "{setup}" -arch=x64 >nul\nset\n', encoding="utf-8")
                    output = subprocess.check_output(["cmd.exe", "/c", str(script)], text=True)
                for line in output.splitlines():
                    if "=" in line and not line.startswith("="):
                        key, value = line.split("=", 1)
                        env[key] = value
                executable = shutil.which("cl", path=env.get("Path", env.get("PATH", "")))
                if executable:
                    return executable, env, True
    raise SystemExit("A C++14 host compiler (g++ or Visual Studio Build Tools) is required")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", type=Path, help="Write reproducible synthetic trace JSON into this directory")
    parser.add_argument("--only", action="append", help="Run only a named host check (repeatable)")
    parser.add_argument("--telemetry-fixture", type=Path, help="Export actual firmware Twin serializer output from fake I/O")
    args = parser.parse_args()
    compiler, env, msvc = compiler_environment()
    checks = [
        ("firmware_update_test", ["firmware_update/OtaManifest", "firmware_update/OtaPolicy"], []),
        ("espidf_firmware_update_test", ["firmware_update/OtaManifest", "firmware_update/OtaPolicy", "firmware_update/EspIdfFirmwareUpdate", "physical_effects/PhysicalEffects", "device_control/PersistentCommandJournal"], ["tests/ota_stubs"]),
        ("physical_effects_test", CORE, []),
        ("show_runtime_test", CORE + ["device_control/PersistentCommandJournal"], []),
        ("offline_show_test", CORE, []),
        ("device_show_cues_test", CORE + ["show_cues/DeviceShowCue", "show_cues/DeviceShowAudio", "show_cues/DeviceShowRuntime", "conversation/ConversationSession", "conversation/ConversationStartCodec"], []),
        ("autonomous_presentation_lifecycle_test", CORE, []),
        ("device_control_runtime_test", CORE, []),
        ("persistent_command_journal_test", ["safety/SafetyController", "wand_selection/SelectionSequence", "device_control/DeviceControl", "device_control/PersistentCommandJournal"], []),
        ("keepsake_durability_test", ["safety/SafetyController", "wand_selection/SelectionSequence", "device_control/DeviceControl", "device_control/DeviceControlRuntime", "device_control/PersistentCommandJournal", "conversation/ConversationRuntimeState", "conversation/PendingProposal", "wand_presentation/WandPresentation"], []),
        ("journal_epoch_test", ["device_control/PersistentCommandJournal"], []),
        ("operation_epoch_core_test", CORE + ["device_control/PersistentCommandJournal", "conversation/TurnOperationCodec", "conversation/TurnPollGuard", "conversation/ConversationStartCodec", "conversation/ConversationSession"], []),
        ("dance_coordinator_test", ["dance/DanceCoordinator", "physical_effects/PhysicalEffects"], []),
        ("show_trace_mailbox_test", ["physical_effects/PresentationDiagnostics"], []),
        ("twin_telemetry_test", CORE, []),
        ("voice_workflow_test", ["voice_turn/VoiceWorkflow", "conversation/ConversationStartCodec"], []),
        ("realtime_session_engine_test", ["voice_turn/RealtimeSessionEngine", "conversation/TurnPollGuard"], []),
        ("espidf_realtime_transport_test", ["voice_turn/EspIdfRealtimeTransport", "voice_turn/RealtimeSessionEngine", "voice_turn/RealtimeEventCodec", "conversation/TurnOperationCodec", "conversation/TurnPollGuard", "conversation/ConversationStartCodec", "conversation/ConversationSession", "lib/device_configuration/NetworkSettings"], ["tests/realtime_stubs", "lib/device_configuration/include"]),
        ("espidf_voice_audio_test", ["voice_turn/EspIdfVoiceAudio"], ["tests/audio_stubs"]),
        ("local_control_server_test", ["local_control/EspIdfLocalControlServer", "local_control/LocalControlCodec", "local_control/LocalControlInbox", "local_control/LocalSocketAdmission", "local_control/LocalBearerAuthenticator", "provisioning/LocalTlsIdentity", "provisioning/LocalPairingCredential", "conversation/ConversationSession", "conversation/ConversationStartCodec"], ["tests/server_stubs", "lib/device_configuration/include"]),
        ("espidf_local_service_advertisement_test", ["local_control/EspIdfLocalServiceAdvertisement", "lib/device_configuration/NetworkSettings", "network_provisioning/NetworkProfiles"], ["tests/discovery_stubs", "tests/station_stubs", "lib/device_configuration/include"]),
        ("wand_presentation_test", CORE, []),
        ("local_control_owner_test", CORE + ["conversation/ConversationSession", "conversation/ConversationStartCodec", "local_control/LocalControlSession", "local_control/LocalConversationGateway", "local_control/LocalControlInbox", "local_control/LocalControlOwner"], []),
        ("local_control_codec_test", ["conversation/ConversationSession", "conversation/ConversationStartCodec", "local_control/LocalControlCodec"], []),
        ("local_control_output_test", ["safety/SafetyController", "wand_selection/SelectionSequence", "device_control/DeviceControl", "device_control/PersistentCommandJournal", "conversation/ConversationSession", "conversation/ConversationStartCodec", "local_control/LocalControlOutput"], []),
        ("show_trace_replay", CORE, []),
    ]
    # Windows indexing can briefly retain a finished executable. Leaving its
    # bounded OS-temp directory must not turn passing assertions into failure.
    with tempfile.TemporaryDirectory(prefix="dv02-host-", ignore_cleanup_errors=True) as temp:
        output = Path(temp)
        completed = 0
        for name, sources, includes in checks:
            if args.only and name not in args.only:
                continue
            executable = output / (name + (".exe" if msvc else ""))
            files = [
                str(ROOT / (source + ".cpp"))
                if source.startswith("lib/")
                else str(ROOT / "src/features" / (source + ".cpp"))
                for source in sources
            ]
            files.append(str(ROOT / "tests" / (name + ".cpp")))
            if msvc:
                flags = ["/nologo", "/std:c++14", "/utf-8", "/EHsc", "/W4", "/WX", "/wd4244", "/Fe:" + str(executable)]
                if name == "espidf_local_service_advertisement_test":
                    # NetworkProfiles bounds its copies before using standard
                    # C functions; retain that portable code on the MSVC host.
                    flags += ["/D_CRT_SECURE_NO_WARNINGS"]
                flags += ["/I" + str(ROOT / include) for include in includes]
            else:
                flags = ["-std=c++14", "-Wall", "-Wextra", "-Werror", "-pthread", "-o", str(executable)]
                flags += ["-I" + str(ROOT / include) for include in includes]
            subprocess.run([compiler, *flags, *files], cwd=output, env=env, check=True)
            if name == "show_trace_replay":
                for scenario in ("success", "stop"):
                    trace = subprocess.check_output([str(executable), scenario], env=env, text=True, encoding="utf-8")
                    document = json.loads(trace)
                    assert document["bootId"] == "synthetic-host"
                    assert document["state"] == ("completed" if scenario == "success" else "fault")
                    compiled = json.loads((ROOT.parent / "mechanika/final/effects.plan.json").read_text(encoding="utf-8"))
                    scene = next(scene for scene in compiled["scenes"] if scene["sceneId"] == document["sceneId"] and scene["variant"] == "main")
                    assert document["variant"] == scene["variant"]
                    assert document["sceneHash"] == scene["sceneHash"]
                    if args.fixtures:
                        args.fixtures.mkdir(parents=True, exist_ok=True)
                        (args.fixtures / ("show-trace-" + scenario + ".json")).write_text(trace, encoding="utf-8", newline="\n")
            else:
                arguments = [str(executable)]
                if name == "local_control_server_test":
                    arguments += [str(ROOT.parent / "contracts/fixtures/valid" / fixture) for fixture in ("hello.json", "safety-stop.json")]
                if name == "local_control_codec_test":
                    valid = ("hello", "state-get", "control-acquire", "control-release", "heartbeat", "safety-stop", "safety-reset", "conversation-text", "conversation-cancel", "selection-start", "inventory-confirm-reload", "voice-configure", "voice-start", "voice-stop")
                    arguments += [str(ROOT.parent / "contracts/fixtures/valid" / (fixture + ".json")) for fixture in valid]
                    arguments += ["--reject", *[str(path) for path in sorted((ROOT.parent / "contracts/fixtures/invalid").glob("*.json"))]]
                if name == "local_control_output_test":
                    data = subprocess.check_output(arguments, cwd=output, env=env, text=True, encoding="utf-8")
                    emitted = output / "control-output.jsonl"
                    emitted.write_text(data, encoding="utf-8")
                    node = shutil.which("node", path=env.get("Path", env.get("PATH", "")))
                    if not node:
                        raise SystemExit("Node.js is required to verify the unchanged strict control envelope")
                    subprocess.run([node, str(ROOT.parent / "contracts/verify-firmware-output.mjs"), str(emitted)], env=env, check=True)
                elif name == "twin_telemetry_test" and args.telemetry_fixture:
                    data = subprocess.check_output([str(executable), "--export"], cwd=output, env=env, text=True, encoding="utf-8")
                    args.telemetry_fixture.parent.mkdir(parents=True, exist_ok=True)
                    args.telemetry_fixture.write_text(data, encoding="utf-8", newline="\n")
                    print("PASS actual firmware telemetry serializer fixture", flush=True)
                else:
                    subprocess.run(arguments, cwd=output, env=env, check=True)
            print(f"PASS {name}", flush=True)
            completed += 1
    print(f"PASS {completed} offline DV02 host checks", flush=True)


if __name__ == "__main__":
    main()
