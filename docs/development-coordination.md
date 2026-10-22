# Development workflow

Linear owns private requirements, plans, ownership, dependencies, decisions and
delivery state. GitHub owns code, PRs, checks and releases. Keep technical
contracts close to code. Public Issues remain optional contributor intake;
triage useful reports one way without mirroring private discussion.

## Start and resume

Read the selected issue, its current brief, blockers and overlapping active
scope through connected Linear tools. If optional context helpers fail, retrieve
ordinary issues, project descriptions and documents. A small task is one issue;
a modest feature can use a parent with a short checklist. A substantial outcome
uses a project, one implementation brief and bounded issues. Projects end with
their delivery outcome.

Keep the contract short: outcome, scope, prerequisites, acceptance and focused
verification. When useful, maintain one execution capsule with lead/session,
checkout/branch, shared-file writer and next action. Only the lead updates the
shared summary; workers report inside the session. Assignment is advisory, not
an atomic lock. Stop conflicting edits and resolve ownership with the integration
owner. Silence or age never authorizes taking over another task.

Read relevant current context. The former Hub, tracking Issues and archive are
historical evidence. Do not replay complete comment histories, publish claims,
update Project cards or create tracking-only commits/PRs. The retired transport
performs no network calls. A Linear outage permits already-owned isolated work;
preserve a compact handoff and do not create a public fallback or ambiguous claim.

## Size, risk and review

Small low-risk work uses one agent, targeted checks, self-review and one PR.
Normal publishable units receive one independent review. Plan substantial work
once with bounded paths, acceptance, dependencies, shared contracts and checks.
Use disjoint paths/worktrees and one integration owner for coupled changes.
Prefer independent PRs for independently deliverable slices or one integration
PR for coupled work. Internal handoffs need no separate PR/review/CI cycle.

Risk is independent of size. Authentication, authorization, secrets, persistent
data, migrations, deployment, shared protocols and physical behavior can require
contract or specialist scrutiny even for a short diff. Review the delivery
boundary once; after changes review the delta and affected interactions. Record
the reviewed commit/tree privately. Checkpoint commits need no repeated full
review. Same-account technical review is not a separate author's GitHub approval.
Use installed project policy for available models and per-session agent limits;
never delegate recursively.

Communicate when another worker must act: blockers, contract changes, ownership
collisions, decisions or completed handoffs. Routine progress belongs in issue
fields or the active session. No greetings, acknowledgments, heartbeat comments
or STARTED/DONE broadcasts are required.

## Privacy and delivery

Never place private issue keys, URLs, titles, copied requirements or internal
decisions in public source, branches, commits, PR metadata, comments, logs or
artifacts. Public PRs describe technical behavior and actual checks. Native PR
association requires disabled public linkbacks/context publication and a verified
controlled privacy pilot. Otherwise attach a generic PR URL privately and update
status explicitly. Keep GitHub Issues Sync disabled. Ready may map to existing
Todo and Review to In Review.

After implementation, targeted checks and review, publish within existing
authority. Record the immutable candidate, actual checks and next owner
privately. Distinguish awaiting checks, merge, release and physical verification.
Missing, canceled or unexpectedly skipped selected jobs fail the aggregate gate.
An amended head invalidates earlier review until its delta and interactions are
reviewed.

The owner authorizes automatic parent-side source finalization after protected
CI passes. The parent verifies an exact-head delivery review it witnessed from
an independent reviewer, or obtains the required review automatically. Never
trust only an implementer-written file, receipt or PR checkbox. Read current
reviews, unresolved threads, selected check results and effective protections
immediately before a normal merge guarded by the expected head SHA. Findings
return to the parent for repair; changed heads invalidate earlier review.

This bounded finalization is not queued GitHub native auto-merge, which remains
disabled without trusted server-side review/head enforcement. Do not fabricate
approvals or bypass rules. Unprotected repositories without proven required CI
retain deliberate maintainer/operator finalization. Only the parent's owned
candidate is eligible; this is not permission to merge unrelated work.

## Asynchronous PR follow-up

Continue available authorized work while CI runs. After a prepared handoff,
do not keep a local coding agent polling checks. For an actual long queued or
running CI wait with no independent work, automatically arm one native follow-up
in the originating parent chat, normally every five minutes. Bind it to the
issue, PR, candidate and review handoff; reuse that chat's delivery follow-up.
Stay quiet while pending. On failure, cancellation or an unexpected selected-job
skip/missing result, pause first and resume repair in that parent. On success,
pause first, verify/review the current candidate and finalize as above. Rearm
only after a new candidate actually awaits CI. Keep idle follow-ups paused.

This is native scheduled continuation, not an instantaneous CI webhook. The
computer and app must be available. If the native integration is unavailable,
record the exact gap rather than promising automatic return; do not add a
daemon, dispatcher, service or broader permissions as a workaround.

Preserve explicit stops and paused automations. Scheduled implementation follow-up
is allowed only for a long actual running/queued CI/CD wait after independent
work is exhausted. Reuse the current follow-up and pause it when action returns.
Do not schedule ordinary implementation, review-only waits or repeated checks of
another chat. Coordinate a concrete readiness handoff with its owner. Never
reactivate a disabled scheduler because this workflow supports follow-up.

Software checks, deployment, real providers, actual iPhone/hardware acceptance
and owner-waived tests remain distinct. Dispatch requests are not completed
deployments. Simulators do not prove physical acceptance. Deployment, signing,
paid calls, providers, host operations and hardware keep separate authority.
