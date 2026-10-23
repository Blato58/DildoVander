# Customer availability and waiting

The suitcase remains the conversation runtime. A waiting position or a phone
connection never starts audio, grants account ownership, or replaces the durable
device lease. Starting still requires explicit conversation consent and the
existing ticket/Bluetooth handshake.

## Availability

Availability is a read-only projection: `available`, `busy`, `recovering`,
`paused`, `offline`, or `unknown`. Occupied and unresolved durable leases take
priority over runtime readiness. A runtime observation is fresh for ten seconds
and must match the registered boot identity and challenge. Presence starts
unknown after server restart; loss of presence does not release a lease.

Authenticated device status accepts the optional `bootId` and `ready` query
parameters together. Existing clients may omit both. The suitcase reports ready
only after boot reconciliation and when its conversation/show owners are idle.
The phone does not provide runtime readiness evidence.

## Explicit waiting commands

Account routes use the existing cookie, origin, CSRF and rate-limit protections.
Bodies are strict JSON capped at 2 KiB. Mutations carry `schemaVersion: 1` and a
UUID `requestId`. Customer routes are rooted at
`/app/v1/customer/waiting/{deviceId}`:

| Route | Purpose |
| --- | --- |
| `GET` | Current event, own entry, position/count and availability |
| `POST /join` | Join with an owned profile and fresh Bluetooth proximity |
| `POST /leave` | Explicitly leave the owned entry |
| `POST /claim` | Claim the eligible turn with fresh proximity |

Operator routes are rooted at
`/app/v1/customer/admin/devices/{deviceId}/waiting`. `GET` returns the event,
eligible head and count without another customer's identity. Explicit
`POST /open`, `/state` and `/skip` require current device operating authority.
State changes use the observed event revision; closed events remain visible to
the operator so reopening can provide the expected previous identity/revision.

An event is bound to a particular evening activation revision. Switching A to B
and back to A does not revive an old event. Legacy events without that revision
cannot admit new work. Pause blocks new joins/claims; leaving remains explicit.

A claim lasts 90 seconds. Its ID becomes the existing ticket operation ID.
First redemption atomically binds that claim to the existing device lease.
The customer must still press the start button; queue progress never starts a
conversation. Accepted ticket/session retries retain their original context and
are reconciled before considering a new admission.

## Reconciliation and retention

Idempotency receipts preserve the original command fingerprint. An uncertain
mutation is retried only explicitly, with its original key and body, after a
fresh read. Read recovery, foregrounding and network recovery do not replay
mutations. Account, profile, device, activation and visibility changes fence
stale work; the server independently checks authority through final commit.

Account/profile deletion fences new admission and requests the existing session
end path. Closing an event, leaving, expiring a claim or disconnecting a phone
does not release an accepted lease. Stop/end keeps its existing priority and
requires runtime reconciliation/terminal acknowledgement.

The durable waiting journal has finite admission limits under
`Backend:CustomerWaiting:`:

| Setting | Default | Maximum |
| --- | ---: | ---: |
| `EventsPerDevice` | 4096 | 65536 |
| `EntriesPerEvent` | 256 | 8192 |
| `ReceiptsPerDevice` | 131072 | 1048576 |
| `ReceiptsPerAccountDevice` | 1024 | 65536 |

All limits must be positive; the per-account receipt limit cannot exceed the
device limit. Capacity exhaustion rejects new admission without discarding
receipts, old memories or accepted work. These controls introduce no automatic
content pruning, expiry policy or monetary budget.

## Verification boundaries

Focused runners include `--customer-availability`, `--customer-waiting`,
`--customer-waiting-admission`, `--customer-waiting-activation`,
`--customer-admission`, and `--customer-waiting-http`. The HTTP fixture uses real
account/passkey, authorization, CSRF, ticket and lease routes; device bearer
provisioning is seeded through its test store. Browser and Simulator checks do
not establish signed-device, physical Bluetooth or suitcase readiness evidence.
