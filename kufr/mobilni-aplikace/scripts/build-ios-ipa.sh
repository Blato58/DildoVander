#!/usr/bin/env bash
set -euo pipefail
umask 077

build_number="${IOS_BUILD_NUMBER:-${GITHUB_RUN_NUMBER:-1}}"
if [[ ! "$build_number" =~ ^[1-9][0-9]{0,3}(\.[0-9]{1,2}){0,2}$ ]]; then
  echo 'Invalid numeric iOS build version.' >&2
  exit 2
fi

: "${RUNNER_TEMP:?RUNNER_TEMP is required}"
: "${IOS_BUNDLE_ID:?IOS_BUNDLE_ID is required}"
: "${IOS_TEAM_ID:?IOS_TEAM_ID is required}"
: "${IOS_SIGNING_KIND:?Signing kind is required}"
: "${IOS_BUILD_CERTIFICATE_BASE64:?Certificate secret is required}"
: "${IOS_P12_PASSWORD:?Certificate password is required}"
: "${IOS_PROVISION_PROFILE_BASE64:?Profile secret is required}"
: "${IOS_KEYCHAIN_PASSWORD:?Keychain password is required}"

case "${IOS_SIGNING_KIND}" in
  development) export_method=debugging ;;
  ad_hoc) export_method=release-testing ;;
  *) echo 'Unsupported iOS signing kind.' >&2; exit 2 ;;
esac
if ! xcodebuild -help 2>&1 | grep -q "${export_method}"; then
  echo "This Xcode does not support the ${export_method} export method." >&2
  exit 1
fi

work_dir="$(mktemp -d "${RUNNER_TEMP}/kufr-ios.XXXXXXXX")"
keychain="${work_dir}/signing.keychain-db"
profile_path=''
original_keychains="${work_dir}/keychains.txt"
security list-keychains -d user | sed 's/^[[:space:]]*"//; s/"[[:space:]]*$//' > "${original_keychains}"

cleanup() {
  local previous_keychains=()
  while IFS= read -r item; do
    previous_keychains+=("${item}")
  done < "${original_keychains}"
  if ((${#previous_keychains[@]})); then
    security list-keychains -d user -s "${previous_keychains[@]}" || true
  fi
  if [[ -n "${profile_path}" ]]; then
    rm -f -- "${profile_path}"
  fi
  security delete-keychain "${keychain}" >/dev/null 2>&1 || true
  rm -rf -- "${work_dir}"
}
trap cleanup EXIT

printf '%s' "${IOS_BUILD_CERTIFICATE_BASE64}" | base64 -D > "${work_dir}/certificate.p12"
printf '%s' "${IOS_PROVISION_PROFILE_BASE64}" | base64 -D > "${work_dir}/profile.mobileprovision"
security cms -D -i "${work_dir}/profile.mobileprovision" > "${work_dir}/profile.plist"

profile_uuid="$(python3 - "${work_dir}/profile.plist" "${IOS_TEAM_ID}" "${IOS_BUNDLE_ID}" "${IOS_SIGNING_KIND}" <<'PY'
import datetime
import plistlib
import sys

with open(sys.argv[1], 'rb') as profile_file:
    profile = plistlib.load(profile_file)

team_id, bundle_id, signing_kind = sys.argv[2:5]
entitlements = profile.get('Entitlements', {})
if profile.get('TeamIdentifier') != [team_id]:
    raise SystemExit('Provisioning profile Team ID does not match.')
if entitlements.get('application-identifier') != f'{team_id}.{bundle_id}':
    raise SystemExit('Provisioning profile Bundle ID does not match.')
if entitlements.get('get-task-allow') is not (signing_kind == 'development'):
    raise SystemExit('The provisioning profile type does not match the signing kind.')
if not profile.get('ProvisionedDevices'):
    raise SystemExit('The profile does not contain a registered device.')
if profile['ExpirationDate'] <= datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None):
    raise SystemExit('The provisioning profile has expired.')
print(profile['UUID'])
PY
)"

mkdir -p "$HOME/Library/MobileDevice/Provisioning Profiles"
target_profile_path="$HOME/Library/MobileDevice/Provisioning Profiles/${profile_uuid}.mobileprovision"
if [[ -e "${target_profile_path}" ]]; then
  echo 'A provisioning profile with this UUID is already installed on the runner.' >&2
  exit 1
fi
profile_path="${target_profile_path}"
cp "${work_dir}/profile.mobileprovision" "${profile_path}"

security create-keychain -p "${IOS_KEYCHAIN_PASSWORD}" "${keychain}"
security set-keychain-settings -lut 21600 "${keychain}"
security unlock-keychain -p "${IOS_KEYCHAIN_PASSWORD}" "${keychain}"
if ! security import "${work_dir}/certificate.p12" -k "${keychain}" \
  -P "${IOS_P12_PASSWORD}" -T /usr/bin/codesign >/dev/null 2>&1; then
  echo 'Keychain rejected the original PKCS#12; checking it with OpenSSL.'
  pem_path="${work_dir}/certificate.pem"
  compatible_path="${work_dir}/certificate-compatible.p12"
  if ! openssl pkcs12 -in "${work_dir}/certificate.p12" \
    -passin env:IOS_P12_PASSWORD -nodes -out "${pem_path}" >/dev/null 2>&1; then
    echo 'OpenSSL could not read the PKCS#12 with IOS_P12_PASSWORD.' >&2
    exit 1
  fi
  if openssl pkcs12 -help 2>&1 | grep -q -- '-legacy'; then
    if ! openssl pkcs12 -export -legacy \
      -in "${pem_path}" -out "${compatible_path}" \
      -passout env:IOS_P12_PASSWORD >/dev/null 2>&1; then
      echo 'OpenSSL could not repackage the PKCS#12 for Keychain.' >&2
      exit 1
    fi
  elif ! openssl pkcs12 -export \
    -in "${pem_path}" -out "${compatible_path}" \
    -passout env:IOS_P12_PASSWORD >/dev/null 2>&1; then
    echo 'OpenSSL could not repackage the PKCS#12 for Keychain.' >&2
    exit 1
  fi
  rm -f -- "${pem_path}"
  security import "${compatible_path}" -k "${keychain}" \
    -P "${IOS_P12_PASSWORD}" -T /usr/bin/codesign >/dev/null
  echo 'OpenSSL-compatible PKCS#12 imported into Keychain.'
fi
security set-key-partition-list -S apple-tool:,apple: -s -k "${IOS_KEYCHAIN_PASSWORD}" "${keychain}" >/dev/null
previous_keychains=()
while IFS= read -r item; do
  previous_keychains+=("${item}")
done < "${original_keychains}"
security list-keychains -d user -s "${keychain}" "${previous_keychains[@]}"
security find-identity -v -p codesigning "${keychain}" > "${work_dir}/identities.txt"

sign_identity="$(python3 - "${work_dir}/profile.plist" "${work_dir}/identities.txt" <<'PY'
import hashlib
import plistlib
import re
import sys

with open(sys.argv[1], 'rb') as profile_file:
    profile = plistlib.load(profile_file)
with open(sys.argv[2], encoding='utf-8') as identity_file:
    identities = identity_file.read()

matches = []
for certificate in profile.get('DeveloperCertificates', []):
    fingerprint = hashlib.sha1(certificate).hexdigest().upper()
    if re.search(rf'\b{fingerprint}\b', identities):
        matches.append(fingerprint)
if len(matches) != 1:
    raise SystemExit('Exactly one imported signing identity must match the profile.')
print(matches[0])
PY
)"

