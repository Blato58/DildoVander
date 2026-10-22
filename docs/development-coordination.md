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
verification. Architects define goals, contracts and acceptance in the private
planning system. For large work, one to three independent Engineer chats own
separate features, worktrees and delivery stacks. Engineers are peers; an
integration responsibility does not introduce a permanent supervising chat.
Each Engineer gives direct Coders concrete outcomes, exclusive paths, contracts
and relevant checks. Coders decide implementation details within their scope
and report to their own Engineer. Assignment is advisory, not an atomic lock.
Stop conflicting edits and resolve ownership with the integration owner.
Silence or age never authorizes taking over another task.

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
Independent features use independent PRs or stacks. Substantial dependent work
can use a small native GitHub stack: the bottom PR targets trunk and each higher
PR targets its predecessor. A branch chain alone does not register a native
stack. Each layer includes the tests for its behavior; layers need not match
Coder assignments. Internal handoffs need no separate PR/review/CI cycle.

Risk is independent of size. Authentication, authorization, secrets, persistent
data, migrations, deployment, shared protocols and physical behavior can require
contract or specialist scrutiny even for a short diff. Review the completed
feature stack once with a fresh independent reviewer and relevant acceptance,
cumulative diff and checks. Add reviewers only for distinct justified risk
scopes. After changes review the delta and affected interactions. Record
the reviewed commit/tree privately. Checkpoint commits need no repeated full
review. Same-account technical review is not a separate author's GitHub approval.
Use installed project policy for available models and per-session agent limits;
never delegate recursively.

Communicate with an affected peer only when it must act: blockers, contract
changes, ownership collisions or integration handoffs. Distinguish an agreed
contract, implementation available in a PR, and a dependency merged at an exact
commit. Use registered real top-level chats and the private targeted helper;
native subagent messaging does not imply cross-chat delivery. Queue acceptance
and actual consumption are separate evidence. Routine progress belongs in issue
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
immediately before finalization guarded by the expected head SHA. For native
stacks use the supported asynchronous squash merge of the selected complete
stack or deliberate prefix. Reconcile native membership, order, trunk, all
selected heads/bases and current review/CI coverage first. A top pass does not
establish every lower prefix is independently safe. The API expected-head guard
applies to the selected tip; do not claim a stronger local whole-stack atomic
comparison guarantee. Findings
return to the parent for repair; changed heads invalidate earlier review.

This bounded finalization is not queued GitHub native auto-merge, which remains
disabled without trusted server-side review/head enforcement. Do not fabricate
approvals or bypass rules. Unprotected repositories without proven required CI
retain deliberate maintainer/operator finalization. Only the parent's owned
candidate is eligible; this is not permission to merge unrelated work.

## Asynchronous PR follow-up

Continue available authorized work while CI runs. After a prepared handoff,
do not keep a local coding agent polling checks. Use the installed publisher
and exact originating-chat binding. Prefer the accepted private webhook relay
when its installed repository coverage and owning-user queue path are verified.
Stack notifications report actionable failure or selected delivery readiness;
routine successful layers remain quiet, with per-run evidence retained.
If that path is unavailable, for an actual long queued or running CI wait with
no independent work, arm one native follow-up
in the originating parent chat, normally every five minutes. Bind it to the
issue, PR, candidate and review handoff; reuse that chat's delivery follow-up.
Stay quiet while pending. On failure, cancellation or an unexpected selected-job
skip/missing result, pause first and resume repair in that parent. On success,
pause first, verify/review the current candidate and finalize as above. Rearm
only after a new candidate actually awaits CI. Keep idle follow-ups paused.

Never arm the relay and native fallback for the same candidate. The computer
and app must be available. If the selected integration is unavailable,
record the exact gap rather than promising automatic return; do not add a
daemon, dispatcher, service or broader permissions as a workaround. Accepted,
pending, enqueued, failed and actually merged are distinct outcomes; record
completion only after authoritative confirmation of the selected group.

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
