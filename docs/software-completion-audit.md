# Software completion audit and implementation report

Audit date: 9 October 2026. Scope: the current `kufr/` firmware, backend,
mobile companion, contracts, simulator, offline AI evaluation, service tools
and deployment workflows. `obludarium/` remains an independent Arduino Nano
starter project, outside the suitcase implementation scope.

The implementation plan and [implementation status](../kufr/docs/stav-implementace.md)
remain authoritative for stage acceptance. Historical acceptance is not treated
as a fresh test. This report distinguishes executable source, host verification,
deployment and physical acceptance.

## Functional inventory and prioritized findings

| Subsystem / evidence | Initial finding and impact | Priority | Implementation and dependencies | Verification / readiness |
| --- | --- | --- | --- | --- |
| Boot, `firmware/src/espidf_main.cpp` | Existing safe outputs, journal recovery and fail-closed NVS; no destructive automatic erase | P0 | Preserved. Optional peripherals remain gated by service measurements | Builds and host safety tests; hardware acceptance remains separate |
| Wi-Fi, `features/backend_access/EspIdfStationNetwork.*` | One USB-configured network; no phone credential replacement or fallback | P0 | Added persistent hotspot/fallback profiles, timeout/backoff, tested replacement, rollback to old credentials, idle reservation | Host station/profile tests; actual hotspot/radio not verified |
| BLE, `features/network_provisioning/` | No BLE provisioning/runtime | P0 | Official ESP-IDF protocomm Security1, per-device 128-bit PoP, bounded advertising windows, encrypted custom endpoint, compact 512-byte wire | Host adapter/real cJSON tests and firmware build; physical handshake pending |
| iOS, `mobilni-aplikace/ios/App/App/KufrProvisioningPlugin.swift` | Existing Capacitor app had LAN pairing and Debug, no native BLE | P0 | Extended existing app with official ESPProvision 3.2.0/CoreBluetooth, native secret entry, discovery, status, replacement/delete/test/OTA controls | Web tests/build/browser passed; full Swift/Xcode and physical acceptance recorded separately |
| Backend identity, `features/provisioning/DeviceIdentity.*` | Per-device credential and trusted TLS already implemented; factory device needs service initialization | P0 | Preserved identity/token boundary; service tool now makes initial network/PoP/OTA settings concrete without embedding secrets | Source and backend access tests; real ESP identity provisioning pending |
| OTA, `features/firmware_update/` | Dual slots existed, no updater or rollback acceptance flow | P0 | Signed manifest, strict hardware/channel/version/size, certificate-validated HTTPS, inactive-slot writes, exact SHA, scheduled idle restart, bounded boot health and rollback | Pure/adapter tests, real release-signature verification, firmware build; power-loss/rollback hardware tests pending |
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
| Observability, `backend/Features/Diagnostics/`, native Debug | Technical correlation/log reader exists; firmware network/OTA visibility missing | P1 | Added secret-free encrypted BLE diagnostics and update states; preserved server reader separation | Backend/native codec tests; operational handset check pending |
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
build/live-server or outstanding physical checks.

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
