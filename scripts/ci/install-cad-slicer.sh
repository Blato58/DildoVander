#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C.UTF-8

# Official Linux distribution: https://help.prusa3d.com/article/install-prusaslicer_1903
# Pin the actual x86_64 Flathub OSTree ref, not the mutable stable branch.
# https://dl.flathub.org/repo/refs/heads/app/com.prusa3d.PrusaSlicer/x86_64/stable
app_ref=app/com.prusa3d.PrusaSlicer/x86_64/stable
app_commit=bf3534e4ffc688bbaf625206f27f9333b1b8820d3425dae19d3083b47e08ce79
expected_version=2.9.6
remote_url=https://dl.flathub.org/repo/flathub.flatpakrepo

[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || {
    echo 'The CAD slicer bootstrap requires Linux x86_64.' >&2
    exit 1
}
: "${GITHUB_WORKSPACE:?Missing GitHub workspace}"
: "${RUNNER_TEMP:?Missing runner temporary directory}"
: "${GITHUB_ENV:?Missing GitHub environment file}"
workspace=$(realpath "$GITHUB_WORKSPACE")
runner_temp=$(realpath "$RUNNER_TEMP")
for directory in "$workspace" "$runner_temp"; do
    [[ -d $directory && $directory != / && $directory != *$'\n'* && $directory != *:* ]] || {
        echo 'Invalid sandbox filesystem directory.' >&2
        exit 1
    }
done

if ! command -v flatpak >/dev/null; then
    sudo apt-get update
    sudo apt-get install --yes --no-install-recommends flatpak
fi

tool_dir=$runner_temp/kufr-slicer
mkdir -p "$tool_dir"
export FLATPAK_USER_DIR=$tool_dir/flatpak
# The official remote file supplies its GPG key; signature checks stay enabled.
flatpak --user remote-add --if-not-exists flathub "$remote_url"
flatpak --user remote-info --commit="$app_commit" flathub "$app_ref" > "$tool_dir/remote-info.txt"
flatpak --user install --assumeyes --noninteractive flathub "$app_ref"
flatpak --user update --assumeyes --noninteractive --commit="$app_commit" "$app_ref"
[[ $(flatpak --user info --show-commit "$app_ref") == "$app_commit" ]] || {
    echo 'Installed PrusaSlicer commit differs from the pin.' >&2
    exit 1
}
runtime_ref=$(flatpak --user info --show-runtime "$app_ref")
[[ $runtime_ref =~ ^[A-Za-z0-9._-]+/x86_64/[A-Za-z0-9._-]+$ ]] || {
    echo 'Unexpected PrusaSlicer runtime reference.' >&2
    exit 1
}
runtime_commit=$(flatpak --user info --show-commit "$runtime_ref")
[[ $runtime_commit =~ ^[0-9a-f]{64}$ ]] || {
    echo 'Invalid installed runtime commit.' >&2
    exit 1
}

# The audited executable is a launcher. Record its distinct native backend too.
native_sha=$(flatpak --user run --sandbox --cwd=/app --nofilesystem=host:reset \
    --command=sha256sum "$app_ref" /app/bin/prusa-slicer | cut -d ' ' -f 1)
[[ $native_sha =~ ^[0-9a-f]{64}$ ]] || {
    echo 'Cannot identify the native PrusaSlicer executable.' >&2
    exit 1
}
launcher=$tool_dir/prusa-slicer
{
    printf '#!/usr/bin/env bash\nset -euo pipefail\nexport LC_ALL=C.UTF-8\n'
    printf 'export FLATPAK_USER_DIR=%q\n' "$FLATPAK_USER_DIR"
    printf 'app_ref=%q\napp_commit=%q\nruntime_ref=%q\nruntime_commit=%q\n' \
        "$app_ref" "$app_commit" "$runtime_ref" "$runtime_commit"
    printf 'workspace=%q\nrunner_temp=%q\nnative_sha=%q\n' "$workspace" "$runner_temp" "$native_sha"
    cat <<'LAUNCHER'
[[ $(flatpak --user info --show-commit "$app_ref") == "$app_commit" &&
   $(flatpak --user info --show-runtime "$app_ref") == "$runtime_ref" &&
   $(flatpak --user info --show-commit "$runtime_ref") == "$runtime_commit" ]] || {
    echo 'Pinned slicer installation changed after bootstrap.' >&2
    exit 1
}
if [[ $# == 1 && $1 == --help ]]; then
    printf 'Kufr Flatpak app commit: %s\nKufr Flatpak runtime: %s@%s\nKufr native executable SHA-256: %s\n' \
        "$app_commit" "$runtime_ref" "$runtime_commit" "$native_sha"
fi
# Reset inherited home/media access and all other manifest privileges. X11/IPC
# serve the existing Xvfb session; model/profile/output access is limited below.
exec flatpak --user run --sandbox --socket=x11 --share=ipc \
    --nofilesystem=host:reset --filesystem="$workspace" --filesystem="$runner_temp" \
    --command=prusa-slicer "$app_ref" "$@"
LAUNCHER
} > "$launcher"
chmod 0755 "$launcher"
"$launcher" --help > "$tool_dir/tool-help.txt"
version_header=$(grep -m 1 '^PrusaSlicer-' "$tool_dir/tool-help.txt" || true)
[[ $version_header =~ ^PrusaSlicer-2\.9\.6([[:space:]]|\+|$) ]] || {
    echo "Expected PrusaSlicer $expected_version; received: $version_header" >&2
    exit 1
}
entrypoint_sha=$(sha256sum "$launcher" | cut -d ' ' -f 1)
export app_ref app_commit runtime_ref runtime_commit native_sha entrypoint_sha expected_version version_header remote_url workspace runner_temp
python - "$tool_dir/provenance.json" <<'PYTHON'
import json
import os
import sys
from pathlib import Path

fields = ("app_ref", "app_commit", "runtime_ref", "runtime_commit", "native_sha",
          "entrypoint_sha", "expected_version", "version_header", "remote_url")
record = {field: os.environ[field] for field in fields}
record["hash_scope"] = "The slicer report hashes the launcher; native_sha identifies /app/bin/prusa-slicer."
record["filesystem_access"] = [os.environ["workspace"], os.environ["runner_temp"]]
Path(sys.argv[1]).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
PYTHON
cat "$tool_dir/provenance.json"
printf 'KUFR_SLICER=%s\n' "$launcher" >> "$GITHUB_ENV"
