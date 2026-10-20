# Runtime storage ownership

The asynchronous journal port separates durable work from manual control. It is
compiled into the production image but is not instantiated there yet. Production
continues to use the legacy journal and the establishment maintenance hold.
Scoped activation, automatic compaction and removal of the 32-operation limit
still require the complete producer, inventory and boot composition described in
[journal establishment](journal-establishment.md).

`JournalStorageOwner` has one preallocated request and completion slot. A worker
alone accesses its mutable `PersistentCommandJournal`. Submission reserves both
slots and a logical transaction token before publishing work. An occupied result
cannot be overwritten by another request. Fixed `JournalSnapshot` publications
give the control owner bounded, read-only observations without reopening NVS.
Snapshots carry no usable reservation capability.

`JournalTransactionArbiter` prevents exclusive reconciliation while a producer,
job or unconsumed result holds a token. Its mutex protects short RAM bookkeeping;
neither a logical transaction nor the mailbox mutex is held across storage I/O.
All other producers, committed inventories and persisted-journal readers must
join this same arbiter before it can protect production compaction.

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
Autonomous/offline admission and evidence producers still use synchronous ports;
their migration, worker task/stack composition and complete boot reconciliation
gate must be integrated before production activation.

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

The `BoundedCompaction` policy is exercised by host integration fixtures. The
production entry point has not selected this runtime, and its scoped activation
and maintenance hold remain unchanged. The fixture runs 1,152 actual manual
producer/control/selection/storage cycles through 37 compactions and restarts,
retaining an untimed prepared reference and rejecting old boot/epoch replays.
The actuator port is a timed simulation. The 8,190,720 ms of simulated workload
is not the required four-hour media/session soak or hardware evidence. Separate
fixtures cover source/close ACK loss, missing stores, source-fault Stop and
reserved result draining. Autonomous/offline producer migration, the actual
worker task, stack/latency measurements and production orchestration remain open.

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

`PendingProposal::Claim` separately provides an atomic move-only handoff of the
original proposal, snapshot and absolute local expiry. Runtime invalidation
cannot rewrite that claimed intent or renew its deadline. Admission callers have
not yet migrated to the claim; it does not bypass the unavailable public snapshot
or activate asynchronous autonomous admission by itself.

The default inventory runner includes `autonomous_completion_storage_test`, using
the actual shared runtime, committed codecs and asynchronous close/proof facade.
Its explicitly test-only bridge pumps reservation and Begin during setup; it is
not full asynchronous admission acceptance. It checks cancellation and commit
cuts on both close/proof boundaries, late receipt adoption, exact source tuples
and authoritative reopen. A separate physical-owner fixture retains the first
close time across repeated polls, accepts a close at 249 ms, refuses it at 250 ms,
and keeps normal control/watchdog ticks running during delayed proof bookkeeping.
