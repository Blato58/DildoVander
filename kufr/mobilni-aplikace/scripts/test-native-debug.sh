#!/usr/bin/env bash
set -euo pipefail
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/kufr-debug-test.XXXXXX")"
trap 'rm -rf -- "$test_directory"' EXIT
swiftc -swift-version 5 -warnings-as-errors -parse-as-library \
  ios/App/App/DebugBackendClient.swift tests/native_debug/main.swift \
  -o "$test_directory/native-debug-tests"
"$test_directory/native-debug-tests"
