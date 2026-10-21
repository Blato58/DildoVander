# Firmware Lab voice carrier

This carrier runs a frozen, account-authorized Lab session on a capable paired
device. It is not a guest grant, provider prompt, new motion permission or a
production fallback. Normal calibrated safety, inspection, current snapshot,
durable intent and timed-return gates still apply.

## Admission and private context

`voiceTestContext` is advertised only by the implemented ESP backend adapter with
shared durable storage. Unsupported native/fake adapters do not advertise it and
their default port rejects a nonempty ticket. The local `voice.start` request has
its existing current device/boot/revision envelope and optional `testContext`:
exactly 43 ASCII base64url characters. Image consent is refused for this path.
The local owner requires standalone current readiness and no manual lease.

The account-authorized backend issues a ticket for the exact device, boot and
revision. Initial admission expires after 90 seconds. Firmware sends it only in
the authenticated device conversation-create body, with audio consent, no image
consent, the original generation/operation epoch and one original create key.
An authentication retry preserves that key and byte-identical body. Invalid,
expired, revoked or differently bound tickets fail closed; no retry drops the
ticket to create a production conversation. The backend enforces frozen mode,
voice and isolated provenance/memory. The device also retains the original
180-second local session deadline; reconnect cannot refresh it. Proposal TTL is
bounded by both the backend expiry and the remaining original local lease.

The raw ticket stays in RAM and is cleared from borrowed request/inbox storage.
It never enters a journal, keepsake inventory, telemetry or technical log. Local
voice status exposes only `testContextId`: lowercase hexadecimal SHA256 of the
ticket's UTF-8 bytes. A scoped `voice.stop` must match that ID on the currently
admitted local connection and a still-active original voice/physical intent.
Completed/failed stale status alone is not authority. Mismatch refuses without
global Stop, cancellation or lease renewal. A successful match burns its scope
before cancelling voice and applying the existing physical Stop transition.
The ordinary physical/global Stop remains a separate safety operation.

## Durable technical origin

Only the trusted carrier marks `TurnProposal::technicalTest`; provider JSON
cannot set it. The immutable one-shot proposal claim carries this discriminator
to the same asynchronous reserve/Begin/close/timed-proof owner used in production.
Technical origin is persisted in the reservation **before** motion, survives
pending recovery and remains with the exact original epoch/UUID/slot/fingerprint.
A reservation is still an untimed tombstone with volatile one-use admission;
reopen or a lost acknowledgement never restores action authority.

The fixed-size CRC journal reads versions 1-4 unchanged. Production-only history
continues to encode version 4 byte-for-byte. History containing a technical row
encodes version 5 with an explicit origin flag; identity, lengths and CRC remain
validated. Older firmware refuses version 5 rather than reclassifying a Lab row.
Do not downgrade such a device by erasing, recreating or restamping its stores.
Compaction retains referenced technical rows and rejects old-epoch replays using
the same rules as every other operation. A production inventory reference to a
technical row fails reconciliation, and both online/offline new-reference
operations refuse it without writing either production inventory.

Lab candidates never call production keepsake Prepare/NVS/outbox. Their durable
physical origin and outcome are journal facts. After a successful timed proof,
one bounded volatile delivery slot can send the original technical receipt to
the backend's durable Lab correlation. Its strict ACK echoes the original
outcome, physical UUID, generation, epoch and fingerprint and has **no locator or
archive grant**. Production ACK matching intentionally refuses it.

The volatile report is best effort for at most 180 seconds. Reset, unavailable
proof or an expired obligation can leave backend technical outcome unknown;
they cannot invent completion, reconstruct a production keepsake or replay
motion. A lost HTTP ACK retries the same immutable receipt, never the action.
New voice admission is refused while that bounded delivery slot is occupied.

## Software evidence and physical boundary

The registered Lab integration test uses actual shared storage, pending claims,
Wand/physical orchestration and simulated I/O through reserve, motion, timed
return, Stop, lost ACK, restart, inspection, compaction and old replay. It checks
that matching technical reservations cannot create production evidence and that
both inventory write counts remain unchanged. Codec tests separate technical
and production ACKs at uint64 boundaries; local owner tests reject stale
revision, image consent and mismatched/repeated scoped Stop without closing the
authenticated connection or invoking global voice Stop.

No real ticket/paid provider, board provisioning, physical presentation or phone
test is implied. See the [exact-build bench checklist](firmware-reliability-bench.md).
