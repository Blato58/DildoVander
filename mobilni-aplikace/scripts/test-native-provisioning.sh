#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT
swiftc "$root/ios/App/App/ProvisioningStatusCodec.swift" "$root/tests/native_provisioning/main.swift" -o "$work_dir/provisioning-test"
"$work_dir/provisioning-test"
swiftc -strict-concurrency=complete -warnings-as-errors \
  "$root/ios/App/App/AccountTransportPolicy.swift" \
  "$root/ios/App/App/CustomerBleProtocol.swift" \
  "$root/ios/tests/customer_ble/main.swift" -o "$work_dir/customer-ble-test"
"$work_dir/customer-ble-test"
