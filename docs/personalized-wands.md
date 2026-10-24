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

## Canonical illustration lifecycle

Confirmed physical issuance transactionally queues a `wand_artwork` dependency in
existing `dv10_jobs`. Text selection alone does not bond a wand or dispatch an
image. The existing worker calls the existing OpenAI Images SDK once for the
selected wand, using its complete validated identity, material, core, personality,
lore and physical design. No candidate images are generated. The artwork job
runs before the separate keepsake story and never blocks the conversation or
ESP32. Queue/storage admission remains bounded; deferred jobs are admitted by the
worker independently of read requests.

A successful result saves a decoded, hash-verified PNG as an immutable version-1
asset and stores wand ID, asset ID, synthetic flag, provider/model, prompt version
and creation time. Private provider responses and prompts use the existing
archive. The asset retains `base_image` compatibility and has no edition owner;
all personalized editions reference that exact asset. Personalized base jobs use
Responses for story only and never call Images. Legacy catalogue keepsakes keep
their existing combined story/image behavior.

Profile, session result and owner keepsake projections include an `artwork`
reference with locator, asset ID, version, synthetic label, current status and
explicit recovery availability. The reference grants no byte access: every
existing owner/public asset route rechecks authorization and publication.
Duel snapshots reference the same approved public artwork. Account-history
presentation removes a withdrawn artwork reference on refresh while retaining
the immutable combat snapshot in storage. Public withdrawal denies original and
derivative image reads. Downloaded files cannot be recalled from a recipient.

`POST /app/v1/customer/keepsakes/{id}/wand-artwork/retry` is an original-owner,
CSRF-protected action with an `Idempotency-Key`. A running or completed attempt
returns its existing job. An explicit retry after failure/uncertain dispatch
fences the prior attempt before queuing a replacement; a late response cannot
publish over it. Once canonical artwork exists, recovery cannot replace it.
Restart requeues undispatched leases and marks dispatched work for reconciliation;
it never silently repeats a paid image request. Technical error codes contain no
provider payload or private prompt.

Startup adoption handles pre-upgrade personalized records without provider calls.
It preserves the valid currently published illustration, or the earliest valid
edition if none is published, as canonical. Contradictory older editions/bytes
remain private history; their share grants are withdrawn. Old queued stories wait
for the artwork dependency without consuming its only queue slot. Text-only
legacy records remain readable. GET never creates jobs, migrates assets or calls
providers. Manual keepsake/account deletion fences in-flight jobs and removes
canonical art and derived assets through the existing cleanup path.

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

## Versioned combat and narration

New approved personalized assignments fight under `duel-personalized-v3`.
Each has three immutable named spells mapped to `strike`, `trick`, `shield`.
The model proposes only an allowlisted archetype. `wand-archetypes-v1` assigns
server-owned strength/cunning/defense with an equal nine-point budget:

| Archetype | Strength | Cunning | Defense |
| --- | --- | --- | --- |
| offensive | 4 | 2 | 3 |
| deceptive | 3 | 4 | 2 |
| defensive | 2 | 3 | 4 |
| balanced | 3 | 3 | 3 |

Unknown archetypes, provider numeric fields and unsupported profile versions are
rejected. Legacy generated records default to balanced. The duel freezes the
archetype, profile version, stats, temperament, affinity, weakness and spells.
V3 round power is `12 + 2 × action stat + 3 × cyclic advantage`, where advantage
is +2 for the counter, -2 for the countered action, and zero for equal actions.
Strike counters trick; trick counters shield; shield counters strike. Thus a
counter always beats the full stat spread; same-action clashes reward specialty
and punish weakness. No random jitter changes v3 scoring. Only missing-player
timeout actions use the persisted deterministic seed commitments.

Stored `duel-personalized-v2` retains its original 3/3/3 profiles, ±2 cyclic
advantage and shared jitter; version-one hash fixtures and all stored results
remain unchanged. Mixed generated/legacy admission remains rejected. The balance
suite exhausts all 16 archetype pairs and nine action pairs, 1%-resolution mixed
strategy minimax grids, and 11,664 three-round battles. Every pair has a fair
minimax value within 0.011 of zero; every pure action has a reliable counter.

V3 offline narration names the committed spells, validated winner and progression.
The existing optional, deduplicated post-result OpenAI request composes three
connected original theatrical flourishes and harmless mishaps from minimal public
wand personalities and immutable combat events. Its echoed identities, actions,
rounds, scores and result must match exactly; text is bounded and cannot supply
commands. Server-rendered factual narration remains alongside the generated
flavor. Existing request claims, usage audit, no automatic paid retries and
restart-to-fallback behavior are preserved. Prose never enters the scorer or
physical cue dispatch.

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

### Verified offline acceptance — 24 October 2026

- Complete backend console suite and zero-warning backend/tests build; focused
  `--generated-wands`, `--wand-artwork` and `--duel` suites.
- SDK-bound fake HTTP fixtures prove one image-only request for the selected wand
  and story-only subsequent requests, with actual decoded PNG bytes. SQLite tests
  cover explicit retry, late response fencing, dispatched restart, capacity-one
  deferred recovery, pre-upgrade queued stories and multiple legacy illustrations.
- Canonical asset IDs match profile, result, keepsake editions and both duel sides;
  original/linked/public access, account isolation, source withdrawal, derivative
  denial, account deletion and late-result cleanup have regression coverage.
- V3 checks cover 144 archetype/action cases, 16 minimax grids and 11,664 complete
  three-round battles. Frozen v1/v2 result and persisted-reopen fixtures pass.
- 150 Node and 816 Vue/component tests, production typecheck/build, shared
  contracts, design/token integrity, 13 delivery, 16 proxy and 10 native
  preparation checks pass. Firmware host harness passes all 54 checks, including
  the simulated media/show soak; no hardware was actuated.
- Browser checks use the compiled application with separately labelled,
  decoded 600×800 synthetic wand fixtures. They exercise two-device consent,
  hidden actions and simultaneous reveals; image reuse across profile/result/
  keepsake/history; failure and explicit retry; publication withdrawal; and
  privacy-safe text/1000×1400 PNG battle exports. Mobile/desktop screenshots are
  inspected. These fixtures prove rendering and contracts, not OpenAI art quality.

Live OpenAI image/narration quality and uniqueness, physical ESP32 audio/LED
behavior, and use on an actual iPhone remain unverified. Linux cannot execute the
Swift/Xcode Simulator checks or the ESP-IDF toolchain absent from this workspace;
selected remote CI performs those builds. A successful unsigned Simulator build
is not signed-device or physical acceptance. No paid inference, deployment,
release, signing or hardware flashing is part of this verification.
