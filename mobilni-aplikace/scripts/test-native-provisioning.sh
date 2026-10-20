#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
swiftc "$root/ios/App/App/ProvisioningStatusCodec.swift" "$root/tests/native_provisioning/main.swift" -o "$work_dir/provisioning-test"
"$work_dir/provisioning-test"
