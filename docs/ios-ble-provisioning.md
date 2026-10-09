# iPhone BLE provisioning

The existing Capacitor app is extended; its **Síť kufru** tab uses a native
CoreBluetooth/ESPProvision 3.2.0 plugin. Safari/Web Bluetooth is not used.

1. Initially install firmware and run the separate USB
   [network service](../kufr/firmware/diagnostics/network-service/README.md)
   with actuators disconnected. Provision the device's random PoP and identity;
   retain the PoP privately with the device. It is not an advertised MAC/code.
2. Power the suitcase, open **Síť kufru**, scan and select `KUFR_…`.
3. Enter the device PoP in the native secure dialog. An incorrect PoP fails the
   official Security1 handshake. There is no unauthenticated credential endpoint.
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

Passwords/PoP are native-dialog input, not JavaScript state or retained Keychain
entries. SDK logging is disabled. Native buffers are released after submission;
no claim is made about guaranteed zeroing of Swift/OS/SDK copies. Diagnostics
whitelist public fields; submitted/saved passwords are never returned.

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