cat > "${work_dir}/ExportOptions.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>${export_method}</string>
  <key>signingStyle</key><string>manual</string>
  <key>teamID</key><string>${IOS_TEAM_ID}</string>
  <key>signingCertificate</key><string>${sign_identity}</string>
  <key>provisioningProfiles</key><dict>
    <key>${IOS_BUNDLE_ID}</key><string>${profile_uuid}</string>
  </dict>
</dict></plist>
EOF

mkdir -p dist/ipa
xcodebuild archive \
  -project ios/App/App.xcodeproj \
  -scheme App \
  -configuration Release \
  -destination 'generic/platform=iOS' \
  -archivePath "${work_dir}/App.xcarchive" \
  CODE_SIGN_STYLE=Manual \
  CODE_SIGN_IDENTITY="${sign_identity}" \
  PROVISIONING_PROFILE_SPECIFIER="${profile_uuid}" \
  DEVELOPMENT_TEAM="${IOS_TEAM_ID}" \
  PRODUCT_BUNDLE_IDENTIFIER="${IOS_BUNDLE_ID}" \
  CURRENT_PROJECT_VERSION="${build_number}"

xcodebuild -exportArchive \
  -archivePath "${work_dir}/App.xcarchive" \
  -exportPath dist/ipa \
  -exportOptionsPlist "${work_dir}/ExportOptions.plist"

ipa_files=(dist/ipa/*.ipa)
if [[ ${#ipa_files[@]} -ne 1 || ! -f "${ipa_files[0]}" ]]; then
  echo 'Expected exactly one exported IPA.' >&2
  exit 1
fi

unzip -q "${ipa_files[0]}" 'Payload/*.app/*' -d "${work_dir}/exported"
app_paths=("${work_dir}"/exported/Payload/*.app)
app_path="${app_paths[0]}"
if [[ ! -d "${app_path}" ]]; then
  echo 'The IPA does not contain an app bundle.' >&2
  exit 1
fi
codesign --verify --deep --strict --verbose=2 "${app_path}"
actual_bundle_id="$(defaults read "${app_path}/Info" CFBundleIdentifier)"
if [[ "${actual_bundle_id}" != "${IOS_BUNDLE_ID}" ]]; then
  echo 'Exported IPA Bundle ID does not match.' >&2
  exit 1
fi
security cms -D -i "${app_path}/embedded.mobileprovision" > "${work_dir}/embedded.plist"
python3 - "${work_dir}/profile.plist" "${work_dir}/embedded.plist" <<'PY'
import plistlib
import sys

with open(sys.argv[1], 'rb') as expected_file, open(sys.argv[2], 'rb') as actual_file:
    expected = plistlib.load(expected_file)
    actual = plistlib.load(actual_file)
if actual['UUID'] != expected['UUID']:
    raise SystemExit('The exported IPA embeds a different provisioning profile.')
PY

shasum -a 256 "${ipa_files[0]}" > "${ipa_files[0]}.sha256"
echo "${IOS_SIGNING_KIND} IPA verified and checksummed."
