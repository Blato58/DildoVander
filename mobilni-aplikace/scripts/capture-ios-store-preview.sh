#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 || "$(uname -s)" != Darwin ]]; then
  echo "Usage on macOS: $0 /path/to/App.app /new/output-directory" >&2
  exit 1
fi
app_path="$1"
output_dir="$2"
if [[ ! -d "$app_path" || -e "$output_dir" ]]; then
  echo "The Simulator app must exist and the output directory must be new." >&2
  exit 1
fi
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/wns-store-preview.XXXXXX")"
owned_devices=("")
cleanup() {
  local status=$?
  trap - EXIT INT TERM
  for device in "${owned_devices[@]}"; do
    [[ -n "$device" ]] || continue
    run_bounded 30 xcrun simctl shutdown "$device" >/dev/null 2>&1 || true
    if ! run_bounded 30 xcrun simctl delete "$device" >/dev/null 2>&1; then
      echo "Could not remove owned preview Simulator $device." >&2
      [[ "$status" -ne 0 ]] || status=1
    fi
  done
  rm -rf -- "$work_dir"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

run_bounded() {
  python3 - "$@" <<'PY'
import subprocess, sys
subprocess.run(sys.argv[2:], check=True, timeout=int(sys.argv[1]))
PY
}

run_bounded 20 xcrun simctl list devicetypes --json > "$work_dir/device-types.json"
run_bounded 20 xcrun simctl list runtimes --json > "$work_dir/runtimes.json"
python3 - "$app_path" "$work_dir" <<'PY'
import json, pathlib, plistlib, re, sys
from urllib.parse import urlsplit
app, work = map(pathlib.Path, sys.argv[1:])
with (app / 'Info.plist').open('rb') as source:
    info = plistlib.load(source)
if 'iPhoneSimulator' not in info.get('CFBundleSupportedPlatforms', []) or set(info.get('UIDeviceFamily', [])) != {1, 2}:
    raise SystemExit('Expected the compiled universal iPhone/iPad Simulator app.')
if not (app / 'public/index.html').is_file():
    raise SystemExit('Simulator app is missing the compiled shared web UI.')
origin = urlsplit(info.get('KufrAccountOrigin', ''))
rp = info.get('KufrPasskeyRelyingParty', '')
dns = r'(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?'
if origin.scheme != 'https' or origin.netloc != rp or origin.path not in ('', '/') or origin.query or origin.fragment or not re.fullmatch(dns, rp):
    raise SystemExit('Simulator app must have a matching canonical HTTPS account origin and RP domain.')
types = json.loads((work / 'device-types.json').read_text())['devicetypes']
runtimes = [runtime for runtime in json.loads((work / 'runtimes.json').read_text())['runtimes']
            if runtime.get('isAvailable') and runtime['identifier'].startswith('com.apple.CoreSimulator.SimRuntime.iOS-')]
if not runtimes:
    raise SystemExit('No installed, available iOS Simulator runtime; no runtime is downloaded automatically.')
runtime = max(runtimes, key=lambda item: tuple(int(part) for part in item['version'].split('.')))
def choose(names):
    for name in names:
        match = next((item for item in types if item['name'] == name), None)
        if match:
            return match
    raise SystemExit('Missing required Simulator device type: ' + ' / '.join(names))
devices = [
    {'slot': 'iphone-6.3', 'deviceType': choose(['iPhone 17 Pro', 'iPhone 16 Pro']), 'portraitSizes': [[1206, 2622]]},
    {'slot': 'ipad-13', 'deviceType': choose(['iPad Pro 13-inch (M5)', 'iPad Pro 13-inch (M4)', 'iPad Air 13-inch (M3)', 'iPad Air 13-inch (M2)']), 'portraitSizes': [[2064, 2752], [2048, 2732]]},
]
(work / 'selection.json').write_text(json.dumps({'runtime': runtime, 'devices': devices}))
(work / 'bundle-id').write_text(info['CFBundleIdentifier'])
PY

bundle_id="$(cat "$work_dir/bundle-id")"
runtime="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["runtime"]["identifier"])' "$work_dir/selection.json")"
mkdir -p "$output_dir"
for slot in iphone-6.3 ipad-13; do
  device_type="$(python3 -c 'import json,sys; print(next(item["deviceType"]["identifier"] for item in json.load(open(sys.argv[1]))["devices"] if item["slot"] == sys.argv[2]))' "$work_dir/selection.json" "$slot")"
  device="$(run_bounded 30 xcrun simctl create "WNS store preview $slot $(date +%s)" "$device_type" "$runtime")"
  owned_devices+=("$device")
  run_bounded 30 xcrun simctl boot "$device"
  run_bounded 120 xcrun simctl bootstatus "$device" -b
  run_bounded 45 xcrun simctl install "$device" "$app_path"
  launch_output="$(run_bounded 30 xcrun simctl launch "$device" "$bundle_id")"
  app_pid="${launch_output##*: }"
  if [[ ! "$app_pid" =~ ^[0-9]+$ ]]; then
    echo "Simulator did not return the launched app PID." >&2
    exit 1
  fi
  # A fresh installation launches the real public home. No navigation, account,
  # archive, microphone, camera, provider request or hardware action is injected.
  sleep 20
  kill -0 "$app_pid"
  run_bounded 20 xcrun simctl io "$device" screenshot --type=png "$output_dir/$slot-home-native.png"
  # Preserve the unedited native PNG and also supply an opaque, full-resolution
  # JPEG for App Store metadata (Apple rejects screenshot alpha channels).
  run_bounded 30 sips -s format jpeg -s formatOptions 100 "$output_dir/$slot-home-native.png" --out "$output_dir/$slot-home.jpg" >/dev/null
  python3 - "$output_dir" "$work_dir" "$slot" "$device" <<'PY'
import hashlib, json, pathlib, struct, subprocess, sys
output, work = map(pathlib.Path, sys.argv[1:3])
slot, device = sys.argv[3:]
selection = next(item for item in json.loads((work / 'selection.json').read_text())['devices'] if item['slot'] == slot)
png = output / f'{slot}-home-native.png'
header = png.read_bytes()[:24]
if header[:8] != b'\x89PNG\r\n\x1a\n' or header[12:16] != b'IHDR':
    raise SystemExit('Simulator did not produce a PNG screenshot.')
size = list(struct.unpack('>II', header[16:24]))
if size not in selection['portraitSizes']:
    raise SystemExit(f'{slot}: unexpected native portrait dimensions {size}; no resizing is permitted.')
jpeg = output / f'{slot}-home.jpg'
properties = subprocess.check_output(['sips', '-g', 'pixelWidth', '-g', 'pixelHeight', str(jpeg)], text=True, timeout=20)
dimensions = {line.split(':', 1)[0].strip(): int(line.split(':', 1)[1]) for line in properties.splitlines() if line.strip().startswith(('pixelWidth:', 'pixelHeight:'))}
if [dimensions.get('pixelWidth'), dimensions.get('pixelHeight')] != size:
    raise SystemExit('Opaque JPEG dimensions differ from the native screenshot.')
record = {'slot': slot, 'deviceType': selection['deviceType']['name'], 'simulatorUdid': device,
          'width': size[0], 'height': size[1], 'route': '/', 'state': 'fresh-install-signed-out-public-home',
          'nativePng': {'file': png.name, 'sha256': hashlib.sha256(png.read_bytes()).hexdigest()},
          'metadataCandidate': {'file': jpeg.name, 'sha256': hashlib.sha256(jpeg.read_bytes()).hexdigest(), 'conversion': 'opaque JPEG quality 100; no resize, cropping, frames or overlays'}}
(work / f'{slot}.json').write_text(json.dumps(record))
PY
  run_bounded 30 xcrun simctl shutdown "$device"
done

python3 - "$app_path" "$output_dir" "$work_dir" <<'PY'
import datetime, hashlib, json, os, pathlib, plistlib, subprocess, sys
app, output, work = map(pathlib.Path, sys.argv[1:])
with (app / 'Info.plist').open('rb') as source:
    info = plistlib.load(source)
selection = json.loads((work / 'selection.json').read_text())
manifest = {
    'schemaVersion': 1, 'capturedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'sourceRevision': subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True, timeout=10).strip(),
    'ciRunUrl': f"{os.environ['GITHUB_SERVER_URL']}/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}" if os.environ.get('GITHUB_RUN_ID') else None,
    'captureKind': 'native-ios-simulator', 'signedAppStoreBuild': False, 'physicalDeviceEvidence': False,
    'buildConfiguration': os.environ.get('IOS_STORE_PREVIEW_CONFIGURATION', 'unspecified'),
    'bundleId': info['CFBundleIdentifier'], 'displayName': info.get('CFBundleDisplayName'),
    'accountOrigin': info['KufrAccountOrigin'], 'relyingParty': info['KufrPasskeyRelyingParty'],
    'sharedWebIndexSha256': hashlib.sha256((app / 'public/index.html').read_bytes()).hexdigest(),
    'xcode': subprocess.check_output(['xcodebuild', '-version'], text=True, timeout=15).strip(),
    'runtime': {key: selection['runtime'].get(key) for key in ('name', 'identifier', 'version', 'buildversion')},
    'captures': [json.loads((work / f'{slot}.json').read_text()) for slot in ('iphone-6.3', 'ipad-13')],
    'limits': 'Unsigned Simulator public-home candidates only. Inspect for rendered UI before metadata upload. No account, provider, accessory or physical-device acceptance is established.',
}
(output / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
PY
echo "Native Simulator preview candidates and manifest: $output_dir"
