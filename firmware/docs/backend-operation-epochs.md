# Backend operation epochs

The firmware and backend carry the original device operation epoch as an
optional canonical decimal string from 0 through 18446744073709551615.
Missing means legacy identity zero. Explicit zero has the same identity.
An envelope never acquires the current epoch when it is retried.
JSON numbers, leading zeroes, signs, whitespace and overflow are rejected.

Conversation creation freezes the epoch. Live and Realtime admission,
correlation, proposal and availability updates must match that frozen value.
Changing an epoch requires a new interaction; updating availability cannot
restamp an active call. Device image uploads use the optional Operation-Epoch
header and retain the captured epoch in their journal correlation.

## Persistent correlation and upgrade

Backend SQLite schema 8 adds nullable TEXT operation_epoch to turn_operations
inside the existing migration transaction. Existing rows remain NULL and
serialize with the old JSON shape. Terminal/restarted reads preserve the saved
value; selection proof is committed only against matching original correlation.
Retained selection JSON keeps its epoch after short-lived operation cleanup.
An already-version-8 database missing the column is corrupt and fails closed;
startup does not silently reconstruct scoped records as legacy zero.

Upgrade the backend before enabling scoped firmware admission. A backend
version supporting only schema 7 refuses the upgraded database. Restore a
supported backend build or use the documented complete backup recovery process;
do not drop the epoch column, lower user_version or delete evidence to force a
downgrade. This source change does not deploy or modify a production database.

## Outcome acknowledgements

For nonzero epochs the device sends the original operationEpoch and nonzero
physicalFingerprint with its timing outcome. The backend matches the proposal
epoch and physical operation UUID, durably binds the request hash, and echoes
both strings unchanged. A changed fingerprint conflicts with the original
confirmation. The fingerprint is device technical evidence, not a position
measurement or backend permission to actuate.

The local outbox requires the exact epoch/fingerprint acknowledgement before
retirement. Legacy requests omit the new fields and retain their response
shape. Invalid, missing or stale scoped acknowledgement leaves evidence pending.

Offline imports use a separate additive scoped table keyed by device, epoch and
canonical operation UUID. Legacy table rows and outcome identities are retained.
Scoped archive and evening-memory identities use the same stable,
domain-separated device/epoch/UUID digest, so reusing a UUID in a new epoch
cannot alias an earlier keepsake. The receipt still names the original device
operation and echoes its epoch. Retrying an import never repeats motion.

## Validation and remaining activation gates

Run dotnet run --project backend-tests/BackendTests.csproj -- --operation-epochs
for deterministic request/ACK and real SQLite migration, restart and import
checks. Run npm --prefix contracts test for wire schemas and boundary cases.
These tests use isolated temporary stores and no paid provider.

Production now composes established identity, complete committed-source
reconciliation and the asynchronous journal/evidence owner before scoped
admission. Bounded compaction replaces the lifetime 32-operation ceiling while
retaining pinned evidence; an entirely pinned window still refuses admission.
See [runtime storage ownership](runtime-storage-owner.md) for the integrated
producer/control lifecycle tests and [software acceptance](redesign-acceptance.md)
for current evidence. Hardware power interruption, real hotspot/iPhone and flash
behavior remain separate, unverified bench checks.
