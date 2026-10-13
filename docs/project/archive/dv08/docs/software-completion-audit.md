# Software completion audit and implementation report

Audit date: 9 October 2026. Scope: the current `kufr/` firmware, backend,
mobile companion, contracts, simulator, offline AI evaluation, service tools
and deployment workflows. `obludarium/` remains an independent Arduino Nano
starter project, outside the suitcase implementation scope.

**Current handoff: D166.**

The owner entered `192.168.0.59` manually and saw the service-state UI, which
requires a valid snapshot: that evidence confirms authenticated pinned WSS through
IP. Hostname timeout was observed; the exact iPhone DNS/multicast cause is unproven.
D166 exposes optional current STA IPv4 `n.ip` only through secured BLE. Offline or
generation changes omit it; at the 512-byte limit only this optional hint is dropped.
The native RAM hint is bound to session/device ID for 60 seconds. Connect refreshes
a live BLE session even with its panel hidden; pending/skipped/failed/stale refresh
aborts the attempt. A failed refresh clears logical BLE state so a later tap can
use the saved/manual route. Manual address takes precedence. Host/device ID are
captured before listener await; native validation accepts both valid matching
fields or both absent. The same Keychain pin/token authenticate WSS; route is saved
only after authenticated onOpen. Uncertain-command and hardware gates remain intact.

