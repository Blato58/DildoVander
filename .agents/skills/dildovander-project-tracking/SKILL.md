---
name: dildovander-project-tracking
description: Coordinate DildoVander work using GitHub Issues, PRs and the shared agent handoff thread. Use for substantive task start, continuation, verification or handoff.
---

# GitHub-first DildoVander task coordination

GitHub Issues are the authoritative **live** backlog. A GitHub Projects v2 board (once configured) is a view, never a separate data store. Shared cross-task coordination lives in [Issue #224](https://github.com/Blato58/DildoVander/issues/224). The one-time import is [Issue #225](https://github.com/Blato58/DildoVander/issues/225).

## Starting a task

1. Read root AGENTS, relevant component design and contract docs, open Issues and PRs, and recent messages in Issue #224. The user's explicit task takes precedence over suggested backlog work.
2. Reuse the relevant Issue or create one focused Issue for substantive new work. Small editorial fixes can skip an Issue. During the historical migration, consult unmatched `docs/project/work-items/*.json` **read-only**, and migrate an item to an Issue before implementing it.
3. In the task Issue comment `CLAIM | agent/session | branch/worktree | scope | UTC time | next check-in`. Re-read for competing claims before editing overlapping files. Earliest visible overlapping claim gets priority; issue comments are NOT atomic distributed locks. Resolve ambiguity by communication, not by concurrent changes.
4. Post short `STARTED`, `BLOCKED`, `HANDOFF` or `DONE` messages in Issue #224 only when they affect **other** agents. Detailed acceptance/test findings stay in the task's Issue/PR. Avoid mirrored status, duplicate logs and routine heartbeat chatter.

## Implementing and finishing

- Use one implementation branch and **one PR per task** unless there are genuinely independent code deliveries. PR links Issue and records actual tests, CI and limitations. Use `Fixes #N` only when all criteria are met; otherwise `Refs #N` and keep task open.
- Follow AGENTS review, security and merge gates. CI results and PR artifacts serve as source evidence. Source tests do not prove live hardware, physical acceptance, real iPhone or production deployment; record the missing check plainly.
- After merge, close the fulfilled Issue or update its still-open acceptance conditions in place. **Never create a second PR only to archive a merge, add test receipts, update a work-item JSON or regenerate STATUS.**
- Keep `docs/project/` legacy work items, evidence, targets and STATUS as historical records; ordinary tasks must not modify them. The old CLI is not required for new tasks or CI. Historical hardware/electrical observations are not deleted or declared accepted by this change.
- If another agent has a stale claim, inspect the issue/PR/session and coordinate a takeover rather than assuming a timeout means abandonment. Do not publish guest photos, private logs or credentials to GitHub.
