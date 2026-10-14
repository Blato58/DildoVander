#!/usr/bin/env python3
"""Build the native twin from production portable C++ sources, offline."""
import argparse
import importlib.util
from pathlib import Path
import subprocess

NATIVE = Path(__file__).resolve().parent
FIRMWARE = NATIVE.parents[1] / "firmware"
CORE = [
    "safety/SafetyController", "wand_selection/SelectionSequence",
    "device_control/DeviceControl", "device_control/DeviceControlRuntime",
    "device_control/PersistentCommandJournal",
    "conversation/ConversationRuntimeState", "conversation/PendingProposal",
    "conversation/TurnPollGuard", "wand_presentation/WandPresentation",
    "conversation/ConversationTextCodec", "conversation/ConversationStartCodec",
    "conversation/ConversationSession", "conversation/TurnOperationCodec",
    "physical_effects/PhysicalEffects", "physical_effects/PhysicalPresentation",
    "physical_effects/PresentationDiagnostics", "dance/DanceCoordinator",
]


def build(output, entry="main.cpp"):
    spec = importlib.util.spec_from_file_location("firmware_host_compiler", FIRMWARE / "tests/run_show_tests.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    compiler, env, msvc = module.compiler_environment()
    output = output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    sources = [FIRMWARE / "src/features" / (source + ".cpp") for source in CORE]
    sources += [NATIVE / name for name in (entry, "VirtualHardware.cpp", "FileJournal.cpp")]
    if msvc:
        flags = ["/nologo", "/std:c++14", "/utf-8", "/EHsc", "/W4", "/WX", "/wd4244",
                 "/I" + str(FIRMWARE / "src/features"), "/Fe:" + str(output)]
    else:
        flags = ["-std=c++14", "-Wall", "-Wextra", "-Werror", "-pthread",
                 "-I", str(FIRMWARE / "src/features"), "-o", str(output)]
    subprocess.run([compiler, *flags, *map(str, sources)], cwd=output.parent, env=env, check=True)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(build(args.output))
