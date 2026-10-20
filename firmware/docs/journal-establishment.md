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

## Explicit USB service composition

The `diag-network-service` image now composes `JournalServiceEstablishment`
with the actual `DeviceIdentity`, journal/witness NVS adapters and both committed
inventory owners. It is an exclusive service image with no actuator or live
reference producers. Its local barrier is not the unfinished production barrier.
The software path has not been executed on a board.
Production explicitly holds scoped journals and any present or unavailable
establishment marker behind its existing maintenance admission gates until the
complete runtime integration is accepted. This also covers interrupted legacy
preparation before the scoped journal commit. A missing marker alone preserves
legacy behavior; marker presence is read into static storage without decoding it
on the production task stack. This refusal-only hold is not validation. Stop and
passive OTA health checks remain available; service establishment cannot by
itself activate scoped motion after booting the production image.

The existing USB request envelope and `ACTUATORS_OFF_USB_SERVICE` confirmation
remain required. An optional `journalEstablishment` field names exactly one
operation; absence never implies establishment:

- `factory` requires a fresh unprovisioned identity, actually missing journal,
  witness and inventories, and the existing `deviceId`/`credential` pair. It
  commits preparation, scoped journal and both bindings before credentials.
- `legacy` uses the already provisioned identity without a credential pair.
  Both actual inventory stores must be present; missing legacy evidence cannot
  be inferred empty. Unknown associations remain unchanged and block evidence
  reconciliation even if the identity witness can be finalized.
- `resume` reopens the committed target witness. Before credential commit it
  needs the original target/credential pair; after identity commit it requires
  their omission and uses the existing identity. It never rotates credentials,
  rebinds the device or recreates a missing established store. A lost first
  witness write with no committed witness requires another explicit `factory`
  or `legacy` operation instead.

`replaceIdentity` is rejected with these modes. The older credential-only path
also refuses to change any preparing/established or scoped identity. Existing
PoP replacement checks and network/OTA validation still apply. No credential is
included in the witness, service result or stack diagnostic. Request credential
buffers retain their existing explicit wiping behavior.

Finalization is followed by complete committed-source reconciliation with
compaction disabled. `journal-evidence-blocked` is a refusal, not motion
admission. The owner reports success only for a fully checked inventory snapshot;
production startup must independently reopen and reconcile before admission.

The service task has an explicit 32 KiB stack. Large inventory snapshots live
in a bounded heap-owned context reconstructed for each request. On task exit,
`KUFR_SERVICE_STACK_FREE` reports its minimum free stack bytes. This allocation
and a successful build are not measured board stack/heap acceptance; actual
worst-case service data and power-cut tests remain unrun. Service/provisioning
execution and flashing require their separate authorization.

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

Referenced entries are pinned again if an earlier owner left them unpinned.
The complete expected history, including original epoch, physical UUID,
compartment, fingerprint, completion and pin bits, fences a stale reconciliation.
Repair-only work uses one atomic replacement. Full-history compaction retains
the referenced entries with their pins and advances the epoch in one atomic
replacement, without intermediate pin-release writes. Publication must wait
for its confirmed commit. A failed acknowledgement still requires reopening.

A late producer must validate its original epoch and pin the referenced entry
before committing a new durable reference. It must not attach a new reference to
an entry that an intervening compaction discarded. Old envelopes never acquire
the current epoch. Full retained backlog and epoch exhaustion refuse new work.

## Committed online and offline inventories

The notifier uses `KeepsakeCommittedInventory` and the offline NVS adapter uses
`OfflineCommittedInventory`. Their `ReadCommitted` views distinguish an absent
blob from an explicit empty bound inventory. Ordinary boot, construction and
legacy writes never establish the binding. Explicit service methods establish
the existing device ID and generation 1; subsequent bound commits increment the
uint64 generation. Overflow and failed storage acknowledgements stop authority
until an authoritative reopen. A RAM draft or retirement mailbox cannot advance
the committed generation or remove a reference.

