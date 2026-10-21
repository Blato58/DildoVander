# Account-owned character and conversation feature

The existing application identity feature owns passkeys, account sessions, CSRF,
staff roles and resource grants. Public enrollment creates only `Customer` after
an owner exists. Customer ownership always comes from the authenticated account,
never an account identifier supplied in a body or a character's name.

`/app/v1/customer/character` stores exact `userInput` separately from enrichment.
Writes require the current revision and profile identity. Correction creates a
new revision; explicit `newCharacter` creates a new identity. Queued/running
enrichment does not block conversation start: the captured input is sufficient.
The worker uses leased, revision-fenced jobs, a 45-second provider deadline and
at most three attempts with durable backoff. It records token usage without
keeping fictional text in the usage ledger. Only actual provider citation URLs
can support web facts. No cross-account enrichment cache is implemented.

`ApplicationIdentity:DataRoot/customer-characters.sqlite` is an additive feature
store; existing Identity migrations, users, roles and grants are unchanged.
Tables are created idempotently. The operation journal adds customer tickets,
immutable session snapshots, revocation tombstones and a shared device lease.
The archive adds a private customer ownership table outside public story data.
Keep all three existing data roots and data-protection keys in normal backups.

Profile removal and its deletion intent commit together. Replay applies journal
revocation and live-context invalidation idempotently, then marks the intent
complete. Failure leaves it pending across restart. Permanent tombstones reject
provider startup even before replay succeeds. Account deletion also fences new
profile writes. Removing a profile never deletes a keepsake archive. Ordinary
account disablement requests an end without erasing archived private data.

`CustomerCharacters:Model` defaults to the existing Responses dialog model;
`CustomerCharacters:WebSearchEnabled` defaults to true. Provider credentials use
the existing backend `OPENAI_API_KEY` configuration and never reach the phone,
BLE or firmware. A missing credential produces a failed enrichment job while
the saved profile remains usable. Deterministic tests use a fake provider.

See the [protocol, recovery and rollback contract](../../../contracts/customer-sessions.md)
and [BLE wire schema](../../../contracts/customer-ble-v1.schema.json). Admission
defaults off and requires both protocol-v1 backend and compatible firmware.
Disable admission and reconcile devices before rollback; retain additive tables.
An old backend must not accept legacy starts while a customer lease is active.

Validation entry points are `dotnet run --project backend-tests --
--customer-profiles` and `--customer-sessions`, the existing identity/memory/
keepsake suites, `npm test` in `contracts/`, the Vue typecheck/build/browser suite,
the macOS native test script and the production firmware build. The contract
contains the real iPhone/ESP32 acceptance procedure; offline tests do not prove
physical BLE, autonomous audio or actual provider behavior.
