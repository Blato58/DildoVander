"""Local-first CAD commands. Publication is explicit and model-only."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import time
import uuid

REPO = Path(__file__).resolve().parents[1]
CAD = REPO / "mechanika/final"
DEPLOY = REPO / "web/deploy"
sys.path.insert(0, str(CAD))


def default_cache():
    if os.environ.get("KUFR_CAD_CACHE"):
        return Path(os.environ["KUFR_CAD_CACHE"])
    base = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".cache"))
    return base / "DildoVander/cad"


class Remote:
    """A private per-command workspace; never installs or restarts a service."""
    def __init__(self, host):
        if not host or host.startswith("-") or not all(c.isalnum() or c in "._-" for c in host):
            raise ValueError("Expected an existing SSH alias")
        self.host = host
        self.directory = "/tmp/dildovander-cad-" + uuid.uuid4().hex

    def ssh(self, *command):
        result = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", self.host,
                                 shlex.join(command)], check=True, text=True, capture_output=True)
        return result.stdout

    def __enter__(self):
        self.ssh("mkdir", "-m", "700", self.directory)
        try:
            self.copy(DEPLOY / "cad_host.py", DEPLOY / "release_store.py")
        except Exception:
            self.__exit__(None, None, None)
            raise
        return self

    def copy(self, *paths):
        subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-r", *(str(p) for p in paths),
                        self.host + ":" + self.directory + "/"], check=True)

    def command(self, operation, channel, source=None):
        args = ["python3", self.directory + "/cad_host.py", operation, "--channel", channel]
        if source:
            args += ["--source", source]
        return json.loads(self.ssh(*args))

    def __exit__(self, *args):
        # Literal generated directory, never user input or a computed parent.
        self.ssh("rm", "-rf", "--", self.directory)


def deploy(bundle, host, channel):
    from fast_build import validate
    validate(bundle)  # Full maintained viewer schema, before any upload.
    manifest = json.loads((bundle / "model-bundle.json").read_bytes())
    if manifest["provenance"]["kind"] != "cad-export":
        raise ValueError("Synthetic fixtures cannot be deployed")
    if channel == "production" and manifest["provenance"]["cadVerification"] != "verified":
        raise ValueError("Production requires a verified model; use --channel preview")
    with Remote(host) as remote:
        status = remote.command("status", channel)
        if (status.get("active") or {}).get("bundleHash") == manifest["bundleHash"]:
            print(json.dumps({**status, "upload": "SKIPPED: active model hash unchanged"}))
            return status
        # Copy only a validated bundle with a stable source name; no app/archive.
        with tempfile.TemporaryDirectory(prefix="cad-upload-") as name:
            import shutil
            snapshot = Path(name) / "bundle"
            shutil.copytree(bundle, snapshot)
            validate(snapshot)
            if json.loads((snapshot / "model-bundle.json").read_bytes()) != manifest:
                raise ValueError("Model changed before upload")
            remote.copy(snapshot)
        result = remote.command("deploy", channel, remote.directory + "/bundle")
        print(json.dumps(result))
        return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("build", "deploy", "publish", "verify", "rollback", "status"))
    parser.add_argument("--force", action="store_true", help="Ignore geometry and tessellation cache reads")
    parser.add_argument("--cache", type=Path, default=default_cache())
    parser.add_argument("--bundle", type=Path, default=CAD / "exports/preview/model-release")
    parser.add_argument("--channel", choices=("preview", "production"), default="preview")
    parser.add_argument("--host", default="vps-dildovander")
    parser.add_argument("--manufacturing", action="store_true", help="verify only: full existing manufacturing pipeline")
    args = parser.parse_args(argv)
    started = time.perf_counter()
    if args.manufacturing and args.command != "verify":
        parser.error("--manufacturing is only available with verify")
    if args.command in ("build", "publish"):
        from fast_build import build
        if args.bundle.resolve() != (CAD / "exports/preview/model-release").resolve():
            parser.error("build writes only exports/preview/model-release; --bundle selects a deploy/verify input")
        build(args.cache, args.bundle, args.force)
    if args.command in ("deploy", "publish"):
        deploy(args.bundle.resolve(), args.host, args.channel)
    elif args.command == "verify":
        if args.manufacturing:
            command = [sys.executable, "-X", "utf8", "regenerate.py"]
            if args.force:
                command.append("--force")
            subprocess.run(command, cwd=CAD, check=True)
        else:
            from fast_build import validate
            validate(args.bundle.resolve())
    elif args.command in ("status", "rollback"):
        with Remote(args.host) as remote:
            print(json.dumps(remote.command(args.command, args.channel)))
    print(f"CAD {args.command} complete ({time.perf_counter() - started:.2f}s)", flush=True)


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as error:
        if error.stderr:
            print(error.stderr, file=sys.stderr)
        raise SystemExit(f"CAD command failed (exit {error.returncode})")
    except (OSError, ValueError, KeyError) as error:
        raise SystemExit("CAD command failed: " + str(error))
