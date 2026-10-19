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
