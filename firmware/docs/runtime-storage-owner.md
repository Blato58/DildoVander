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
The standalone autonomous and offline paths still use their synchronous ports;
their asynchronous state machines, worker task/stack composition and complete
boot reconciliation gate must be integrated before production activation.
