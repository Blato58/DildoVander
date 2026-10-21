# Negotiated runtime diagnostics

The authenticated local control protocol advertises `runtimeDiagnostics` only
when `LocalControlOutput` has a bound diagnostic provider. A client opts in with
`state.get` and `includeDiagnostics:true`. The setting lasts for that admitted
connection and resets on disconnect. Legacy clients receive the existing
snapshot shape. Welcome itself remains unchanged; runtime metadata appears only
in later opted-in `state.snapshot` messages.

The optional `diagnostics` object is defined by
[`control-message-v1.schema.json`](../../contracts/control-message-v1.schema.json)
and emitted by
[`DeviceDiagnostics.cpp`](../src/features/device_runtime/DeviceDiagnostics.cpp).
All nested objects reject unknown properties. Ordinary control/snapshot messages
retain the 2,048-byte limit; a state snapshot containing diagnostics has a
4,096-byte UTF-8 ceiling. The fixed diagnostic object has its own 2,048-byte cap.
The generated shared validator enforces these boundaries for native clients.
Unavailable or contended RAM samples omit the optional object instead of
blocking the control task or inventing values.

`EspIdfControlServices` samples potentially costly heap, task-stack, station,
OTA and storage status at most once per second. The runtime separately
publishes control and physical-I/O counters after each pass. Both publications
use bounded 32-bit atomic words and a version check; readers obtain a coherent
copy or skip that attempt. `sampledAtMs` is the monotonic uptime at the start of
the service sample, not a wall-clock timestamp. Control counters can be newer.
The urgent Stop gate is read atomically when building the diagnostic copy.
No diagnostic read performs source I/O or waits for another worker.

| Fields | Meaning and units |
| --- | --- |
| `version`, `buildId`, `resetReason` | Version 1, compiled firmware build identity, and actual ESP-IDF reset-reason numeric code. |
| `profile`, `calibrationConfigured`, `hardwareAvailable` | Profile 0 means unavailable/unknown, 2 is the selected B profile. Calibration means startup configuration passed validation; it does not certify physical measurement. Availability is the adapter's current software gate. |
| `station` | Fixed phase enum, actual active profile enum (0 none, 1 hotspot, 2 fallback), IP availability and network-change state. No SSID, password or address is exported. |
| `backend` | Actual backend online observation, independently published authentication and trusted-time states. Unknown remains unknown; network/IP availability does not imply authorization or trusted time. |
| `ota` | Fixed allowlisted OTA state. It does not include server/provider error text or update credentials. |
| `storage` | Startup state (0 uninitialized, 1 service required, 2 unavailable, 3 established); runtime state (0 uninitialized, 1 identity unavailable, 2 inventory unavailable, 3 inspection required, 4 reconciliation failed, 5 ready, 6 capacity blocked, 255 absent). `journalKnown` distinguishes missing sample data from a real empty journal. Used/capacity count entries; operation epoch and compaction count are full uint64 decimal strings. `busy` includes worker, journal and evidence work. |
| `memory` | Current free bytes, SDK minimum-free aggregate and largest free block for internal 8-bit memory and PSRAM 8-bit memory. Zero PSRAM bytes do not assert installed hardware capacity. |
| `stack` | Minimum remaining bytes observed for the control, service and storage tasks on the pinned ESP-IDF target. These are high-water observations, not reservations or worst-case stack proofs. |
| `control` | Pass/missed-release counts; last/max execution, release lateness and start-to-start gap in microseconds. Owner enum: 0 idle, 1 recovery, 2 maintenance, 3 manual, 4 dance, 5 device show, 6 offline show, 7 presentation, 8 urgent Stop pending. Maintenance-owner enum: 0 none, 3 network, 4 OTA, 5 pairing/service startup. |
| `physical` | Largest measured SDK transfer and summed per-pass bus time in microseconds; lock/deadline refusals; coalesced LED requests; passes whose summed bus time exceeded the 8 ms admission budget; current urgent Stop gate. These measure software calls, not actual actuator motion. |
| `storageMaxOperationUs` | Largest storage-worker operation duration observed in microseconds. |
| `mediaAvailable`, `media` | Availability requires a coherent media sample with a nonzero logical session counter. Session, cancellation epoch and played PCM byte frontier are uint64 decimal strings. Capture/playback occupancy/high water and capture overrun counts are software queue facts. Termination is 0 idle, 1 active, 2 completed, 3 cancelled, 4 failed. Unavailable media emits a zeroed object. `cancellationTail` remains `unmeasured`; played PCM does not establish speaker/DMA drain or acoustic silence. |

Diagnostic snapshots contain fixed enums and numeric observations only. They
never include bearer tokens, pairing material, network credentials, operation
IDs, journal records, visitor memories, photographs, PCM data, provider messages
or Lab ticket contents. The epoch and media counters are numeric scope/frontier
facts, not those identifiers. Counter histories reset with the device boot;
32-bit control timing counters saturate while some adapter counters wrap.

The played byte frontier belongs to the current playback generation; it is not
a lifetime session total. The reported cancellation epoch is the outer voice
owner's cancellation fence. Neither observation authorizes an action.
The raw I2S port does not provide an underrun observation here; no zero-underrun
claim is inferred from a successful enqueue. Measure underruns and audible gaps
alongside the unmeasured DMA/acoustic tail during the bench workload. The outer
snapshot supplies boot ID, protocol, engine and actual calibration revision.

The SDK bus-clear caveat and unmeasured physical acceptance targets remain in
[bounded-runtime.md](bounded-runtime.md). No diagnostic field converts those
open hardware checks into completed acceptance.
