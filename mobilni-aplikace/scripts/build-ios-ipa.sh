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
: "${KUFR_PASSKEY_RP_DOMAIN:?Configure the canonical passkey domain before signing}"
: "${IOS_SIGNING_KIND:?Signing kind is required}"
: "${IOS_BUILD_CERTIFICATE_BASE64:?Certificate secret is required}"
: "${IOS_P12_PASSWORD:?Certificate password is required}"
: "${IOS_PROVISION_PROFILE_BASE64:?Profile secret is required}"
: "${IOS_KEYCHAIN_PASSWORD:?Keychain password is required}"

case "${IOS_SIGNING_KIND}" in
  development) export_method=debugging ;;
  ad_hoc) export_method=release-testing ;;
  app_store) export_method=app-store-connect ;;
  *) echo 'Unsupported iOS signing kind.' >&2; exit 2 ;;
esac
if [[ "${IOS_SIGNING_KIND}" == app_store && "${IOS_BUNDLE_ID}" != de.dildovander.companion ]]; then
  echo 'The App Store build requires the approved Wand Night Stand Bundle ID.' >&2
  exit 2
fi
if ! xcodebuild -help 2>&1 | grep -F -- "${export_method}" >/dev/null; then
  echo "This Xcode does not support the ${export_method} export method." >&2
  exit 1
fi

work_dir="$(mktemp -d "${RUNNER_TEMP}/kufr-ios.XXXXXXXX")"
keychain="${work_dir}/signing.keychain-db"
profile_path=''
project_path='ios/App/App.xcodeproj/project.pbxproj'
project_backup=''
info_path='ios/App/App/Info.plist'
info_backup=''
original_keychains="${work_dir}/keychains.txt"
security list-keychains -d user | sed 's/^[[:space:]]*"//; s/"[[:space:]]*$//' > "${original_keychains}"

cleanup() {
  local restore_failed=0
  local recovery_dir=''
  if [[ -n "${info_backup}" ]] && ! cp "${info_backup}" "${info_path}"; then
    restore_failed=1
    if recovery_dir="$(mktemp -d "${RUNNER_TEMP}/kufr-ios-info-recovery.XXXXXXXX")" \
      && cp "${info_backup}" "${recovery_dir}/Info-original.plist"; then
      echo "Could not restore Info.plist; public backup retained at ${recovery_dir}/Info-original.plist." >&2
    else
      echo 'Could not restore Info.plist or preserve its public backup.' >&2
    fi
  fi
  if [[ -n "${project_backup}" ]]; then
    if ! cp "${project_backup}" "${project_path}"; then
      restore_failed=1
      # Preserve only the public project, never the credential-bearing work dir.
      if recovery_dir="$(mktemp -d "${RUNNER_TEMP}/kufr-ios-project-recovery.XXXXXXXX")" \
        && cp "${project_backup}" "${recovery_dir}/project-original.pbxproj"; then
        echo "Could not restore the Xcode project; public backup retained at ${recovery_dir}/project-original.pbxproj." >&2
      else
        echo 'Could not restore the Xcode project or preserve its public backup.' >&2
        if [[ -n "${recovery_dir}" ]]; then
          rm -rf -- "${recovery_dir}"
        fi
      fi
    fi
  fi
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
  return "${restore_failed}"
}
trap cleanup EXIT

printf '%s' "${IOS_BUILD_CERTIFICATE_BASE64}" | base64 -D > "${work_dir}/certificate.p12"
printf '%s' "${IOS_PROVISION_PROFILE_BASE64}" | base64 -D > "${work_dir}/profile.mobileprovision"
security cms -D -i "${work_dir}/profile.mobileprovision" > "${work_dir}/profile.plist"

profile_uuid="$(python3 scripts/validate-ios-profile.py \
  "${work_dir}/profile.plist" "${IOS_TEAM_ID}" "${IOS_BUNDLE_ID}" "${IOS_SIGNING_KIND}")"

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

sign_identity="$(python3 scripts/validate-ios-profile.py \
  "${work_dir}/profile.plist" "${IOS_TEAM_ID}" "${IOS_BUNDLE_ID}" "${IOS_SIGNING_KIND}" \
  --identities "${work_dir}/identities.txt")"

cat > "${work_dir}/ExportOptions.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>${export_method}</string>
  <key>destination</key><string>export</string>
  <key>manageAppVersionAndBuildNumber</key><false/>
  <key>signingStyle</key><string>manual</string>
  <key>teamID</key><string>${IOS_TEAM_ID}</string>
  <key>signingCertificate</key><string>${sign_identity}</string>
  <key>provisioningProfiles</key><dict>
    <key>${IOS_BUNDLE_ID}</key><string>${profile_uuid}</string>
  </dict>
</dict></plist>
EOF

mkdir -p dist/ipa
# Command-line build settings also affect Swift package resource bundles. Keep
# signing metadata in the App target's Release configuration for this run only.
cp "${project_path}" "${work_dir}/project-original.pbxproj"
project_backup="${work_dir}/project-original.pbxproj"
plutil -convert json -o "${work_dir}/project.json" "${project_path}"
version_arguments=()
if [[ "${IOS_SIGNING_KIND}" == app_store ]]; then
  version_arguments=(--marketing-version 1.0)
  cp "${info_path}" "${work_dir}/Info-original.plist"
  info_backup="${work_dir}/Info-original.plist"
  plutil -replace CFBundleDisplayName -string 'Wand Night Stand' "${info_path}"
fi
python3 scripts/configure-ios-signing.py \
  "${work_dir}/project.json" "${work_dir}/project-signed.pbxproj" \
  "${IOS_TEAM_ID}" "${IOS_BUNDLE_ID}" "${sign_identity}" \
  "${profile_uuid}" "${build_number}" --passkey-domain "${KUFR_PASSKEY_RP_DOMAIN}" "${version_arguments[@]}"
plutil -lint "${work_dir}/project-signed.pbxproj"
cp "${work_dir}/project-signed.pbxproj" "${project_path}"

xcodebuild archive \
  -project ios/App/App.xcodeproj \
  -scheme App \
  -configuration Release \
  -destination 'generic/platform=iOS' \
  -archivePath "${work_dir}/App.xcarchive"

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
if [[ "${IOS_SIGNING_KIND}" == app_store ]]; then
  if [[ "$(defaults read "${app_path}/Info" CFBundleShortVersionString)" != 1.0 \
    || "$(defaults read "${app_path}/Info" CFBundleVersion)" != "${build_number}" \
    || "$(defaults read "${app_path}/Info" CFBundleDisplayName)" != 'Wand Night Stand' ]]; then
    echo 'Exported App Store app metadata does not match.' >&2
    exit 1
  fi
  codesign --display --entitlements - "${app_path}" > "${work_dir}/signed-entitlements.plist"
  python3 - "${work_dir}/signed-entitlements.plist" "${IOS_TEAM_ID}.${IOS_BUNDLE_ID}" <<'PY'
import plistlib
import sys

with open(sys.argv[1], 'rb') as source:
    entitlements = plistlib.load(source)
if entitlements.get('get-task-allow', False) is not False or entitlements.get('application-identifier') != sys.argv[2]:
    raise SystemExit('Exported App Store entitlements do not match distribution requirements.')
PY
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
