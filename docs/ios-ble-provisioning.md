# iPhone BLE provisioning

The existing Capacitor app is extended; its **Síť kufru** tab uses a native
CoreBluetooth/ESPProvision 3.2.0 plugin. Safari/Web Bluetooth is not used.

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

LAN discovery requires more than BLE Wi-Fi setup: the suitcase must also have
its local TLS identity and local pairing provisioned. Firmware advertises
`_kufr._tcp` only after its authenticated local TLS server starts. The USB
network-service provisioning tool does not install these local TLS/pairing
records. An empty LAN scan on such a device cannot be fixed by entering its IP.
Use the same LAN with local-network permission and provision the local identity
through the separate service process before expecting WSS discovery/pairing.

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

Current verified delivery: iOS1.0/build1000.0.19 at
[Safari installation](https://2.28.202.220/ios/), signed/native run
[37899385679](https://github.com/Blato58/DildoVander/actions/runs/37899385679)
and public manifest/IPA/checksum verification
[37899776001](https://github.com/Blato58/DildoVander/actions/runs/37899776001).
The initial registered iPhone profile is still required. During D159 on
9 October 2026, the owner confirmed discovery and a successful PoP-authenticated
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