Online binding uses a canonical schema-4 JSON envelope with CRC32 and the
existing schema-3 payload. It allows at most 32 KiB, including the terminator,
because full physical retirement tuples can exceed the old 16 KiB limit.
Unbound schemas 1-3 remain readable and unbound writes retain schema 3. Offline
binding uses a 1,412-byte versioned, little-endian CRC envelope; unbound v1/v2 POD
layouts and v2 writes remain compatible. Neither CRC is an anti-rollback proof.
Legacy missing associations remain ambiguous after binding.

Every pending/prepared online candidate and every unacknowledged offline
reservation or completion is a dependency. New online dispositions retain the
original complete physical tuple. An exact terminal disposition stops being a
live dependency only after both candidate removals are committed. Unknown
physical outcomes and conflicting or incomplete associations remain
conservative. The bound retirement ledger cannot be dropped by ordinary
commits. Its explicit pruning operation acquires the common barrier and checks
the healthy scoped journal's device binding, absence of pending motion, a newer
epoch, absence of the exact historical entry and absence of live dependencies
before committing the smaller ledger. Runtime outbox state must then be reloaded
by its owner; pruning does not update a separate RAM mailbox.

Run the portable host checks from the repository root:

```text
python firmware/tests/run_show_tests.py
python firmware/tests/run_inventory_tests.py --cjson-dir firmware/managed_components/espressif__cjson/cJSON
```

The second command verifies the pinned cJSON source hashes before compiling.
Its local path is populated by the existing PlatformIO dependency resolution;
CI supplies the same checked source bytes in its temporary directory.

## Scoped preparation before evidence

A scoped presentation first reserves its exact epoch, UUID, compartment and
fingerprint in the existing v4 history as pinned, untimed evidence. The reserve
commit does not set physical pending state or produce a motion. Only its
acknowledged commit publishes a single volatile capability in that journal
owner. The journal owner cannot be copied or moved to duplicate that authority.

Online preparation captures the selected score fingerprint and reserves through
the presentation/control owner before the keepsake reference is persisted.
Offline admission reserves the original selected variant before its technical
outbox record is persisted, including during the opening audio. Scoped physical
begin consumes that same reserved entry with a separate durable pending commit;
it never appends another history entry. It can consume the last history slot.
The existing post-persist timing and physical safety checks still precede output.

Construction and reopening never restore the volatile capability. A reserved
entry found after restart is only a replay tombstone, not permission to resume.
Cancel and Stop clear the capability without removing its durable pin or
inventing completion proof. Failed preparation/consume acknowledgements require
the existing authoritative reopen procedure. Active reservations block other
journal mutations and reconciliation without claiming that physical motion is
pending. The trusted control owner remains the author of timed completion proof;
reservation alone cannot provide it.

## Remaining production integration and evidence

The production stores currently remain unbound. Startup recovery, every live
producer and persisted-journal reader still need one barrier with the control
owner, including original-epoch validation and pin ordering for late producers.
The scoped online/offline admission paths now reserve before their evidence
commits, but this is not a substitute for the common barrier or boot gate.
The exclusive diagnostic caller composes establishment; production startup must
still prevent motion or inspection mutations during recovery. A ready identity witness alone is not
physical admission: successful committed-source reconciliation is also required.
These are required before production activation.
The synchronous core performs at most one journal write per attempt.
Production storage scheduling and barrier ownership must still keep Stop responsive;
these methods must not be dropped into the existing control loop as blocking
work. The host tests do not establish an ESP latency or flash-wear bound.

Host tests inject commit cuts, failed writes and committed writes with lost
ACKs. The combined test runs 1,152 mixed manual/online/offline journal operations
through both actual inventory codecs, reserving before online/offline reference
commits, reopening after every operation and
retaining delayed evidence across repeated compactions. Its driver calls the
journal directly; it does not establish production control/actuator integration.
The actual NVS adapter tests check namespace isolation, bounds and mandatory
reopening after failure using simulated NVS. They do not prove physical NVS power-loss
behavior, actual supply isolation, servo position, flashing or iPhone behavior.
Those checks remain part of the hardware bench acceptance.
