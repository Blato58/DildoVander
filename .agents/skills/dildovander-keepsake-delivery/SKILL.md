---
name: dildovander-keepsake-delivery
description: Prepare or validate DV10 VPS code delivery, private archive configuration, encrypted backups and isolated recovery. Excludes persona design, paid image evaluation and ordinary firmware flashing; production mutation requires authorization.
---

# DV10 VPS delivery and recovery

For Show Pack read [the integrated runbook](../../../kufr/docs/show-pack.md).
Package all new assets via the existing keepsake-only build; regenerate exact
proxy examples with keepsake_proxy.py. Preserve both existing SQLite files,
tombstones, pending offline imports and ambiguous AI attempts during recovery.
Public routes grant neither operator nor duel-side rights. Source changes do
not authorize production flags, paid commentary/audio, release, signing or flash.

Read [VPS_HANDOFF](../../../kufr/docs/dv10/VPS_HANDOFF.md), the applicable
AGENTS, and the DV10 work item. Work from the actual repository root. Use the
existing .NET backend, same-origin guest web and private deploy orchestration;
Sites, Node BFF and new public backend listeners are outside this workflow.

For read-only preflight, inspect source deployment first, then verify the existing
SSH alias with `ssh -G vps-dildovander` and selected host/user/port/host-key fields.
Read only service/listener/path metadata and capacity. Never print environment
files, keys, DB contents, prompts or visitor photographs. Report exact UTC time,
loopback versus public checks and unknown capabilities.

For local nonpaid verification:

```text
python -m unittest discover -s kufr/backend/deploy -p test_keepsake_delivery.py -v
python -m unittest discover -s kufr/web/deploy -p test_keepsake_proxy.py -v
```

Build the canonical portal and backend through their existing workflows. Combine
already-built outputs with `keepsake_release.py`, then the existing
`scripts/ci/package.py`; inspect provenance and code-only inventory. Keep shared
SQLite, archive, credentials and any required keys outside releases/webroot.
Render the exact nginx method/route map with `keepsake_proxy.py --compatibility`
and its required `--http-map-output`. The complete source-checked profile replaces
the old catch-all/site include, preserving current authorized API services with
a final 404. Preserve the surrounding certificate/listener, iOS, ACME and external
CloudVPSBridge locations. Include the Upgrade map once in nginx http scope; current
Live ingress uses streamed POST NDJSON and must not be buffered. Review the
generated profile and validate nginx before separately authorized replacement.

Retention is `manual_only`. No TTL, LRU, deploy cleanup or old janitor may delete
content. Rollback swaps compatible code, never older DB/grants. Use separate
keepsake service configuration because the existing activation helper rewrites
its base environment.

Before any real backup, prove writer quiescence and capture latest authoritative
deletion/revocation/ambiguous-attempt evidence. Use SQLite online backup plus the
retained archive inventory, encrypted for an authorized independent custodian.
Do not provision an external destination without authorization. Synthetic tests
and binary existence do not establish off-VPS backup or successful recovery.

Recovery stays isolated, without guest ingress or provider credentials. Verify
hashes, latest evidence and uncertain attempts using `keepsake_recovery.py`.
Different/newer evidence blocks start pending offline reconciliation; the tool
never authorizes public/provider start. Report missing evidence as a gate and
continue independently authorized local work.

For authorized delivery, capture private config backups, validate nginx/systemd,
activate compatible backend before dependent web, verify existing services and
public guest isolation, and retain a compatible rollback pair. Record exact
commands/results and subject hashes with project tracking. Explicit production
authorization is separate from artifact preparation and synthetic tests.

The owner approved static pickup at `/keepsake/evening/`; read `kufr/docs/dv10/STATIC_EVENING.md` for precedence. Shared wands/stories are scoped to the configured device and captured active evening. Public browsing never grants owner/photo/admin rights. Preserve manual retention and read-only GET behavior.
