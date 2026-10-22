# Wand Night Stand App Store release

The App Store identity is `de.dildovander.companion`, display name
**Wand Night Stand**, marketing version **1.0**. The separate manual workflow
`.github/workflows/kufr-ios-app-store.yml` exports a signed App Store IPA and
checksum. It does not upload to Apple, submit a review, publish, or install the
app. The existing `kufr-ios-sign.yml` and installation workflow retain their
Ad Hoc identity and credentials.

## Owner prerequisites

An Apple Developer Account Holder or Admin must create an explicit App ID for
`de.dildovander.companion`, enable Associated Domains, create the matching App
Store Connect app record, and create an **App Store Connect distribution**
provisioning profile. Select the Apple Distribution certificate whose private
key is available as a password-protected PKCS#12 export. A device registration
list and an Ad Hoc profile cannot substitute for this profile. Apple's
[profile instructions](https://developer.apple.com/help/account/provisioning-profiles/create-an-app-store-provisioning-profile/)
describe the account roles and distribution profile creation.

Create the isolated GitHub environment `ios-app-store`, with the appropriate
required reviewers and main-branch restriction. Configure these values there;
never paste private keys, certificates, passwords, or profile contents in chat,
source, logs, or an artifact:

| Environment value | Purpose |
| --- | --- |
| Variable `IOS_APP_STORE_TEAM_ID` | Ten-character Apple team matching the new App ID/profile |
| Variable `KUFR_PASSKEY_RP_DOMAIN` | Canonical production passkey DNS domain, without scheme/path |
| Secret `IOS_APP_STORE_CERTIFICATE_BASE64` | Base64-encoded PKCS#12 distribution identity including its private key |
| Secret `IOS_APP_STORE_PROFILE_BASE64` | Base64-encoded App Store Connect `.mobileprovision` |
| Secret `IOS_APP_STORE_P12_PASSWORD` | PKCS#12 password |
| Secret `IOS_APP_STORE_KEYCHAIN_PASSWORD` | Independent temporary runner keychain password |

The canonical domain's HTTPS Apple App Site Association response must authorize
`<team-id>.de.dildovander.companion` for `webcredentials`, alongside any existing
identity still in use. Confirm backend relying-party/origin settings match that
domain. The build derives Associated Domains and account origin from this value;
a successful signature does not prove production passkey registration or login.

## Build and inspect

1. Merge reviewed source through normal delivery and wait for a successful
   `ci.yml` run for current `main` that contains the `web-dist` artifact.
2. Run **Build Wand Night Stand App Store IPA** on `main`, supplying that CI run
   ID. Approve the isolated environment when ready to use its signing identity.
   The reusable verifier checks provenance; the workflow checks `main` again
   immediately before signing and runs the native transport/debug/BLE checks.
3. Download `app-store-ipa` within its three-day retention. Verify its checksum
   and build manifest. It is an App Store distribution artifact, unsuitable for
   direct Ad Hoc installation. Keep the artifact access restricted to release
   operators.

The workflow requires macOS with Xcode **26.3** at the configured developer path.
It uses the current `app-store-connect` export method, explicitly sets
`destination=export`, and checks installed `xcodebuild -help` for that method
before importing credentials. It deliberately fails if that Xcode installation
does not support the method. Export does not manage the app version/build number.
Each attempt uses build version `(1 + run_number / 100).(run_number % 100).attempt`,
using integer division, for example `1.1.1` then `1.1.2` for its rerun. The script
rejects overflow beyond Apple's four/two/two-digit components. Before the first
upload, confirm this series is above any build already uploaded for version 1.0;
coordinate all other uploaders because a workflow counter is local to its workflow.

The script checks profile team, exact app identity, lifetime, UUID, absence of
device/enterprise provisioning, distribution entitlements and one matching valid
imported distribution identity. It verifies the exported signature, bundle ID,
version/build/display name, signed debug entitlement and embedded profile UUID.
The App Release bundle ID/version and Info.plist display name are temporary
changes restored on exit; capacitor defaults and project defaults stay available
for the existing installation path. Windows can run helper/cleanup tests and
inspect workflow source; archive/export and actual signature acceptance require
macOS/Xcode. Those tests do not establish Apple upload or review acceptance.

## Upload and Apple review

The application exposes public `/support` and `/privacy` routes, also linked
from its footer. Publish the reviewed application and add their exact GET/HEAD
locations from `web/deploy/site-locations.conf` to the installed application
profile. Verify both deep links without a session before entering their canonical
HTTPS URLs in App Store Connect. The support email is public; reviewer phone
numbers and credentials belong only in Apple's private review fields.

Before submission, resolve these existing product requirements:

- Test account deletion from customer and staff settings. Fresh passkey assertion
  and explicit confirmation precede durable acceptance; the account loses access
  immediately and local content cleanup retries after failures/restarts. A separate
  protected seven-day receipt allows signed-out status confirmation through the
  public `/account-deletion` page. Deploy its exact private-header ingress too.
  Migrate the identity schema through the existing backup-preserving migration
  command before starting this backend version; never drop existing passkeys.
- Operator invitations expire after one hour and are single-use. They do not
  provide durable App Review access. Arrange a dedicated synthetic account and
  repeatable, tested sign-in instructions without publishing an owner credential.
- Hardware-dependent features need a genuine demonstration of the app on a
  physical Apple device interacting with the accessory. Synthetic browser
  screenshots and simulator evidence do not demonstrate physical operation.
  If submitting without this evidence, explain the hardware dependency accurately;
  Apple may request it before completing review.
- Configure `ApplicationIdentity:AppleAppIds` with the installed and App Store
  identities. The legacy scalar `AppleAppId` remains additive; malformed entries
  reject the whole AASA response. Verify the HTTPS response before enabling the
  new app so existing passkey access stays available.

Use the resulting IPA with Transporter on macOS or an authorized App Store
Connect upload integration. A release operator must provide an Apple-supported
authentication method with the required app access. No upload API key or Apple
password is provisioned or consumed by this workflow. Wait for Apple processing,
resolve any validation or compliance issues, then select the processed **1.0**
build in App Store Connect.

Before submission, complete the app's screenshots for supported devices,
description/category/age rating, support and privacy-policy URLs, privacy and
encryption declarations, content rights, availability and review contact details.
Review notes must explain hardware dependencies and provide working reviewer
access or an approved demonstration route; Apple cannot review an inaccessible
hardware-only experience. Verify privacy answers against actual microphone,
camera, account and server behavior. Submission, review approval, and App Store
release are separate states; an exported or uploaded IPA does not establish any
of them. Check the processed build on a real supported device before submission.
