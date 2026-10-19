# Development coordination

Public source, technical contracts, implementation PRs and build/test CI live in
this repository. Internal requirements, priorities, blockers, task decisions and
agent handoffs live in a private GitHub Issues repository. Authorized agents use
the [coordination skill](../.agents/skills/dildovander-project-tracking/SKILL.md)
for discovery; owners do not need to supply task numbers at startup.

Use one PR for one coherent implementation. Public commit messages, PR bodies and
comments contain technical changes, tests and limitations. Keep private task
identifiers, links and acceptance discussions in the private task instead; record
the public PR and verified merge there. No second PR to record CI or update a
project dashboard. Public Issues remain available for legitimate contributors.

Historical tracking evidence is archival, never a live backlog or proof that a
current build passed hardware, signed-iPhone, provider or production acceptance.
The source contracts remain in component documentation. See the skill's
[setup guide](../.agents/skills/dildovander-project-tracking/references/setup.md)
for permissions, archive publication and provisioning gaps. A missing private
credential must not trigger public tracking or an unsupported completion claim.

## Asynchronous PR follow-up

When CI or a PR review is pending, hand off to a native Codex heartbeat attached
to the current chat, normally every five minutes, then end the active turn.
Reuse the existing follow-up for that PR; do not create a separate chat or a
second tracker. Stop local watch/poll processes without cancelling remote CI.

Include the repository/PR, current branch and checkout, private task/session,
credential routing, actual validation, remaining work and existing authorization
in the private follow-up prompt. Fetch the current head and its checks each run;
never act on a superseded run. If nothing actionable changed, return quietly.
On failure, inspect logs and relevant feedback, fix and validate, push and hand
off again. On success, re-read all reviews, unresolved threads and Codex review
state immediately before any authorized merge. Verify the merge, record evidence
in the private Issue and synchronize the Project. Never merge failing checks or
an unresolved relevant finding. Pause the follow-up when work completes or a
specific owner action blocks progress, and report the outcome or required action.

This is a periodic app follow-up, not a GitHub webhook. If the runtime cannot
schedule a follow-up, state that limitation and leave precise handoff steps;
do not promise an automatic wakeup. Private task details stay out of public
instructions, PR descriptions and comments.
