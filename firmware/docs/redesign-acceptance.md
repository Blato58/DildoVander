# Firmware redesign software verification

This document maps the reviewed findings to current source and reproducible
checks. Hardware, real hotspot/iPhone, physical NVS cuts, provisioning, flashing,
paid provider and deployed WAN TLS checks remain separate. Do not interpret a
fake adapter, loopback socket, build or simulated clock as physical evidence.

## Findings and implementation

| Finding | Disposition and source boundary |
| --- | --- |
| F1 OTA versus inspection/offline boot | Fixed by passive image/configuration health and the asynchronous artifact-bound confirmation guard. Inspection and motion remain separate; WAN/IP is not boot-health evidence. See `ota-boot-health.md`. |
| F2 lifetime 32-operation journal | Fixed for explicitly established scoped devices by original immutable epochs, common committed-source reconciliation and bounded atomic compaction. A fully pinned backlog still refuses safely; unknown/lost stores require service. See `runtime-storage-owner.md`. |
| F3 per-batch microphone HTTPS | Superseded by authenticated persistent device WSS on ESP and the native shared actor. Host-only explicit fake compatibility is not a production fallback. |
| F4 synchronous network/playback | Fixed by shared asynchronous session/control ports, bounded independent playback ownership, immutable consumed/drained receipts and immediate cancellation fences. Profile B acoustic/DMA tail remains a measured hardware boundary. |
| F5 expected local refusal disconnects | Fixed by correlated negotiated refusals for busy, stale, recovery, lease and unsupported transitions, without lease renewal or action admission. Malformed/auth/protocol violations still close. |
| F6 mixed, unbounded control owner | Refactored to `DeviceRuntime`, absolute cadence and explicit reservations; TLS/server/HTTP scheduling and storage use workers. Urgent authenticated Stop latches OE outside bus waits. No hard real-time claim: conditional SDK bus recovery can exceed the nominal period. |
| F7 retired implementations/docs/seams | Removed ESP opening/text/poll and per-batch transport implementations, retained explicit upgrade rejection and supported correlation/cleanup, split current README from historical slices and added actual orchestration/fault checks. |

The optional [Lab carrier](lab-voice-carrier.md) has durable technical origin,
capability-gated RAM-only tickets and exact scoped cancellation. It cannot feed
the production keepsake inventory or obtain motion authority from an ACK.

## Reproducible commands

Run from the repository root, using the installed toolchains and pinned packages:

```powershell
python firmware/tests/run_show_tests.py
python firmware/tests/run_inventory_tests.py --cjson-dir <pinned-cJSON-directory>
pio run -e production
pio run -e diag-board-memory -e diag-hardware-service -e diag-network-service -e diag-pca9685-logic -e diag-servo-bench
python firmware/simulator/native/build_host.py --output <temporary-directory>/native-twin.exe
python firmware/simulator/native/test_media.py --executable <temporary-directory>/native-twin.exe
$env:KUFR_TWIN_TEST_NATIVE = '<temporary-directory>/native-twin.exe'
dotnet run --project backend-tests -- --operation-epochs
dotnet run --project backend-tests -- --device-media
dotnet run --project backend-tests -- --twin-media
dotnet run --project backend-tests
npm --prefix contracts test
npm --prefix mobilni-aplikace test
npm --prefix mobilni-aplikace run build
```

`KUFR_TWIN_TEST_NATIVE` points the isolated backend integration tests at the
fresh native executable. Their loopback fake providers never call a paid model.

`run_inventory_tests.py` verifies exact cJSON source hashes before compiling;
use the same pinned bytes as CI, not a substitute parser. Both host runners
select a real C++14 compiler and fail on missing checks. Do not execute two
`.pio`/CMake writers in one checkout. Native CMake may use an isolated temporary
build directory. No command above flashes, deploys, calls paid providers or
generates CAD. Native iOS tests/unsigned Simulator build use the existing macOS
CI workflow; a Windows web build cannot replace them.

## Persistence and producer/control seams

Registered production-startup tests exercise more than 1,000 manual, local,
online and offline lifecycles apiece, through actual control, pending claims,
storage worker and physical orchestration with simulated I/O. They retain old
proofs, compact repeatedly, restart, reuse UUIDs in new epochs and refuse old
envelopes. Before/after commit cuts include lost reserve/Begin/source/close ACKs.
No test is allowed to replace these producers with a direct journal loop and
then claim end-to-end production acceptance. Direct codec/engine tests are
retained and labelled separately. Actual NVS power loss remains a bench check.

## Transport fault workload and expected behavior

The native shared `DeviceMediaSession`/engine/playback actor connects through a
real loopback ClientWebSocket bridge with offline provider ports. Seed **82421**
applies **50-300 ms** jitter, a **500 ms** stall and a **2,500 ms** receive outage
to paced bounded output for both supported engines. Within that workload, exact
PCM and drain/played receipts are retained. This does not assert that an
arbitrary outage or unlimited producer rate is lossless.

Abrupt loss, malformed or duplicate/out-of-order output and a no-consumer burst
of 300 immediate 4,800-byte blocks fail explicitly within bounded capacity.
Stop remains available; no automatic reconnect or audio/action replay follows.
The stalled Stop test includes a delayed old callback, actor replacement and
an actual loopback backend restart. Recorded host Stop durations were **37 ms
Live** and **26 ms Realtime** under that fixture. These are observations, not a
board latency guarantee. Mapped real-socket tests separately cover bearer
expiry, uint64 epochs, input credit, control replay/conflict, ACK visibility and
stalled provider-upload cancellation. Fake TLS tests cover strict RFC6455
fragmentation, masks, UTF-8, upgrade validation, partial writes and failure.

## Four-hour simulated soak

`device_media_soak_test` advances **14,400,000 ms** with seed **0x42a79d01**:
1,440 ten-second sessions, alternating Live/Realtime, 720,000 control passes,
bounded 64 KiB playback workspace and separate fixed wire/capture queues. The
workload injects seeded 50-300 ms buffering, 18 explicit interruptions and
simulated selection/show cycling. It reports high-water marks, PCM and input
counts and asserts no post-Stop effect. This is four hours of **simulated time**
with fake socket/audio/clock ports, not four hours of WAN TLS or real speakers.

## Targets and remaining physical measurements

Nominal 10 ms control cadence, 20 ms software Stop admission-to-OE request and
50 ms application-message-to-audio-cancel are engineering targets. The control
owner records actual execution, lateness, skipped releases, gaps, physical bus
time and over-budget passes. IDF's conditional bus-clear path can consume about
50 ms independently of the configured short transfer wait; that unmet bound is
exposed rather than called hard real time. The GPIO-only Stop latch avoids the
bus path once its authenticated callback is scheduled. Hardware scheduling,
electrical OE latency and acoustic/DMA tail require measurement.

Use [runtime diagnostics](runtime-diagnostics.md) and the
[prepared exact-build bench checklist](firmware-reliability-bench.md). Record
actual commands/exits, source/artifact identities, workload and unrun cases;
do not close physical acceptance by changing its criteria.
