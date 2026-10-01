#!/usr/bin/env bash
set -euo pipefail
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/kufr-debug-test.XXXXXX")"
peer_pid=""
cleanup() {
  if [[ -n "$peer_pid" ]]; then
    kill "$peer_pid" 2>/dev/null || true
    wait "$peer_pid" 2>/dev/null || true
  fi
  rm -rf -- "$test_directory"
}
trap cleanup EXIT
node tests/native_debug/http_peer.mjs "$test_directory/http-address" &
peer_pid=$!
for ((attempt = 0; attempt < 50; attempt++)); do
  if [[ -s "$test_directory/http-address" ]]; then break; fi
  if ! kill -0 "$peer_pid" 2>/dev/null; then echo 'HTTP test peer exited' >&2; exit 1; fi
  sleep 0.1
done
if [[ ! -s "$test_directory/http-address" ]]; then echo 'HTTP test peer did not start' >&2; exit 1; fi
swiftc -swift-version 5 -warnings-as-errors -parse-as-library \
  ios/App/App/DebugBackendClient.swift tests/native_debug/main.swift \
  -o "$test_directory/native-debug-tests"
KUFR_DEBUG_HTTP_PEER="$(cat "$test_directory/http-address")" "$test_directory/native-debug-tests"
