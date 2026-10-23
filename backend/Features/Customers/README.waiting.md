# Customer waiting journal

Waiting entries and claims retain technical account, profile, device and event
identifiers. They do not grant a physical device lease or start a conversation.
An explicit claim lasts at most 90 seconds. Its expiry removes unredeemed
eligibility while preserving the entry's waiting order until another explicit
action. Accepted conversations remain owned by the existing session ledger.

## Technical waiting limits

The journal reads and validates `Backend:CustomerWaiting` once during construction,
before opening its database or creating queue tables. These limits bound retained
technical rows. They are separate from provider spending, conversation duration
and character-content retention.

| Configuration field | Default | Allowed range | Counted scope |
| --- | ---: | ---: | --- |
| `EventsPerDevice` | 4096 | 1-65536 | All retained waiting events for one device, including closed events |
| `EntriesPerEvent` | 256 | 1-8192 | All retained entry rows in one event, including left, skipped and completed entries |
| `ReceiptsPerDevice` | 131072 | 1-1048576 | All retained accepted-command receipts for one device, across events and actors |
| `ReceiptsPerAccountDevice` | 1024 | 1-65536 | One account's retained accepted-command receipts for one device, across events |

`ReceiptsPerAccountDevice` must not exceed `ReceiptsPerDevice`. Missing fields use
their defaults. Configured values must be integers within the stated bounds;
empty, malformed, zero, negative, overflowing and out-of-range values reject
startup. Values as small as one support isolated capacity tests.

The defaults allow thousands of retained events, hundreds of entries per event,
and repeated commands and retries while keeping finite bounds. The account quota
prevents one guest's fresh request IDs from consuming the entire device receipt
budget. Operator receipts consume the device budget and are not charged to a guest.
The 90-second claim lifetime is fixed and is not configurable through this section.

For example, the following explicit configuration matches the defaults:

```json
{
  "Backend": {
    "CustomerWaiting": {
      "EventsPerDevice": 4096,
      "EntriesPerEvent": 256,
      "ReceiptsPerDevice": 131072,
      "ReceiptsPerAccountDevice": 1024
    }
  }
}
```

Changing the configuration of a running process does not change its captured
limits. A reviewed manual increase within the allowed bounds takes effect at the
next backend restart. Lowering a limit below retained usage preserves the rows
and blocks new commands that would exceed that limit.

## Capacity and durable replay

An exhausted bound rejects new commands with status 429. Journal codes distinguish `waiting_event_capacity`,
`waiting_entry_capacity`, `waiting_receipt_capacity`, and
`waiting_owner_receipt_capacity`. Failed commands do not add receipts or entries.
An event-capacity failure during restart rolls back closing the current event.

Accepted request IDs remain stored and replay their original result without
mutation, even after capacity is exhausted. A different body under an accepted
request ID conflicts. Historical join or claim replay cannot restore a removed
entry or renew an expired claim. Another account remains usable after a guest
reaches its own quota, subject to the actual event and device bounds.

There is no automatic pruning, rolling expiry or eviction of accepted keys to
make space. These settings do not delete content or reset waiting history. The
existing explicit account-deletion flow may remove that account's technical
waiting metadata only behind its durable revocation fence.

At capacity, inspect the affected technical bound and retained usage, then make a
reviewed configuration increase and restart if appropriate. Do not erase accepted
receipts or clear waiting history to admit another command. Waiting activation
remains durable: reaching a bound or closing an event does not restore legacy
customer admission. Capacity errors, queue expiry and operator queue actions do
not release an accepted session's physical lease; existing runtime retirement and
Stop behavior retain their separate responsibilities.

## Validation

The focused SQLite suites are:

```text
dotnet run --project backend-tests --no-restore -- --customer-waiting
dotnet run --project backend-tests --no-restore -- --customer-waiting-admission
```

Capacity regressions use explicit tiny limits. They exercise actual SQLite
commands, immutable replay after exhaustion, account isolation, startup rejection
before database writes, captured configuration, and manual increases after restart.
