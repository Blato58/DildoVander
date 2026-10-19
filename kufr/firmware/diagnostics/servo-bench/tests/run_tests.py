"""Offline controller checks; uses the repository's existing host compiler discovery."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
FIRMWARE = HERE.parents[2]
spec = importlib.util.spec_from_file_location("show_test_runner", FIRMWARE / "tests/run_show_tests.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
compiler, environment, msvc = runner.compiler_environment()
with tempfile.TemporaryDirectory(prefix="servo-bench-", ignore_cleanup_errors=True) as directory:
    output = Path(directory) / ("controller-test.exe" if msvc else "controller-test")
    if msvc:
        command = [compiler, "/nologo", "/std:c++14", "/EHsc", "/W4", "/WX",
                   str(HERE / "controller_test.cpp"), f"/Fe:{output}"]
    else:
        command = [compiler, "-std=c++14", "-Wall", "-Wextra", "-Werror",
                   str(HERE / "controller_test.cpp"), "-o", str(output)]
    subprocess.run(command, env=environment, cwd=directory, check=True)
    subprocess.run([str(output)], check=True)
