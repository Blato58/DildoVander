# Runtime storage ownership

The production entry point instantiates one asynchronous storage worker and shares
its journal and evidence ports with manual, autonomous and offline producers.
An explicitly established device uses scoped admission and bounded compaction.
Legacy/unbound devices require [explicit service migration](journal-establishment.md);
normal boot never invents identity or replaces missing stores. Thirty-two entries
bound each retained journal window, rather than the device's lifetime operations.
An entirely pinned window still refuses new motion without evicting evidence.

`JournalStorageOwner` has one preallocated request and completion slot. A worker
alone accesses its mutable `PersistentCommandJournal`. Submission reserves both
slots and a logical transaction token before publishing work. An occupied result
cannot be overwritten by another request. Fixed `JournalSnapshot` publications
give the control owner bounded, read-only observations without reopening NVS.
Snapshots carry no usable reservation capability.

`JournalTransactionArbiter` prevents exclusive reconciliation while a producer,
job or unconsumed result holds a token. Its mutex protects short RAM bookkeeping;
neither a logical transaction nor the mailbox mutex is held across storage I/O.
The composed producers, committed inventories and persisted-journal readers use
this same arbiter. Control never obtains a mutable persisted-journal instance.

Stop advances a cancellation fence even when the device is idle or only a
storage request is pending. It does not wait for a write. An admission cancelled
before execution is skipped; a write already in progress can still commit.
Control always adopts that write's durable consequences but rejects its stale
actuation permission. Cancelling a presentation reservation burns only its
volatile capability, preserving the pinned, untimed replay tombstone. Failed or
lost commit acknowledgements poison the journal until authoritative reopening.

Manual selection and reload retain one immutable original envelope while waiting
for storage. Retries keep its original epoch, lease, sequence, revision and
deadline. They revalidate authorization and safety without consuming another
sequence number or sending a premature success. Exact transport retries coalesce;
conflicting requests cannot replace the retained command. A selection's final
command acknowledgement must succeed before its guarded motion admission.

Physical completion waits for the durable closing acknowledgement before
reporting `Completed`. Stop, expiry, disconnect and send failure remain able to
invalidate authority while that receipt is outstanding. Late closing evidence
may finish its storage obligation without changing an already reported unknown
physical outcome. Inspection recovery consumes its original pending write and
checks that no later Stop invalidated that inspection attempt.

The host checks exercise these transitions through the actual local owner,
control runtime, journal engine and storage mailbox. A condition-variable storage
port stalls a real journal write while an urgent Stop runs independently. Other
cases cover ACK failure, TTL and lease expiry, stale envelopes, missing completion
capacity, physical-close acknowledgement, and writes with lost acknowledgements.
They establish software ordering under those fixtures, not ESP scheduling bounds,
physical NVS atomicity, calibrated movement or measured Stop-to-OE latency.

Run `python firmware/tests/run_show_tests.py` from the repository root. Focused
checks are `journal_snapshot_test`, `journal_transaction_arbiter_test`,
`journal_storage_owner_test`, `async_command_journal_test`, `device_control_test`
and `local_control_owner_test`, selected with repeatable `--only NAME` arguments.
Shared autonomous/offline producers now use the asynchronous ports described
below. Their production constructors use the shared runtime after the boot handoff.
An unavailable runtime supplies a blocked journal and no backend-network startup;
there is no synchronous legacy storage fallback.

## Committed evidence and startup authority

`EvidenceStorageOwner` adds one bounded typed request/result channel for the
actual committed online/offline sources. It merges prepare, completion, delivery
acknowledgement, retirement and settings intents against the latest committed
state. Producers cannot submit arbitrary stale whole-store replacements. New
references require the original current epoch, exact existing journal pin and
live reservation or pending operation before a source commit. A job retains its
arbiter lease until its result is consumed. All result slots remain drainable
after a source fails; draining cannot start more I/O under failed authority.

Online publications allocate their bounded immutable snapshot before the source
write and swap it after acknowledgement. Readers copy outside the mailbox lock.
Offline publications are fixed-size. Terminal idempotent acknowledgements prove
only the complete persisted disposition tuple, not boot/proposal metadata absent
from that ledger, and never create another write or a new physical proof.

`DeviceStorageRuntime` concretely constructs the journal, identity witness, both
committed sources, both job channels, asynchronous control facade and one shared
arbiter. `Boot` and `RunOne` belong to a single storage worker. No construction or
boot call establishes identity or creates a missing established store. Established
binding, complete committed source inventories and successful reconciliation are
separate admission requirements. A pending record can permit inspected recovery
only after its established binding and source authority are checked. Its closing
ACK still requires complete-source reconciliation at the same cancellation fence
before recovery can resume admissions.

