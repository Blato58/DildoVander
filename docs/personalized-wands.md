# Personalized wands and character duels

Authenticated customer conversations generate their own fictional wand candidates.
The six compartments, calibration, masks, catalogue version used by the device,
proposal correlation, ESP safety sequence and timing evidence are unchanged.
A digital design never changes a physical prop. Operator conversations without
an authenticated character and historical records retain their legacy catalogue.

## Selection and persistence

`CustomerWandSelection` prepares both supported voice engines from the frozen
customer session profile. It starts before voice-provider connection. Verified
local personal facts are included only with the captured personal-memory consent
and the existing verified guest binding/long-term permission. Raw memory facts are supplied ephemerally, with the fact epoch rechecked before
dispatch and commit; corrections or forgetting cannot leave a reusable request copy.
New conversation
answers inform the autonomous decision service; they do not regenerate candidates.
Enrichment completing later applies to a subsequent session.

`WandGenerationStore` uses the existing backend SQLite database. The unique
account/operation key binds the profile identity/revision and eligible slot mask.
An immediate transaction claims a 90-second lease; provider work holds no database
transaction. A new selection operation gets new server-generated identities.
Reopening an existing selection reuses its immutable snapshots. A stale worker,
wrong revision, competing selection or revoked profile cannot replace them.

`OpenAiWandGenerationProvider` uses the existing Responses SDK/transport with
strict Structured Outputs, `store=false`, no automatic SDK retries, two concurrent
requests maximum, a 60-second deadline and bounded request/response/output sizes.
`Wands:Model` defaults to the existing Responses text model. The provider returns
fiction only. The server assigns identities and slots, validates every required
field and exactly three actions, and rejects duplicate/indistinguishable spoken
names, legacy titles, unknown JSON members and invalid counts. Model/prompt,
attempt and token-usage audit records distinguish unknown usage from zero.
No local monetary limit is introduced.

Unavailable/invalid/expired/cancelled generation has an explicit retryable state.
A new explicit conversation can retry after device reconciliation. There is no
legacy-name fallback for a known generated operation, including tombstoned ones.
Availability revisions and all physical/device validation remain authoritative.

The selected snapshot is pinned before proposal publication; this is a pending
selection, not proof of presentation. Only the existing authenticated physical
outcome can issue its keepsake. Issuance revalidates the generated assignment in
the same SQLite transaction. Profile display uses issued account-owned bonds,
not pending selections. Historical keepsakes retain the snapshot after ordinary
profile edits. The canonical design is imposed on partial and completed story
editions and the image prompt, preventing a second wand design.

## Private review and public fiction

OpenAI processing consent does not authorize publishing private-derived text.
New generated keepsakes stay private by default, including their public assets
and wizard identity. The original account owner reviews the wand, spells, story
and design in the keepsake and approves the exact ready edition via
`POST /app/v1/customer/keepsakes/{id}/wand-publication` with
`{approved, editionId, revision}`. The account/session, original ownership,
CSRF/origin, revision and ready edition are checked server-side. Linked viewers
cannot approve. Withdrawal hides public projections; a new edition requires
fresh approval. Existing catalogue-based public records remain compatible.

Public projections exclude profile/revision/session bindings, account details,
provider prompts, private context, portraits, tokens and unrevealed actions.
Manual keepsake deletion atomically scrubs candidate copies and related duel
snapshots/grants/commentary, retaining technical revocation fences. Account and
profile deletion fence in-flight generation. Local deletion does not claim
provider or backup erasure.

## Existing duel engine, version two

Two approved completed assignments use `duel-personalized-v2`.
Each has three named spells mapped to `strike`, `trick`, `shield`. Server-owned
stats are 3/3/3; the existing cyclic advantage is ±2 and shared bounded jitter
cannot favor a side. Identical actions tie. Fictional affinity, weakness, names
and AI narration have no scoring authority. Version-one hash fixtures, stored
commitments, rules and results remain unchanged. Mixed generated/legacy admission
is rejected rather than assigning a generated wand a legacy profile.

The operator creates the existing same-evening duel and issues separate scoped
grants. Both interactive participants must explicitly join/consent before start.
Grant rotation revokes the old participant consent. Choices lock once; both phones
can commit from the same round revision without leaking either action. Reveal
occurs after both commit or the server's 15-second deadline; missing actions use
persisted deterministic commitments. Autoplay/exhibition continue to work.

The mobile battle presents the named spells, server deadline countdown, locked
feedback, reveal, scores and final result. Device narration uses the existing cue
bridge and trusted physical slot mapping, without servo commands. Missing or
unconfirmed device playback switches personalized battles to explicit web-only
mode. Playback confirmation remains distinct from dispatch. An unsafe lost show
occupation/restart interrupts instead of replaying physical effects.

Original accounts can read their battle history through
`GET /app/v1/customer/duels` and `/duels/{id}`. Authorization uses original-owned
wizard identity IDs, never the six shared catalogue IDs. Historical pages remain
read-only; account access does not grant participant tokens or operator controls.

## Verification boundaries

Offline commands include `dotnet run --project backend-tests -- --generated-wands`,
`--character`, `--duel`, the complete backend suite, `npm test --prefix contracts`,
`npm test --prefix mobilni-aplikace`, the application build and browser scripts
under `mobilni-aplikace/tests/`. Fake providers exercise generation, canonical
keepsakes and real SQLite duel boundaries; browser fixtures are synthetic.

Live Responses/Live/Realtime individuality, actual iPhone/native transport, ESP
acknowledgements, LED/audio behavior and physical calibration require separate
provider/device verification. This implementation does not deploy, flash, invoke
paid providers, or claim a measured physical effect.
