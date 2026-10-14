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
as `physical_outcome_unknown`. Reconstruction retains the original technical
identity and never creates a new physical action or backend operation.

Prepared proposals also have an exact-identity retirement mailbox. Text/Live
store rejection, stale or expired snapshots, failed admission, pending Stop or
cancel, conversation closure and terminal presentation Fault signal the notifier
task. Local expiry runs before WAN/time/token checks. The control loop only
queues technical identity; it performs no outbox flash or network writes.
Active presentation cancellation waits for physical terminal bookkeeping rather
than prematurely rejecting a candidate that can still gain a valid timed marker.
The worker reads committed physical journal bytes and gives a validated matching
completion precedence over late cancellation, preserving the same outcome UUID.

For an uncompleted proposal, the worker first commits a technical disposition
while the prepared slot remains occupied, then commits prepared/pending removal.
Only after that second commit may it compact the temporary preparation disposition.
These are technical proposals without retained guest content; compaction does not
delete accepted/completed guest facts and uses no TTL/LRU. Backend terminal
dispositions and boot `physical_outcome_unknown` remain retained. Failed commits
keep the retirement pending and cannot release its slot. Boot repeats durable
removal before compaction, so reset at either boundary cannot resurrect issuance.

The control-owner handoff returns `VolatilePendingPersistence`, explicitly an
in-memory mailbox acknowledgement rather than durable acceptance. Task
notification wakes persistence immediately even during HTTP backoff. The
background task persists a bounded eight-entry NVS outbox before sending any request. The same
outcome UUID, backend operation UUID and body are used for every network/reboot
retry. Failed persistence prevents send. Already-acknowledged facts may retry
after a crash before the delivery ACK is durably fenced; backend outcome
idempotency must return the same stored keepsake. Delivery cannot perform
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

The producer performs only `POST /v1/keepsakes/outcomes/{operationId}`. A successful
201 (or compatible idempotent 200) must return the exact outcome UUID, physical
operation UUID and original generation, with a valid locator. The notifier then
commits an `outcome_delivered` ACK fence before removing the exact prepared/pending
candidate, commits that removal, and only then compacts the local ACK fence.
Failed ACK/removal persistence retains mailbox capacity and retries local
persistence without resending while that acknowledgment is in memory. Reset
before the ACK commit may resend the same idempotent outcome UUID. Reset after
that commit restores a delivery fence and cannot reissue the outcome.
The fence is technical delivery bookkeeping; accepted guest content stays owned
by the backend and is never deleted by local ACK compaction.

The backend associates the outcome with its stored evening. The device does not
choose or relabel an evening and does not request a private `/claim` capability.
Visitors use the website's shared static public evening link to view public wand
stories. There are no individual guest QR URLs, expiry fields or claim state in
firmware, `LocalVoicePort`, LAN snapshots or the control schema. Conversation and
control generation semantics remain unchanged. Private owner flows remain on the
server; neither a phone nor the public page mediates generation or actuator control.

## Validation and remaining gates

The proposal-retirement change adds host cases for eight rejected proposals,
failed disposition commit retaining all eight slots, exact-identity rejection,
96 sequential ordinary retirements without cumulative exhaustion, and a valid
completed marker winning cancellation across interrupted cleanup. Its host and
firmware builds passed after independent review. The shared production NVS codec
test uses actual serialization plus cJSON decoding for all eight retirement reasons
at both persisted interruption boundaries, and rejects unapproved reasons.

The pure host test exercises exact retry identity, capacity, exact acknowledged
candidate matching, duplicate restore rejection, explicit volatile admission,
terminal disposition before queue removal, replay fencing after that crash
boundary, per-item fair backoff, and no TTL discard. The durability suite exercises
reset after confirmed physical completion before the RAM/outbox handoff, exact
original correlation recovery, wrong-slot rejection, Stop, failed post-check,
marker write failure, legacy/manual records, and a valid committed marker with
an uncertain write acknowledgement. The existing `persistent_command_journal`,
`device_control`, and `device_control_runtime` suites also passed. All six suites
were compiled with installed Visual Studio BuildTools MSVC C++14 `/W4 /WX`.
The related `local_control_output_test` also passed with those flags; its 54
actual serialized firmware messages passed the shared v1 schema verifier.
The external managed cJSON C source was compiled separately with `/TC /W3`;
the project C++ codec test retained `/W4 /WX`. Both cJSON 1.7.19 source/header
are byte-identical to the official upstream tag. The source SHA-256 is
`298581a04a36c0165da4b0aade235c23088cb2faa58651d720ea2f3706ed0b0d`, and header
SHA-256 is `25b0145150d500498e4d209cec69c18c42cf818bffcc54690be3b895a2a16dee`.

From a Visual Studio x64 developer command environment, the new codec check uses:

```text
cl /nologo /TC /W3 /D_CRT_SECURE_NO_WARNINGS /c managed_components/espressif__cjson/cJSON/cJSON.c /Fo:<private-host-dir>/cJSON.obj
cl /nologo /EHsc /std:c++14 /W4 /WX /Imanaged_components/espressif__cjson/cJSON tests/keepsake_persistence_codec_test.cpp src/features/conversation/ConversationStartCodec.cpp <private-host-dir>/cJSON.obj /Fo:<private-host-dir>/ /Fe:<private-host-dir>/keepsake_persistence_codec_test.exe
<private-host-dir>/keepsake_persistence_codec_test.exe
node ../contracts/verify-firmware-output.mjs <private-host-dir>/local_control_output_test-run.log
node ../contracts/verify-fixtures.mjs
pio run
```

The individual QR snapshot fixtures were removed with the unreleased LAN field.
The static-evening source passed independent review and the current checks on
2026-10-14 (receipt captured 17:50 UTC). All seven host suites passed; the protocol
fixture verifier passed 30 valid and 44 invalid messages. ACK cases cover exact
candidate matching, wrong physical/generation/outcome responses, failed first and
second commits, same-UUID reset replay, unrelated candidates retained, both actual
serialized ACK interruption boundaries, and 96 successive successful ACKs without
quarantine exhaustion. No source fixes were needed during this validation.
`pio run` passed with ESP-IDF 6.1 / PlatformIO 7.1.3 in 28.70 seconds:
RAM 113244/327680 bytes (34.6%), flash 1948993/6553600 bytes (29.7%).
The current local `firmware.bin` is 1949424 bytes, SHA-256
`4963479e83a2f120ca85ca1d3094e58aa6ec11dedaf6c9e1145346dd08fa6a6e`.

Earlier receipt, 2026-10-14, retirement/codec integration before the static-evening
change: six host suites passed and `pio run` completed in 272.71 seconds:
RAM 113372/327680 bytes (34.6%), flash 1951109/6553600 bytes (29.8%).
That preceding `firmware.bin` was 1951536 bytes, SHA-256
`393f7fd5e86625aba7f9544d78891fdc1782dc84da702c8b2f6ad0e78e677ebd`.

**Evidence boundary:** reset before the final confirmed marker remains an unknown
outcome and cannot produce a guest keepsake. Reset after the confirmed marker but
before the completed-outbox handoff recovers the durably prepared exact candidate.
An NVS write error does not prove that bytes were absent: the current owner
suppresses unconfirmed delivery on its unhealthy journal, while a subsequent boot may accept valid
committed version 3 marker bytes for the already validated historical timed fact.
It never restarts motion, creates a different outcome, or infers measured position.
No firmware was flashed, no live keepsake was created, no paid AI ran, and no
physical/iPhone acceptance or network-concurrency measurement was performed.