Startup promotes a prepared online outcome only when its exact original
historical timing proof exists. Uncertain prepared outcomes remain referenced,
pinned and undelivered. They supply no authority to resume motion. Compaction
uses the existing single journal replacement; typed retirement pruning follows
under the same barrier and reloads the evidence publication. Restart retries
pruning even when the new journal epoch was committed before a source ACK was
lost. A failed source or journal ACK requires a fresh authoritative runtime;
ordinary control polls never reopen it as healthy.

Production selects `BoundedCompaction` behind complete committed-source admission
and inspected recovery. The same policy is exercised by host integration fixtures.
The fixture runs 1,152 actual manual
producer/control/selection/storage cycles through 37 compactions and restarts,
retaining an untimed prepared reference and rejecting old boot/epoch replays.
The actuator port is a timed simulation. The 8,190,720 ms of simulated workload
is not the required four-hour media/session soak or hardware evidence. Separate
fixtures cover source/close ACK loss, missing stores, source-fault Stop and
reserved result draining. Actual board stack, dynamic heap and latency measurements
remain unrun.

## Production boot and inspected recovery

`DeviceStorageStartup` probes the actual journal and establishment marker on the
storage worker before constructing any mutable runtime. Normal boot cannot invoke
service establishment, credential mutation or identity rebinding. An established
identity still requires both committed inventories and complete reconciliation.
Actual missing or corrupt storage fails closed. A found unscoped legacy journal
retains its original bytes and requires explicit USB service; its valid passive
image-health evidence is independent of motion admission. The persisted legacy
`OriginMissing` field is not proof that the current store is absent.

`EspIdfDeviceStorageWorker` uses one 32,768-byte FreeRTOS task at priority 2. Control
receives the facade only after the startup semaphore publishes the reconciled
epoch and ready mask. Failed task/semaphore allocation never falls back to control
I/O. Work runs one bounded request at a time, followed by at least one scheduler
tick, including a 100 Hz scheduler where a two-millisecond conversion is zero.
The worker records its maximum observed operation duration and stack watermark;
these counters require execution on the board before they provide hardware
acceptance evidence. Boot-lifetime objects and placement workspaces exclude these
dynamic task and request peaks from static RAM figures.

Inspected recovery retains a noncopyable runtime-bound handle with its original
generation and revision. Local Reset performs physical inspection once, retains
the original socket and lease, and waits for both durable resolution and complete
source reconciliation before acknowledging success. Exact retries coalesce;
changed mutations receive Busy. Stop, expired lease and disconnect invalidate the
attempt without restoring authority from a late write. The physical long-press
path uses the same typed handle. Read-only state, Stop and passive OTA health stay
available during the recovery wait. Source/service and OTA gates remain separate
from this narrow recovery admission.

## Autonomous completion acknowledgements

The physical owner now retains a distinct completion wait after disabling all
outputs at rest. `DeviceControlRuntime::AdvanceAutonomousCompletion` retains the
original slot, cancellation generation and timing callback through the durable
close acknowledgement. The exact original journal tuple stays owned through
both close and timing-proof requests; published snapshots never supply renewed
authority. A pending acknowledgement is a wait, not an I/O failure or success.

The close ACK must pass the existing fresh physical clock check, including its
original 250 ms completion window and whole score deadline. Only that successful
check creates the private live completion seal. The subsequent proof ACK is
historical bookkeeping: its latency neither rechecks nor extends physical motion
timing. Wand and physical owners remain in completion wait with outputs disabled,
while health, calibration, clock order and cancellation gates still apply. They
report normal completion only after proof acknowledgement. Stop before the seal
permits no proof; Stop after it clears live success authority even if an already
started proof write becomes durable. A late close alone never supplies timing
evidence. Existing control-loop watchdogs remain active throughout the wait.

`PendingProposal::Claim` provides an atomic move-only handoff of the original
proposal, snapshot and absolute local expiry. Runtime invalidation cannot rewrite
that claimed intent or renew its deadline.

The default inventory runner includes `autonomous_completion_storage_test`, using
the actual shared runtime, committed codecs and asynchronous close/proof facade.
Its explicitly test-only bridge pumps reservation and Begin during setup; it is
not full asynchronous admission acceptance. It checks cancellation and commit
cuts on both close/proof boundaries, late receipt adoption, exact source tuples
and authoritative reopen. A separate physical-owner fixture retains the first
close time across repeated polls, accepts a close at 249 ms, refuses it at 250 ms,
and keeps normal control/watchdog ticks running during delayed proof bookkeeping.

