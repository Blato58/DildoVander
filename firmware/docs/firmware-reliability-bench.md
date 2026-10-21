# Exact-build firmware reliability bench

Status: prepared checklist. No physical flashing, service provisioning, board
power interruption, acoustic measurement or real iPhone check has been run as
part of this redesign. Execute only after separate authorization. Simulation
and successful firmware builds do not satisfy these checks.

## Record the artifact and fixture

- Pin the reviewed commit/tree and the production binary SHA-256, build/version,
  configuration and dependency versions. Use the production artifact, never a
  diagnostic image, for OTA. Keep diagnostic binaries separately identified.
- Record board revision, actual flash/PSRAM configuration, hardware profile,
  calibration revision, OTA partition and boot/reset reason. Confirm the actual
  wiring against the pin map before power or motion tests.
- Record the iPhone/iOS version and hotspot mode, backend version and trusted
  certificate path. Use authorized device credentials without recording tokens.
- Measure the 5 V supply before connecting the ESP. Preserve direct PCA OE,
  the service power switch, calibrated servo timing and purchased hardware.
  Begin RGB brightness near 25%, use at most one vibration actuator, and never
  overlap vibration with servo movement. Record measured current/voltage.

## Passive boot, storage and OTA

- Boot with motion disabled pending inspection and no hotspot/WAN. Confirm a
  locally healthy pending OTA image without operator input, leaving inspection
  and motion readiness separate. Repeat with transient IP loss.
- Inject the authorized local adapter/storage failure cases and verify the
  documented rollback decision. Interrupt before/after confirmation-guard and
  SDK slot-state commits, including a lost acknowledgement. Record both slots
  and exact artifact digests; never interpret an ambiguous write as success.
- After explicit authorized service establishment, restart with valid bound
  inventories, pending physical intent and missing/corrupt established stores.
  Missing established authority must fail closed, with status/Stop and passive
  image health still available. Normal boot must not provision or rebind.
- Interrupt real NVS writes before/after journal intent, physical close,
  keepsake/offline commits, retirement and compaction. Check old epoch/UUID
  replays and retained evidence after restart. Unknown physical position still
  requires inspection; timed return is not a measured position sensor.

## Independent operation and audio

- Complete calibration/inspection, then exercise manual, autonomous online,
  offline, dance and presentation arbitration. Verify only one servo/vibration
  actuator at a time and the six-LED segment/brightness boundary. Disconnect an
  observer phone; standalone work continues. Expire a manual lease; manual work
  stops safely without lease renewal from rejected commands.
- Test the actual iPhone hotspot with 50-300 ms jitter, 500 ms stalls,
  multi-second outages and backend restart. Verify bounded failure/recovery,
  explicit engine/session restart and no old PCM or physical replay. Repeat
  token expiry, stale responses and duplicate/out-of-order sequences.
- Use GPT Live as primary and the explicit Realtime alternative. Verify 24 kHz
  mono PCM conversion, microphone/speaker gating and Live's silent input
  timeline during speaker output. No AEC/full-duplex claim is accepted on ESP.
- Fill playback/input queues, then cancel while DMA/network/storage is stalled.
  Deliver a delayed callback after a new session begins. Observe generated,
  queued, playing, drained, cancelled and failed frontiers separately. A pause
  or queued buffer never authorizes a physical proposal.
- Measure profile-B residual DMA/acoustic tail with the actual amplifier and
  speaker; software mute is not an instantaneous hardware mute. Verify camera
  capture and image consent without storing guest media in technical logs.
- With a separately authorized isolated Lab ticket, verify capability admission,
  the original session expiry and exact hashed-context Stop. A mismatched or
  stale context must not stop unrelated work. Interrupt pending technical motion
  and inspect recovery; it must remain technical and never enter the production
  keepsake inventory. Do not record the ticket or execute a paid provider test
  merely to complete this checklist.

## Measurements and result

Record workload, instruments, timestamps, high-water marks, overruns/underruns,
internal/PSRAM free/minimum/largest blocks, task stack margins, control execution
and lateness, storage latency, queue depth, playback frontier and cancellation
tail. Test nominal 10 ms control cadence, software Stop-to-OE-request within
20 ms, and cancellation processing within 50 ms of the complete application
message. These are engineering targets requiring measurements; report every
unmet target and cause instead of asserting hard real-time behavior.

Store the exact-build result with pass/fail/unrun per case. Redact credentials
and guest content. Hardware acceptance remains unverified until that record is
completed; a new firmware artifact requires a new exact-build comparison.