[PR165](https://github.com/Blato58/DildoVander/pull/165) merged as
`b22f478ffcba03bf052b8073a9ed4fd1a53be47b`, from source
`e4d215cfe10b2d293c74cdf9e581efc974ae80a4`.
[CI 37945851773](https://github.com/Blato58/DildoVander/actions/runs/37945851773), run number 167,
passed; CI/main share tree `dc21879478bd6f691b67b3c8934e2d731f8e0f6f`.
[OTA publication 37946520625](https://github.com/Blato58/DildoVander/actions/runs/37946520625)
published **0.1.168**, buildId `a228b02f1a1881065470680c26081f8ca8884f6a`, 1745456 B,
SHA-256 `a83069da0bf9af357bbf8613f85eedb1455e838ced225f5f7c7f08a55d9ddc31`.
Public HTTPS, ECDSA signature, hash and embedded version were verified; N16R8
image/partition checks passed.
[iOS signing 37946514768](https://github.com/Blato58/DildoVander/actions/runs/37946514768)
passed all three native harnesses (pinned LAN/Keychain/WSS/captured route, native
Debug backend and secured BLE whitelist), Xcode archive and Ad Hoc export.
The signed IPA has verified Bundle ID `app.turquoise6409.green2444` and version
**1.0 / build 1000.0.23**.
[Safari publication 37947083377](https://github.com/Blato58/DildoVander/actions/runs/37947083377)
passed all jobs. The public [installation page](https://2.28.202.220/ios/)
and manifest confirm **1000.0.23**; public IPA and checksum match the signed artifact.
The application-only **0.1.168** write to COM7 at `0x10000` passed hash verification;
NVS, otadata and the partition table were preserved. Normal RTS boot confirmed
**0.1.168**, control-loop startup and the local TLS server; a PC BLE scan found
`KUFR_4B6048` with the expected service UUID. The current delivered combination is
**0.1.168 / 1000.0.23**. D165 **0.1.162 / 1000.0.22** remains historical. Exact receipts remain in
[implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).
The owner answered **“Connection worked”** to the exact test request to update
to **1000.0.23**, use secured BLE with the saved pairing and connect without
typing an IP. This confirms automatic D166 connection in that trial. Next:
physical calibration, sustained WSS, Keychain retention after restart/update and
negative DHCP/network/lifecycle cases. Full physical acceptance remains pending.

**Historical D165 handoff.** Forgetting the iPhone's local
Keychain profile left the ESP paired, so subsequent first-use BLE pairing returned
`already_paired`. D165 adds a native confirmation before authenticated
Security1/PoP `recover-local` with exact `confirm:true`. Healthy Paired records alone
can rotate to a fresh digest; pending QR, Missing and corrupt records stay blocked.
Recovery holds maintenance, admission, inbox and credential gates, rejecting live
or pending controllers, queued/disconnect events, manual lease, voice and maintenance.
It preserves TLS identity and Wi-Fi settings, keeps pin/token native and inserts
into an empty Keychain profile without overwriting an existing credential.
Cancel, reconnect and failed enrollment do not automatically rotate authority.

[PR163](https://github.com/Blato58/DildoVander/pull/163) merged as
`f752c1e0009d25b6827657f789bd35ff943c74f4`, from source
`50b6b0605279884be5c2ed969254e2a57f32411a`;
[CI 37936213640](https://github.com/Blato58/DildoVander/actions/runs/37936213640)
passed with firmware **0.1.162**.
[OTA publication 37936948294](https://github.com/Blato58/DildoVander/actions/runs/37936948294)
succeeded: buildId `a3426c091ce727418e8e7252d3f66b3087ddf5c3`, 1744688 B,
SHA-256 `0345a302c0170ded0ba80e4d4f5a5ff0bba5a06a4e3457bc0768a4bb2eab682b`.
Public HTTPS, ECDSA signature, hash and embedded version were verified. COM7
app-only recovery at `0x10000` was hash-verified without touching NVS. Normal RTS
boot confirmed **0.1.162**, 16384 B main stack / minimum remaining 12540 B,
BLE advertising, control loop and TLS startup at 18899 ms.
[iOS signing 37936943860](https://github.com/Blato58/DildoVander/actions/runs/37936943860)
passed all three native harnesses, Xcode archive and Ad Hoc export for
**1.0 / build 1000.0.22**. [Safari publication 37937381222](https://github.com/Blato58/DildoVander/actions/runs/37937381222)
succeeded in all three jobs. The public installation page and manifest identify
`app.turquoise6409.green2444` / **1000.0.22**; public IPA 1873296 B and its checksum
were verified, SHA-256 `f5cb611018ea898b215701bda0982c76dcd0dff7d1738421e9d13299fb39ff3e`.
The post-flash PC BLE scan found `KUFR_4B6048` with the expected service UUID.
The owner subsequently confirmed pairing again, but LAN discovery/connection
still fail. PC Bonjour and unauthenticated TLS are reachable; phone token/pin,
stable WSS and Keychain retention remain unverified. Exact delivery and acceptance
are tracked in [implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).

The later direct-IP test reached the device service-state UI; source emits that
only after a valid snapshot, so this is evidence of a direct-IP WSS connection.
The hostname timed out. Hardware/calibration remains gated; automatic address
selection and long-running WSS/Keychain checks remain open.

**Historical D164 handoff.**
[PR161](https://github.com/Blato58/DildoVander/pull/161) merged as
`12efd65fa40d12cda2c6041e7590b10c600d772c`;
[CI 37931935316](https://github.com/Blato58/DildoVander/actions/runs/37931935316)
passed with run number 160 and development OTA **0.1.161**.
[Publication 37933188173](https://github.com/Blato58/DildoVander/actions/runs/37933188173)
succeeded: buildId `56413430197b8becdcaa03c19658a2f24dbf5360`, 1743920 B,
SHA-256 `119c799fc6b31b96be891f9b5356483714d6e8ae14eb09748604f8deb5871c52`.
Exact receipts and the public verification result are tracked in
[implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).

The owner's approved app-only USB recovery on COM7, with actuator power off,
verified the write at `0x10000` on ESP32-S3 rev. 0.2 / N16R8 and matching partition
table. Private partition/NVS/otadata backups have owner-only ACLs; the flasher did
not erase or rewrite NVS. A deliberate normal RTS reboot confirmed **0.1.161**,
16384 B configured / 12556 B minimum remaining, control loop startup, BLE
advertising and local TLS startup after about 19 seconds. Active PC scanning found
`KUFR_4B6048` with the expected service UUID; before recovery **0.1.157** overflowed
the 3584 B main stack before BLE startup. This verifies USB recovery and startup,
not a new OTA installation or complete LAN control acceptance.

At D164, iOS remained **1.0 / build 1000.0.21** from the D163 signing/publication below;
D164 did not sign or publish it again. The owner confirmed the fresh
same-Wi-Fi iPhone control path: "yes I am connected". Connection is confirmed;
long-running pinned WSS and post-update Keychain retention remain pending.

**Historical D163 handoff.**
[PR159](https://github.com/Blato58/DildoVander/pull/159) merged as
`5f742b24364b9564b729628ef5cd620ae601fb90`; [CI 37928528451](https://github.com/Blato58/DildoVander/actions/runs/37928528451)
passed. Development OTA **0.1.160** is
[published 37929155208](https://github.com/Blato58/DildoVander/actions/runs/37929155208),
with public signature/hash/embedded version verified. iOS **1.0 / build 1000.0.21**
passed [native harnesses and signing 37929159117](https://github.com/Blato58/DildoVander/actions/runs/37929159117)
and [Safari publication 37929535139](https://github.com/Blato58/DildoVander/actions/runs/37929535139);
the installation page, manifest, public IPA and checksum were verified.
The firmware now accepts extension offers without
negotiating them; four Apple WSS harness requests offered extensions. Native
failure diagnostics and reconnect are bounded. The owner's reported scan/pairing
success and repeated same-Wi-Fi Ovládání disconnect still require a physical retest.
Exact receipts and acceptance are in
[implementation status](../kufr/docs/stav-implementace.md#předání-další-ai).

**Historical D162 publication receipt:** the receipt in
[implementation status](../kufr/docs/stav-implementace.md#předání-další-ai)
records development OTA **0.1.157** from
[publish run 37923929435](https://github.com/Blato58/DildoVander/actions/runs/37923929435)
and iOS **1.0 / build 1000.0.20** from
[signing run 37923948887](https://github.com/Blato58/DildoVander/actions/runs/37923948887)
and [publish run 37924314161](https://github.com/Blato58/DildoVander/actions/runs/37924314161).
These publication receipts do not establish installation or physical acceptance.
The owner's subsequent report confirmed scanning and pairing work, but LAN
connection repeatedly disconnected. D163 fixes a source incompatibility;
physical acceptance remains pending.

The implementation plan and [implementation status](../kufr/docs/stav-implementace.md)
remain authoritative for stage acceptance. Historical acceptance is not treated
as a fresh test. This report distinguishes executable source, host verification,
deployment and physical acceptance.

**Historical D158/D159 delivery and physical evidence:** the software was merged
and delivered: complete firmware/backend/web CI,
macOS native suites and signed Ad Hoc archive/export passed. Backend,
development OTA0.1.1 and iOS1.0/build1000.0.19 are published with verified
HTTPS readback. Exact commits, hashes and workflow receipts are in the status.
Production stable signing and full physical event acceptance remain separate
requirements. D159 subsequently installed the verified CI image on the physical
ESP32-S3, provisioned its identity/PoP/development OTA trust and verified NVS
readback plus backend authentication from the service computer. The owner
confirmed native BLE connection and home Wi-Fi reconnection after reset. Physical
testing exposed an SDK6.1 empty-body token setup bug; firmware0.1.1 removes the
failing call and adds a reproduced adapter regression. The board automatically
installed the signed0.1.1 update in ota_1, confirmed its boot VALID and retained
all nine provisioned identity/PoP/network/OTA keys. Real ESP token POST200 passed
both after OTA and another reset; the owner confirmed connected Wi-Fi/backend
on the iPhone. Hotspot transitions, OTA power-loss/negative verification/rollback
and loaded peripherals remain unverified. No paid inference was performed.

## Functional inventory and prioritized findings (D158/D159 snapshot)

| Subsystem / evidence | Initial finding and impact | Priority | Implementation and dependencies | Verification / readiness |
| --- | --- | --- | --- | --- |
| Boot, `firmware/src/espidf_main.cpp` | Existing safe outputs, journal recovery and fail-closed NVS; no destructive automatic erase | P0 | Preserved. Optional peripherals remain gated by service measurements | Builds and host safety tests; hardware acceptance remains separate |
| Wi-Fi, `features/backend_access/EspIdfStationNetwork.*` | One USB-configured network; no phone credential replacement or fallback | P0 | Added persistent hotspot/fallback profiles, timeout/backoff, tested replacement, rollback to old credentials, idle reservation | Host station/profile tests; D159 owner-confirmed home Wi-Fi plus physical NVS readback; hotspot/failover pending |
| BLE, `features/network_provisioning/` | No BLE provisioning/runtime | P0 | Official ESP-IDF protocomm Security1, per-device 128-bit PoP, bounded advertising windows, encrypted custom endpoint, compact 512-byte wire | Host adapter/real cJSON tests and firmware build; D159 owner-confirmed physical discovery/authorized handshake; wrong-PoP test pending |
| iOS, `mobilni-aplikace/ios/App/App/KufrProvisioningPlugin.swift` | Existing Capacitor app had LAN pairing and Debug, no native BLE | P0 | Extended existing app with official ESPProvision 3.2.0/CoreBluetooth, native secret entry, discovery, status, replacement/delete/test/OTA controls | Web tests/build/browser passed; full Swift/Xcode and physical acceptance recorded separately |
| Backend identity, `features/provisioning/DeviceIdentity.*`, `backend_access/EspIdfTokenExchangeTransport.cpp` | Factory identity needs service initialization; physical test found SDK6.1 rejects empty post-field setup before token request | P0 | Preserved identity/token boundary; D159 provisioned the board and removes the failing empty-body call in0.1.1 | USB/NVS, additive backend registration and service-PC HTTPS exchange passed. Token regression reproduces old failure/new pass; actual ESP POST200 and owner-confirmed backend passed after OTA and another reset |
| OTA, `features/firmware_update/` | Dual slots existed, no updater or rollback acceptance flow | P0 | Signed manifest, strict hardware/channel/version/size, certificate-validated HTTPS, inactive-slot writes, exact SHA, scheduled idle restart, bounded boot health and rollback | Pure/adapter tests, public signature/image verification and actual0.1.0→0.1.1 OTA/VALID boot/NVS retention passed; power-loss/negative verification/rollback hardware tests pending |
| OTA distribution, `backend/Features/Firmware/FirmwareReleases.cs` | No firmware release endpoint/pipeline | P0 | Read-only signed manifests/artifacts in existing backend, per-channel trust, bounded concurrency; root-owned signing/publication helper and provenance reuse workflow | Backend tests and packaging tests; live deployment evidence appended in status |
| HTTP photo rejection, `CostumeImageEndpoints.cs` | Linux smoke exposed an existing oversized-body rejection that could advertise a socket Kestrel then closes | P2 | Explicit HTTP/1 Connection: close on unread/overflow 413; preserves body limits and HTTP/2 semantics | Added declared/chunked/HTTP2 regression and repeated HTTP oversized/next-request test |
| GPT-Live, `backend/Features/GptLive/` | Implemented autonomous actor and isolated official wire adapter; fake mode intentional | P0 | Preserved GPT-Live responsibilities, Sol decisions, playback checkpoint, cancellation, tool validation | Existing offline runner passes; no new paid inference or human conversational acceptance |
| Personalities/catalog, `backend/Features/Conversations/PersonalityCatalog.cs`, `ai-eval/` | Existing durable selection/catalog and prompts | P0 | Preserved unchanged | Existing personality/catalog tests; no new persona replacement |
| Audio, `features/voice_turn/`, `backend/Features/Realtime/` | Existing I2S/PCM stream, drain ACK and half-duplex gates; hardware/AEC unverified | P0 | Preserved B-effects audio pins and both amplifier mono configurations | Host/backend suites; actual microphone, speakers, clipping, echo and latency pending |
| Effects, `features/physical_effects/`, `wand_presentation/` | PCA/MCP/LED/servo/U059 integrated; physical calibration incomplete | P0 | Preserved direct OE GPIO2, one actuator, 64/255 brightness, bounded timed motion and mandatory inspection after fault | Host tests/build; calibrated movement and power/load tests pending |
| Recovery/concurrency, `local_control/LocalControlOwner.*` | No admission exclusion for future OTA/network I/O | P0 | Common owner reservation blocks new manual/text/voice/presentation starts; Stop/status stay usable; BLE lifecycle moved off safety loop | New maintenance regression and adapter tests; long radio/audio/actuator soak pending |
| Camera, `features/costume_photo/`, `docs/kamera.md` | Latest source already has OV5640 one-shot upload; older wiring docs still said missing | P1 | Preserved consent, PSRAM framebuffer, isolated I2C1, cancel/cleanup; corrected stale documentation | Existing fake/driver/build checks; camera/FPC not yet physically accepted |
| MicroSD, `features/sd_assets/` | Protected pins documented, storage driver absent | P1 | Added gated 1-bit CMD38/CLK39/DATA40, safe worker, bounded flat-name reads, no formatting or outbound card writes | Host adapter/build checks; SD revision/card/pullups/simultaneous loads pending |
| Crash/watchdog, safety/journal and IDF configuration | Control-pass fault bound exists; no project hardware watchdog required by current owner design | P1 | Preserved software bound and persistent pending-command recovery; added OTA first-boot recovery | Source/tests. No claim of independent hardware inhibit or measured reset silence |
| Memory/PSRAM, `sdkconfig.defaults`, camera/audio adapters | Octal PSRAM and bounded queues exist; no physical integrated stress measurement | P1 | Preserved camera/SD/USB allocations; BLE and updater get separate bounded workers | Binary/config checks; heap, DMA, thermal/current and duration measurements pending |
| Observability, `backend/Features/Diagnostics/`, native Debug | Technical correlation/log reader exists; firmware network/OTA visibility missing | P1 | Added secret-free encrypted BLE diagnostics and update states; preserved server reader separation | Backend/native codec tests; D159 owner-confirmed Wi-Fi/backend BLE status; OTA/error/recovery handset checks pending |
| Durable session state, `OperationJournal`, evening/hybrid memory | Persistent journal/evenings exist; active streams are intentionally RAM-scoped and cancelled on restart | P1 | Preserved idempotence, limits, cleanup and deliberate restart behavior | Existing full backend runner. No promise of resuming an interrupted spoken turn |
| CI/deploy, `.github/workflows/`, `scripts/ci/` | Web/backend/firmware and signed iOS paths exist; no signed firmware publisher | P1 | Extended existing paths; no competing OTA service or simulator signing pipeline | Local checks and actual CI/publish evidence separately recorded |
| Provisioned-data security | Current production build does not enable flash encryption/secure boot | P1 | No false encryption claim. Credentials remain in NVS and survive ordinary OTA; physical USB/flash access is a service trust boundary | Encrypted BLE/HTTPS implemented; physical-at-rest protection needs deliberate eFuse/key provisioning |
| Battery/power telemetry | PB720 capacity/runtime telemetry has no verified hardware/API contract | P3 | Documented measurement task; no invented driver or power estimate | Not implemented; not a core software requirement |
| Removed endstops/external watchdog/high-side modules | Older notes describe cancelled hardware | P3 | Explicitly obsolete per D156; not restored | Latest source/wiring contract preserved |

Delivery review found three P1 defects in the new OTA path: the privileged signer
needed independent GitHub artifact verification before signing, and first-boot
confirmation needed calibrated effects health, and nested download/hash buffers
exceeded the OTA worker stack. These are corrected with rejected artifact,
boot-health and single heap-buffer/allocation-failure regressions. BLE startup
retries independently of Wi-Fi within its original boot deadline, failed teardown
is retried before reopening, and legacy credential cleanup failures propagate
with one bounded previous-profile restore. Native empty scans correctly return
an empty list. Package pins, partition flags and fallback probe timing are fixed.
Actual Linux/MSVC checks and the latest CI/review receipts are in the status.

## Architecture and compatibility

The ESP remains the owner of conversation and physical sequences. The phone
configures and observes it; disconnecting an observer does not cancel the
standalone conversation. The backend returns validated data and never drives
GPIO. The personality catalog, existing local pinned TLS pairing and GPT-Live
flows remain intact.

BLE Security1 uses the official X25519/PoP/AES-CTR implementation on both sides.
The custom endpoint carries management JSON inside that authenticated session,
not passwords in plaintext characteristics. The native app expands compact
wire fields into a credential-free whitelist for JavaScript. Provisioning PoP
is distinct from the local WSS bearer and backend device credential.

The Wi-Fi profiles are one versioned NVS blob. Candidate credentials are tested
before publication. Failed connection retains the previous record; storage
failure keeps the old RAM state and attempts one persistent restoration. A
persistent NVS fault can defeat that rollback and requires inspection/repair.
Legacy Wi-Fi settings migrate and the immutable backend host is preserved.
OTA and optional SD share the owner admission boundary with network changes.

OTA authenticates exact manifest bytes with DER ECDSA P-256/SHA-256, then verifies
the entire downloaded artifact against the signed digest. SHA alone is not
authentication. Public keys are service-provisioned; private keys remain outside
source/firmware/mobile. Production and development authorities are separate.
The original 16MB partition offsets and filesystem space remain intact.

## Verification and remaining acceptance

Exact final commands, results, commits and live publication are recorded in
[implementation status](../kufr/docs/stav-implementace.md). The test matrix in
[event readiness](event-readiness.md) maps all requested scenarios to host,
build/live-server or outstanding physical checks. Its D158/D159 results remain
historical evidence. D164 adds the explicitly recorded USB recovery, boot,
PC BLE scan and owner-confirmed iPhone connection above. That connection predates
forgetting. D165 subsequently adds owner-confirmed re-pairing and a valid-device
service-state UI after using the direct IP; hostname discovery/connection still fail.
D166 implements the secured BLE IPv4 route; the owner confirmed automatic iPhone
connection in the instructed update/BLE/address-free trial. Long-running WSS,
restart/post-update Keychain, negative DHCP/network, OTA-negative and full-event
acceptance remain pending.

Software tests cannot establish actual CoreBluetooth/radio coexistence, iPhone
hotspot behavior, power-loss rollback, NVS physical security, measured servo
position or audio/camera/SD load behavior. Initial OTA-capable bootloader/app
installation still requires the controlled USB procedure. No automatic OTA
partition-table migration is claimed.

## Build, delivery and operation

Use [iOS BLE provisioning](ios-ble-provisioning.md),
[networking](device-networking.md), [firmware OTA](firmware-ota.md),
[hardware integration](hardware-software-integration.md) and
[event checklist](event-readiness.md). These describe actual source paths and
explicitly identify signing, device, measurement and initial-install dependencies.