## Claimed autonomous admission

Wand now claims once before reservation and retains the exact proposal, deadline,
fingerprint and calibration through reservation, producer preparation and Begin.
The public conversation snapshot remains unavailable during storage work. Runtime
captures this exact claim owner and original generation/revision/catalog/epoch,
ready bit, calibration and online/offline authority. Only its identified pending
Reserve/Begin may continue; another claim with identical contents cannot consume
its ACK. Early publication of an already-committed Begin cannot advance the
control revision before the original caller consumes that acknowledgement.

Reserve acknowledgement alone supplies no motion authority. Begin retains its
first attempt timestamp across retries; the physical port checks the original
claim expiry again after ACK and after its fresh clock sample before any effect.
Stop, stale guards and uncertain acknowledgements never resume the intent.
First-reservation refusal before any accepted asynchronous request remains a
plain refusal; a previously pending request's failed result requires inspection.

The backend producer uses `StartPolled`: Tick cannot consume Begin before that
producer rechecks its cancellation and preparation gates. Its held claim also
prevents the network expiry task from treating the consumed proposal mailbox as
expired. Preparation is tracked once for the exact retained candidate. This does
not activate the shared runtime in production. The legacy notifier path and
offline outbox/settings writes remain synchronous in that composition.

The no-bridge `autonomous_claimed_storage_test` exercises actual shared storage,
Wand, control and physical score owners with simulated I/O: 1,152 completed
cycles, 144 cycling boots, 37 compactions, 1,151 delivery acknowledgements and one
retained original proof. It polls real reserve, prepare, Begin, close, proof and
source outcomes at 10 ms simulated cadence. Its 8,835,840 ms workload, calibrated
elapsed travel and direct test producer are not backend/offline end-to-end,
physical position feedback or the required four-hour media/session soak.
Production composes the shared worker and producers with scoped admission and
bounded compaction. Physical runtime measurements remain unrun.

## Shared online evidence producer

The explicit shared-storage backend/notifier constructors use
`KeepsakeEvidenceBridge`, not a second journal or inventory reader. Its eight
fixed generation-tagged slots reserve space before preparing a candidate and
retain its later completion, retirement or validated delivery acknowledgement.
Control calls only try-lock and copy fixed data. The network task submits typed
jobs; the storage worker alone reads and commits the source. Preparation remains
Pending until the original result and immutable publication are adopted.

The backend keeps its original candidate and handle through preparation waits.
Busy completion/retirement transfer retains caller ownership and prevents a new
proposal from replacing it. A once-only successful handoff lets the network
owner keep that obligation through delivery and exact terminal publication.
Retirement waits for the thread-safe journal mailbox to drain, including its
result lease; network code never reads the control facade's mutable envelope.
Late proof bookkeeping can therefore win over cancellation before retirement
examines historical physical facts. This cannot restore live action authority.

The bridge adopts the source publication before releasing its result lease,
preventing quiescent pruning from erasing the evidence used to prove handoff.
Unknown results poison the bridge and retain capacity until authoritative boot
recovery constructs a new instance. Missing stores never bootstrap as empty.
If a preparation cancellation loses to an actual timed completion, its transient
reason does not become a terminal delivery reason. A later exact permanent HTTP
failure can still be committed; an existing durable terminal reason is immutable.

HTTP sends use only published pending outcomes and the existing exact ACK parser.
Response handoff retries Busy without resending the already acknowledged HTTP
request. Token refresh, permanent refusals and blank-404 cooldown remain distinct.
Transient exponential retry scheduling stays in bounded network RAM, as in the
legacy sender. It uses the exact attempt tuple, saturates at 30 seconds and resets
on reboot. Monotonic timestamps are not persisted, and a failed send cannot write
a stale whole-source draft or add an NVS retry write.

The bridge suite uses the actual committed inventory owner and simulated storage
cuts, including a receipt committed before live-row removal. Typed ACK/retirement
retries normalize that exact source without changing its terminal reason. Tests
also check eight-slot backpressure, stale handle rejection, zero allocations and
no storage calls on measured control paths, Stop/write races and proof-winning
retirement. They do not execute the ESP HTTP task or establish end-to-end
backend/hardware acceptance. Production uses the shared constructor after its
established-runtime startup handoff.

