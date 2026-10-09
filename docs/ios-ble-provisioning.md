# iPhone BLE provisioning

The existing Capacitor app is extended; its **Síť kufru** tab uses a native
CoreBluetooth/ESPProvision 3.2.0 plugin. Safari/Web Bluetooth is not used.

**Current handoff: D164.** Development OTA **0.1.161** passed
[CI 37931935316](https://github.com/Blato58/DildoVander/actions/runs/37931935316)
and [publication 37933188173](https://github.com/Blato58/DildoVander/actions/runs/37933188173).
[PR161](https://github.com/Blato58/DildoVander/pull/161) increases the main task
stack from 3584 B to 16 KiB for loading an existing TLS identity. With actuator
power off and the owner's explicit approval, app-only USB recovery at `0x10000`
retained NVS. A deliberate normal RTS reboot of the real ESP32-S3 rev. 0.2 / N16R8
confirmed **0.1.161**, 16384 B configured / 12556 B minimum remaining, the control
loop, BLE advertising and the local TLS server after about 19 seconds. An active
PC scan found `KUFR_4B6048` with the expected service UUID; the previous **0.1.157**
main stack overflow prevented BLE startup.

iOS remains **1.0 / build 1000.0.21**, signed and published by the D163 runs below;
it was not signed or published again in D164. The owner confirmed the fresh same-Wi-Fi
iPhone test: "yes I am connected". This confirms connection through Ovládání;
long-running WSS and Keychain retention after update remain pending. Exact receipts and public verification are in
[implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).

**Historical D163 handoff.** Development OTA **0.1.160** is
[published](https://github.com/Blato58/DildoVander/actions/runs/37929155208), with
public signature, hash and embedded version verified. iOS **1.0 / build 1000.0.21**
passed [native tests and signing 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
and [Safari publication 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139).
The [installation page](https://2.28.202.220/ios/), manifest, public IPA and checksum
were verified for this build. The firmware accepts WebSocket extension
offers without negotiating extensions; native errors are sanitized and reconnect
is bounded. The Apple harness observed extension offers in four actual requests.
The owner's same-Wi-Fi report still requires physical retesting: scan/pairing
worked, then Ovládání disconnected repeatedly. Receipts and acceptance
remain in [implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).

1. Initially install firmware and run the separate USB
   [network service](../kufr/firmware/diagnostics/network-service/README.md)
   with actuators disconnected. Provision the device's random PoP and identity;
   retain the PoP privately with the device. It is not an advertised MAC/code.
2. Power the suitcase, open **Síť kufru**, scan and select `KUFR_…`.
3. Enter the device PoP in the native secure dialog. An incorrect PoP fails the
   official Security1 handshake. A successfully authenticated PoP is saved in
   this iPhone's non-synchronizing Keychain for that BLE suitcase. On reconnect,
   choose the saved PoP or enter a replacement; a replacement is saved only
   after successful authentication. There is no unauthenticated credential endpoint.
4. On the iPhone open Settings → Personal Hotspot and enable **Allow Others to
   Join**. Enable **Maximize Compatibility** for ESP32's 2.4GHz Wi-Fi. The app
   cannot enable the hotspot, read its password or guarantee discovery of SSID.
5. Select the preferred hotspot profile and enter SSID/password in the native
   dialog. The app provides a supported app-settings link; it does not use a
   private hotspot Settings URL. Enter the iPhone's actual hotspot name manually.
6. Observe `connecting`, then `connected` or `connection_failed`. A BLE command
   ACK means queued acceptance; final network status is a separate result.
7. Configure an optional regular Wi-Fi fallback in the same way. Deletion has
   an explicit native confirmation; it deletes only the selected network profile.
8. **Test** reconnects the selected saved profile under the idle gate.
   **Check update**, **Install** and automatic-install policy manage signed OTA.

The same iPhone may supply the hotspot and configure BLE; it does not join its
own hotspot as a Wi-Fi client. Actual behavior must be checked on the intended
iPhone/iOS/carrier. If LAN control is unavailable from the hotspot provider,
BLE diagnostics still work during its window and the ESP talks directly to the
backend. Existing LAN pairing is a separate workflow/credential.

Advertisements open for five minutes after boot, plus one five-minute recovery
window after a minute offline per boot. A subsequent missed window requires a
deliberate power cycle. They are never permanently unrestricted. Every custom
request still needs the PoP-authenticated session. Nearby access with an unknown
PoP cannot configure Wi-Fi; denial-of-service/radio interference is not prevented.

Wi-Fi passwords and PoP are native-dialog input, never JavaScript state.
Wi-Fi passwords are not retained on the phone. PoP uses a separate Keychain
namespace, accessible only while unlocked and only on this device, without
iCloud synchronization. SDK logging is disabled. Session buffers are released;
no claim is made about guaranteed zeroing of Swift/OS/SDK copies. Diagnostics
whitelist public fields; submitted/saved passwords are never returned.

LAN discovery requires more than BLE Wi-Fi setup. Since D161, healthy empty
local TLS storage is initialized at boot with a unique EC identity. Firmware
advertises `_kufr._tcp` after its TLS server starts, even before first local pairing.
Once PoP-authenticated BLE connects, **Spárovat ovládání přes Wi-Fi** issues the
first local token and transfers the address/pin/token only to native Keychain.
Existing or corrupt records are not replaced automatically; recovery remains a
physical service operation. Use the same LAN and allow local-network access.
Discovery or an address alone does not authenticate the subsequent pinned WSS.

Protocol: service `b97a7f10-70e8-4b98-9dcc-40d66395c821`; characteristics
`b97aff50-…` (`proto-ver`), `b97aff51-…` (`prov-session`), `b97aff54-…`
(`kufr-control`). Official `sec_ver=1`, PoP mandatory; no `no_pop` capability.
Plain management JSON inside the secure session is at most 512 bytes. Status
wire keys are `{v,ok,error?,id,fw,n:{p,r,a,l,h,f},b,o:{s,a,e,i}}`.
The Swift codec projects the existing rich network/update objects.

Build on Windows: `npm ci`, `npm test`, `npm run build` in
`kufr/mobilni-aplikace` (install `kufr/contracts` dependencies too).
Native source/permissions/package registration live in the existing Xcode
project. Use the existing `kufr-ios-sign.yml` workflow with a verified web CI run;
it resolves the pinned Swift package, runs native tests and signs the app.
Xcode or a signed archive does not establish physical BLE/hotspot acceptance.

Historical D162 delivery: iOS1.0/build1000.0.20 at
[Safari installation](https://2.28.202.220/ios/), signed/native run
[37923948887](https://github.com/Blato58/DildoVander/actions/runs/37923948887)
and public manifest/IPA/checksum verification
[37924314161](https://github.com/Blato58/DildoVander/actions/runs/37924314161).
D161 adds one-button Wi-Fi control pairing over authenticated BLE, with
credentials stored only in native Keychain; development OTA0.1.157 is published.
After publication the owner confirmed scanning and pairing, then repeated LAN disconnects in Ovládání on the same Wi-Fi. Exact installed versions are not documented; pinned WSS and Keychain retention remain open.
The initial registered iPhone profile is still required. During D159 on
9 October 2026 with iOS build1000.0.19, the owner confirmed discovery and a successful PoP-authenticated
connection to the provisioned physical ESP32-S3, then entered home Wi-Fi privately
as the fallback profile. The profile blob was independently read back from NVS
without exposing its SSID/password; the owner confirmed Wi-Fi reconnection after
reset without re-entering credentials. The initial reported backend connection
did not hold after reset. UART/server evidence identified an SDK6.1 bodyless token
POST setup failure, fixed in firmware0.1.1 with a reproduced regression. The board
then automatically installed the signed development0.1.1 update, confirmed its
new boot slot VALID and retained all identity/PoP/network/OTA settings. Actual
ESP token POST200 was verified before and after another hardware reset; the owner
then confirmed **Wi-Fi connected; Backend připojený** on the iPhone. Actual
iPhone hotspot, wrong-PoP rejection and hotspot/fallback transitions remain
unverified; this home-network setup does not establish their acceptance.
