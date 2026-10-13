# DV08 migration and rollback

This is a one-time migration report, not an ongoing journal. The authoritative
current state is [STATUS](STATUS.md), [work items](work-items/) and typed receipts.
Rules and commands are in [ACCEPTANCE](ACCEPTANCE.md); the project skill lives in
[.agents/skills](../../.agents/skills/dildovander-project-tracking/SKILL.md).

## Inventory and provenance

[migration.json](migration.json) records original SHA-256, source section/line
ranges, destinations for current claims and every migrated blocker, unresolved
statements, inbound links and rollback. The five original files were hashed
before replacement. Archives preserve the original bytes, including line endings;
`.gitattributes` disables conversion in `docs/project/archive/dv08/`.
The source specification is DV08 version 2; its hash is recorded without making
future worktrees depend on the external specification's absolute path.

The migration preserves all 47 rows of the historical D158/D159 scenario matrix.
It also covers D166 WSS/Keychain/network checks, E0/E5/E6, E9/E12/E13,
D167 Dance, F2 physical montage, stable signing trust, native delivery and the
optional unverified PB720 contract. Scope follows real outstanding results;
historical commits/PRs were not turned into individual work items.

## Reconciliation and unresolved claims

- D166 owner acceptance of automatic connection supersedes older D165 reports
  of connection failure. Hostname timeout's cause remains unresolved. The
  long-running, persistence and negative network scenarios remain open.
- D159 service provisioning, home Wi-Fi/backend, development OTA/VALID boot and
  retained NVS keys remain recognized historical acceptances. Migrating them
  does not erase those observations. Their prose lacks sufficient subject and
  procedure data for a new receipt supporting the entire current target.
- D167 is verified source software, not a new flashed/published/native artifact.
  The reported physical baseline remains D166 0.1.168 / iOS 1000.0.23. Digests,
  current wiring/configuration/mechanics revisions and operator identity need
  evidence before exact physical acceptance; omitted values are not fabricated.
- F2's 12 October dimension revision supersedes older geometry checks. CAD,
  mesh and viewer passes do not accept epoxy/wood, fingers/connectors, wiring/FPC,
  loaded servo motion or transport. The design is not released for final print.
- The 12 October phrase “ESP32 i Arduino Nano build” does not establish a kufr
  Arduino runtime. `platformio.ini` defines only ESP-IDF; the Nano is the separate
  obludarium project. The archived claim's precise test provenance is unresolved.
- Legacy counts (32/56 mobile tests) are dated observations; D167 reports 60.
  Counts are not transferred into receipts for the new checkout.
- Legacy plan references to monetary caps are superseded by the owner's
  9 October decision already in AGENTS and the plan. No local budget gate is added.
  Paid inference is still outside DV08 authorization.
- Public-source sanitization and private deployment separation are recorded
  history. Read-only old PR refs are retained by the owner's decision; no new
  support request or history rewrite is implied. Native upload/signing after the
  publication changes remains unverified.

Duplicate current assertions formerly appeared in the implementation state,
event matrix and completion audit. Their state tables now point at the shared
JSON. The architectural plan keeps contracts and stage dependencies; operational
event instructions remain a runbook, with the obsolete matrix archived.
Existing inbound links are inventoried. Shims retain the headings used by those
links and route readers to the current state or dated archive.

## Switching safely

Proposed JSON was created and validated while all original source documents
remained in place. Repeating validation/dry-run reads does not add items or receipts.
Before switch, copy the privately staged original bytes to the manifest's archive
paths and verify every SHA-256. Run `migration-check`, check inbound links and
render determinism. Then update bootstrap, short shims and duplicate update
instructions in the same changeset. A failure before this switch leaves the old
AGENTS and documents usable.

## Rollback

1. Stop tracking edits and inspect `git status`; preserve concurrent work.
2. Run `python scripts/project_status.py migration-check` and compare the archive
   digests with `migration.json`. If any hash differs, obtain the unmodified archive
   from the migration commit before restoring anything.
3. Copy each of the five archived files to its original manifest path **as bytes**.
   Restore only the DV08 bootstrap changes in README, CONTRIBUTING and CI from
   the migration's parent commit; keep unrelated later changes.
4. Prefer reverting the DV08 migration commit/PR as a reviewed Git changeset.
   Do not reset the branch or remove newer receipts automatically. The archive
   is deliberately self-contained, and the original AGENTS still loads the old
   state document if the switch is interrupted or reversed.

Measurements and actual workflow checks are in the DV08 receipts. A validator
confirms internal consistency and local availability, not truth of observations.