## Shared offline producer and local acknowledgements

The explicit shared `OfflineShow` constructor uses `OfflineEvidenceBridge` and
the same typed source owner. Its control-owned fixed handles retain original
outcomes and settings intents; no offline storage call runs on the control path.
Published outbox contents and settings revisions change only after the actual
durable result is adopted. Failed or lost acknowledgements poison authority
until authoritative reopen. Stop keeps a corrective safe settings intent when a
write has already started; cancelled admission cannot rearm the device.

Start captures the original generation, revision, epoch, selected variant,
fingerprint and calibration once. It waits for journal reservation and source
reservation before audio. The 18.5-second preparation deadline and later
two-second Begin deadline remain absolute across retries. Only the original
intent may continue while the public snapshot is unavailable. Motion still
requires the actual Begin acknowledgement, and completion evidence retains its
first timestamp while awaiting durable publication. Ending audio waits for that
publication. Uncertain preparation remains pinned without invented completion.

`LocalControlOwner` retains a pending offline command and its original socket
generation. Arm transfers the lease once; retries keep its post-transfer fence.
Read-only queries and exact duplicates cannot recreate that lease or consume a
premature success. Stop abandons the reply while adopting late storage facts;
disconnect cannot send that reply to a replacement socket. Server settings and
Stop revisions remain in the show mailbox until their exact durable publication
is observed, including independently newer settings and Stop revisions.

The default inventory runner includes the actual shared offline producer and
local-owner fixtures. They use real journal/source codecs and simulated physical,
audio and storage ports. The offline workload executes 1,008 cycles through 126
boots and 33 compactions, retaining old evidence and rejecting replay. Its
7,844,740 simulated milliseconds do not establish the four-hour media/session
soak, ESP task scheduling, physical power-cut behavior or hardware acceptance.
Production uses the shared offline constructor after startup. Actual scheduling,
stack and latency measurements remain unrun.

## Maintenance does not own the control pass

`ControlMaintenanceGate` reserves a completed idle publication or a control pass
using fixed RAM tickets. Production no longer holds a maintenance mutex around
the entire control loop. Network changes and BLE pairing reserve exclusive
maintenance between passes; a pass that cannot acquire admission ownership still
processes Stop, disabled outputs and status. OTA/SD work transfers its ticket
directly from the admitting pass, without publishing an intermediate idle window.
Finishing maintenance requires a fresh control pass before idle can be reused.

Pairing initialization and credential recovery also reserve the disconnected
admission/inbox state in RAM. The actual credential write runs with both mutexes
released. A new handshake receives Busy before entering credential authentication
while recovery is held; Stop and lifecycle mailbox operations can still proceed.
RAII releases the hold on every normal return and host exception. The control
owner defers credential-dependent server startup while maintenance owns admission.

Concurrent host checks stall a real credential write while querying these gates,
exercise normal-authentication-first races, and race maintenance acquisition with
control-pass admission. These establish ownership and progress at the tested
boundaries. OTA boot metadata, rollback/reboot and settings commits also run on
their existing OTA worker, with its mutex released during I/O. Control queues boot
decisions and keeps the original absolute rollback deadline while awaiting the
ACK. Before SDK confirmation, a canonical 48-byte CRC guard binds the exact
running slot and ELF digest. Loss of passive health, a failed ACK or an expired
confirmation before final authorization retains that guard and requests rollback;
none starts a new health interval. A returning rollback cannot lose the hold on
reboot merely because the SDK had already persisted VALID. An unknown/corrupt
guard on a previously valid image requires service and cannot itself authorize
rollback of that image.

After a fresh successful SDK ACK, final authorization linearizes once under the
short RAM lock before the worker erases the guard. Later health/deadline changes
cannot revoke that already authorized erase or promise rollback from RAM alone.
The separate runtime safety gates still apply. A failed/uncertain clear holds the
current boot; restart accepts only authoritative Missing from an authorized clear.
An existing or unreadable guard never automatically clears, confirms or establishes
another identity. Failed worker/mutex allocation also retains admission refusal.

OTA settings retain one immutable intent/result. An identical API retry observes
its durable ACK; a conflicting request cannot replace it. BLE acknowledges queue
acceptance and polls that original intent through status, whose policy value stays
at the last committed value until the ACK. A failed write publishes an error.
Actual adapter host tests stall metadata, settings, rollback and restart calls
while control queries continue. They do not establish the complete hardware Stop
latency target or physical flash/cache behavior.
