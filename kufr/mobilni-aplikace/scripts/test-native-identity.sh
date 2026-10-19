#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
swiftc "$root/ios/App/App/AccountTransportPolicy.swift" "$root/tests/native_identity/main.swift" -o "$work_dir/identity-test"
"$work_dir/identity-test"
