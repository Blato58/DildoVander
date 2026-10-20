# Establishment witness and evidence reconciliation

These components prepare the remaining production journal migration. They are
compiled and tested separately from production admission. **Production scoped
admission, pin release and compaction remain disabled; the legacy 32-operation
limit remains.** Merely linking these classes does not establish an identity or
prove that the live outboxes use their barrier.

The original replay contract is in [journal epochs](journal-epochs.md). The
backend must already support [original epochs](backend-operation-epochs.md).

## Establishment storage

`JournalIdentityOwner` reads a versioned, CRC-protected witness through a
`JournalStorage` port. `EspIdfJournalIdentityStorage` stores it at
`wand_identity/journal_scope`, beside the independent provisioned device record.
The journal remains at `wand_journal/state`. No credential is copied into the
witness.

Preparation retains the complete expected canonical journal record. Recovery
compares that record rather than guessing from a revision, the current device
epoch or a short hash. Finalization replaces the preparation payload with an
established witness naming the device and its initial epoch. It does not erase
history or authorize reconstruction of a lost journal.

There are three durable phases: `PreparingFactory`, `PreparingLegacy` and
`Established`. Construction and validation never write. Service methods require
explicit authority from a trusted caller; an ordinary boot or client retry must
never supply that authority implicitly. These methods are software interfaces,
not authorization to run a service operation, provision or flash a real device.

The caller must keep admissions and reference creation blocked while preparing:

1. For a new device, require a healthy unprovisioned identity and actually absent
   marker and journal. Commit the preparation witness before initializing the
   scoped journal with ready mask zero.
2. Persist explicitly initialized, device-bound online and offline inventories.
   A missing blob is not an empty committed inventory.
3. Provision the matching identity through the existing credential path.
4. Finalize only against the exact expected journal and both complete committed
   inventories. Run reconciliation before admitting operations.

Legacy migration requires explicit service authority, a healthy provisioned
identity and a present unscoped journal with no pending operation. It preserves
the ready mask and every historical entry, including unknown or legacy evidence.
A stored ready-mask-only record is distinguishable from an absent journal;
writing that mask does not make the original engine's missing-identity fence go
away. Binding such a record requires the stronger explicit service path.

Any provisioned identity with an absent journal fails closed, even during
preparation. Missing marker with a scoped journal, mismatched identity, malformed
records and unexpected preparation contents also require service adjudication.
Do not erase records, lower epochs or synthesize completed evidence to recover.

Any failed durable write may have committed despite a lost acknowledgement.
The owner or adapter stops subsequent work and must be reconstructed from
authoritative storage. Reusing the failed instance or its cached preparation
state is not recovery.

## Reconciliation boundary

`JournalEvidenceSource` supplies a bounded committed snapshot with origin,
device binding, generation, completeness, ambiguity and exact physical tuples.
Neither an ordinary RAM outbox view nor a retirement mailbox satisfies this
interface. Overflow, missing/unavailable storage, incomplete inventories,
unknown associations or mismatched identities block reclamation.

All admission, reference creation, inventory commits and persisted-journal
readers must share `JournalReconciliationBarrier`. Reconciliation acquires it
without waiting, checks the established identity again, and validates both
inventories before changing any pin. Pending physical operations block it.

Referenced entries are pinned again if an interrupted attempt left them
unpinned. Unreferenced pins are released only when attempting a full-history
compaction under that same barrier. Each release names the original epoch,
physical UUID, compartment and fingerprint. Any failed release stops the
attempt. Epoch advancement remains one atomic journal replacement; publication
must wait for its confirmed commit.

A late producer must validate its original epoch and pin the referenced entry
before committing a new durable reference. It must not attach a new reference to
an entry that an intervening compaction discarded. Old envelopes never acquire
the current epoch. Full retained backlog and epoch exhaustion refuse new work.

## Remaining production integration and evidence

The real notifier and offline stores still need committed inventory origins,
complete durable reference/retirement representations, startup recovery and a
shared barrier with the control owner. The service/provisioning caller must
compose establishment without allowing motion or inspection mutations midway.
These are required before production activation; a fake committed source is
test evidence for the port contract, not proof of the existing live stores.
The synchronous core may perform several durable pin writes in an attempt.
Production storage scheduling and barrier ownership must keep Stop responsive;
these methods must not be dropped into the existing control loop as blocking
work. The host tests do not establish an ESP latency or flash-wear bound.

Host tests inject commit cuts, failed writes and committed writes with lost
ACKs, preserve retained identities and exercise repeated operation/restart/
compaction cycles. The NVS adapter test checks namespace isolation, bounds and
mandatory reopening after failure. They do not prove physical NVS power-loss
behavior, actual supply isolation, servo position, flashing or iPhone behavior.
Those checks remain part of the hardware bench acceptance.
