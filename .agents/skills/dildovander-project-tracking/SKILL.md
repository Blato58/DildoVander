---
name: dildovander-project-tracking
description: Start, continue, verify or hand off DildoVander work using the generated STATUS, relevant work item and shared tracking CLI. Use for implementation and acceptance changes or project status questions; skip tracking ceremony for small editorial corrections.
---

# DildoVander project tracking

Work from the repository root discovered by `git rev-parse --show-toplevel`.
Read applicable AGENTS instructions, then [STATUS](../../../docs/project/STATUS.md)
and the work item for the user's task in `docs/project/work-items/`.
Keep the explicit task; a recommendation does not authorize a different initiative.
If no item matches a substantive outcome, add a small item using the model in
[ACCEPTANCE](../../../docs/project/ACCEPTANCE.md). Do not read the archive by default.

Use the same CLI on Windows (`python`) and Linux (`python3`):

```text
python scripts/project_status.py next --initiative DV08
python scripts/project_status.py blockers
python scripts/project_status.py validate
python scripts/project_status.py verify-evidence
python scripts/project_status.py render
python scripts/project_status.py render --check
```

Replace the initiative with the explicitly assigned initiative; add `--scope`
when the request limits a component. For status questions read only the relevant
item, criteria and selected receipts. Load the architectural plan sections or
component README only when they govern the change. For a blocked recommendation,
report its reasons and work on independently authorized scope. Never reactivate
deferred/cancelled work through a recommendation.

During work keep the relevant item active and its `nextAction` concrete. After
a meaningful check, add a new immutable receipt containing the actual command,
result, limitations and identity of the tested bytes. Use `git status` to assess
dirty state; capture an artifact/configuration digest when applicable. Point the
criterion's `evidenceRefs` at the receipt. Read ACCEPTANCE for required fields,
evaluation, correction, closure and exit codes; do not recreate those rules here.
Missing evidence remains missing. A failed command is recorded honestly; neither
missing infrastructure nor a URL alone proves a test result.

Before updating acceptance, inspect the diff and affected targets. Check relevant
firmware, dependencies/build settings, configuration, power, wiring, mechanics and
load, rather than only matching changed paths. Update the relevant subject identity
in `appliesTo` or the target components. Raise criterion revision if its meaning
changes. Run the CLI against that target to identify stale or contradictory
evidence. Documentation alone need not change the hardware subject. Transfer of
validity needs explicit evidence and a reason supported by the diff/fingerprint;
when that evidence is absent, leave the new target unverified or stale.

Keep implementation, verification, publication, deployment/installation and
physical acceptance separate. A release record is added only for a real published
artifact. Never create a physical receipt for software tests. Existing historical
service acceptance is retained in dated notes/archive; a new target still needs
applicable evidence. Receipts do not authorize deployment, paid AI, signing or flash.

For handoff, update only affected items: state, concrete blocker and unblock
condition, missing checks and next action. New receipts are needed only for actual
checks. Do not append a chronological journal. For closure use the acceptance
snapshot and verified subject defined in ACCEPTANCE, then validate and render.
Report whether verification is current for the target; lifecycle closure alone
does not establish that. Ordinary slices typically edit one item, optionally a
receipt, and regenerate STATUS. Small text corrections need no new work item.
