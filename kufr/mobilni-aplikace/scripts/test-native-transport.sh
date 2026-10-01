#!/usr/bin/env bash
set -euo pipefail
test_directory="$(mktemp -d "${TMPDIR:-/tmp}/kufr-native-test.XXXXXX")"
server_pid=""
cleanup() {
  if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; fi
  rm -rf -- "$test_directory"
}
trap cleanup EXIT
for name in ec256 ec384 rsa; do
  case "$name" in
    ec256) key_options=(-newkey ec -pkeyopt ec_paramgen_curve:prime256v1) ;;
    ec384) key_options=(-newkey ec -pkeyopt ec_paramgen_curve:secp384r1) ;;
    rsa) key_options=(-newkey rsa:2048) ;;
  esac
  openssl req -x509 "${key_options[@]}" -nodes -days 1 -subj '/CN=kufr-native-test' \
    -keyout "$test_directory/$name.key" -out "$test_directory/$name.pem" 2>/dev/null
  openssl x509 -in "$test_directory/$name.pem" -outform DER -out "$test_directory/$name.der"
  openssl x509 -in "$test_directory/$name.pem" -pubkey -noout | \
    openssl pkey -pubin -outform DER | openssl dgst -sha256 -r | \
    cut -d ' ' -f1 > "$test_directory/$name.sha256"
done
swiftc -swift-version 5 -warnings-as-errors \
  ios/App/App/LocalDeviceIdentity.swift ios/App/App/LocalDeviceTransport.swift \
  ios/App/App/LocalDeviceDiscovery.swift \
  tests/native_transport/main.swift -o "$test_directory/native-tests"
node scripts/native-test-server.mjs "$test_directory" 49443 > "$test_directory/server.log" 2>&1 &
server_pid="$!"
for attempt in {1..50}; do
  if grep -q native-test-peer-ready "$test_directory/server.log"; then break; fi
  sleep 0.1
done
grep -q native-test-peer-ready "$test_directory/server.log"
"$test_directory/native-tests" "$test_directory" 49443
