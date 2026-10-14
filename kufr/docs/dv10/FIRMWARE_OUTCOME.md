# DV10 completed-outcome firmware handoff

`EspIdfBackendNetwork` remembers the exact validated backend operation and
proposal for text and voice selection. `FinishPresentation` queues a keepsake
only after the autonomous `WandPresentation` returns Idle. Fault/Stop creates no
completed outcome. Evidence is `completed_by_timing`, meaning the existing
calibrated physical sequence and persistent journal completed; no position sensor
or delivery into a guest's hand is claimed. `physicalOperationId` is the actual
physical journal/trace operation ID, which is the proposal UUID.

`EspIdfKeepsakeNotifier` runs a separate low-priority FreeRTOS task with its own
device token session. HTTP and outbox persistence use network/notifier tasks,
without holding the control task or Live semaphore.
Before the validated proposal becomes available to the presentation owner, the
network task persists its exact backend operation/outcome UUID, physical proposal
UUID, original boot/generation/revision and slot as a prepared candidate.
Preparation and pending delivery share one atomic bounded NVS blob and a
persistence mutex used only by those background tasks. If preparation fails or
capacity is exhausted, DV10 is unavailable and emits a fixed technical warning;
the original physical selection remains allowed.

The existing physical journal is now version 3, retaining the same record size.
Its final `timedCompleted` marker captures the exact command ID/slot/fingerprint
only after `DeviceControlRuntime` verifies both completion and its existing
post-persist timing check. The new marker bookkeeping runs with outputs already
resting; it does not change the calibrated movements or their timing gates.
`ResolveRestored`, ready masks and legacy admission tombstones are never sufficient
proof. Version 1/2 records read as unknown for DV10; older firmware that only reads
version 2 rejects a version 3 record, so a compatible firmware rollback must retain
the new reader rather than erase or downgrade persistent evidence.

At boot the notifier reads a separate persisted journal snapshot before the
control loop starts. A prepared candidate with the exact confirmed physical
marker reconstructs the same original outcome. Other candidates are quarantined
as `physical_outcome_unknown`. Reconstructed facts always have visibility epoch
zero and cannot populate a prior guest's QR on a new boot.

The control-owner handoff returns `VolatilePendingPersistence`, explicitly an
in-memory mailbox acknowledgement rather than durable acceptance. Task
notification wakes persistence immediately even during HTTP backoff. The
background task persists a bounded eight-entry NVS outbox before sending any request. The same
outcome UUID, backend operation UUID and body are used for every network/reboot
retry. Failed persistence prevents send. Already-acknowledged facts may retry
after a crash between server acknowledgement and NVS removal; backend outcome
idempotency must return the same stored keepsake. Claim issuance cannot perform
another physical action or inference.

NVS namespace `dv10_outbox`, blob `completed`, contains only technical completion
correlation, never device/OpenAI credentials, claim URL, photographs or content.
Unchanged queue bytes do not cause repeated flash writes. A corrupt or oversized
queue disables intake; queue-full returns `Rejected`, preserves pending facts
and emits a fixed technical warning. Each transiently failed entry backs off
independently (up to 30 seconds), so it cannot block a newer eligible outcome.
Blank HTTP 404 pauses HTTP globally for 30 seconds as feature/route unavailable;
it never proves deletion and all eight admitted facts remain retained. New
completions still wake persistence while HTTP is paused; further admission at
capacity is explicitly rejected without erasing older facts.

Verified fixed error codes for permanent 410 deletion, 409 eligibility/conflict
or operator restoration, and 404 missing/wrong-owner keepsake produce a technical
quarantine disposition. The disposition is committed in the same NVS blob before
the pending send is removed. On reboot, an already quarantined pending send is
fenced and never retries generation; later queued guests can advance. Technical
quarantine is bounded at 32 records, with no TTL/LRU: capacity exhaustion requires
service intervention and rejects intake, rather than dropping recorded facts.
Unknown or malformed error responses remain pending with backoff. This device
outbox never deletes keepsake content or calls an image provider.

QR links exist only in RAM, are validated against the configured
HTTPS backend host and `/keepsake/#c=` shape, and are never logged or persisted.

The optional `state.snapshot.keepsake` LAN field is:

```text
null | {
  outcomeId: UUID,
  sessionEpoch: nonnegative integer,
  physicalOperationId: UUID,
  url: HTTPS claim link with secret in fragment,
  expiresAtEpochMs: nonnegative integer
}
```

It is sent only through the existing authenticated LAN/WSS transport. The
firmware's `LocalVoicePort::KeepsakeStatus(generation)` default returns empty so
existing adapters and test fakes remain compatible. `LocalControlOutput` publishes
null unless the notifier has a matching current-generation, unexpired claim.
Explicit accepted start/cancel/Stop hides QR and fences any late network result.
Replayed outbox facts from an earlier boot cannot populate the current QR panel.
Phone UI must also hide on Stop/disconnect/new generation and label simulator
outcomes as simulations; the optional schema is not proof of iPhone behavior.

## Validation and remaining gates

The pure host test exercises exact retry identity, capacity, completion matching,
expiry, generation mismatch, immediate hiding, late-result fencing, duplicate
restore rejection, hiding earlier-boot outcomes, explicit volatile admission,
terminal disposition before queue removal, replay fencing after that crash
boundary, per-item fair backoff, and no TTL discard. The durability suite exercises
reset after confirmed physical completion before the RAM/outbox handoff, exact
original correlation recovery, wrong-slot rejection, Stop, failed post-check,
marker write failure, legacy/manual records, and a valid committed marker with
an uncertain write acknowledgement. The existing `persistent_command_journal`,
`device_control`, and `device_control_runtime` suites also passed. All five suites
were compiled with installed Visual Studio BuildTools MSVC C++14 `/W4 /WX`.
Contract
fixtures include a synthetic valid QR snapshot and HTTP-origin rejection and pass
the existing verifier (31 valid and 45 invalid messages). `pio run` passed with
ESP-IDF 6.1 / PlatformIO 7.1.3 in 27.26 seconds after durability integration:
RAM 113356/327680 bytes (34.6%), flash 1946225/6553600 bytes (29.7%).
The resulting local `firmware.bin` is 1946656 bytes, SHA-256
`e5954bfaaaa6ae28ae2eb34cecc28b0f9cdf4396f8c19c245e5dba84b368366e`.

**Evidence boundary:** reset before the final confirmed marker remains an unknown
outcome and cannot produce a guest keepsake. Reset after the confirmed marker but
before the completed-outbox handoff recovers the durably prepared exact candidate.
An NVS write error does not prove that bytes were absent: the current owner
suppresses QR on its unhealthy journal, while a subsequent boot may accept valid
committed version 3 marker bytes for the already validated historical timed fact.
It never restarts motion, creates a different outcome, or infers measured position.
No firmware was flashed, no live keepsake was created, no paid AI ran, and no
physical/iPhone acceptance or network-concurrency measurement was performed.
