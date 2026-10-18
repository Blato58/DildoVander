# Local task guidance in worktrees

Tracked `AGENTS.md` and `.agents/skills/dildovander-ui/` belong to this repository.
Their updates reach another checkout through normal authorized Git delivery.
Uncommitted edits in an owning checkout are not automatically in a new worktree.
The optional `.codex/personal-routing.md` is ignored machine-local routing; read
it alongside the selected branch's tracked instructions, never in their place.
Its editable durable source and personal installer are identified in that note.

## Desktop-managed local worktrees

The root `.worktreeinclude` selects only the ignored routing note. Confirm
`git ls-files -- .codex/personal-routing.md` is empty and
`git check-ignore -v -- .codex/personal-routing.md` names the intended local rule.
Keep this selection narrow: no credentials, caches, build output or unrelated
local config. An ignored `AGENTS.override.md` is copied automatically by Desktop
when present; this repository does not need to add one for this note.

[Official worktree documentation](https://learn.chatgpt.com/docs/environments/git-worktrees#copy-ignored-local-files-into-managed-worktrees)
limits this copying to Desktop-managed local worktrees. It skips source symlinks
and existing destinations. Native Git and remote worktrees need explicit setup.
A matching include and ignore rule are configuration evidence; verify actual
Desktop copying separately before claiming it ran.

## Explicit preparation of one native Git checkout

First verify the owning checkout and the explicitly selected target with
`git -C <checkout> rev-parse --show-toplevel` and
`git -C <checkout> rev-parse --path-format=absolute --git-common-dir`.
Both must belong to the intended DildoVander repository and share the common
Git directory. Check `git ls-files -- .codex/personal-routing.md` and
`git check-ignore -v -- .codex/personal-routing.md` in both. Do not copy if either
path is tracked, the ignore rule is missing, either path is a symlink/reparse
point, or ownership differs. Investigate the specific setup gap; do not broaden
ignore patterns or run an all-project installer to make it pass.

After those checks, copy only the note from the long-lived owning checkout into
the selected target. This deliberately refuses to overwrite target-only content:

```powershell
$taskOwner = (Resolve-Path '<owning DildoVander checkout>').Path
$taskCheckout = (Resolve-Path '<explicitly selected native Git worktree>').Path
$taskSource = Join-Path $taskOwner '.codex/personal-routing.md'
$taskTarget = Join-Path $taskCheckout '.codex/personal-routing.md'
if (Test-Path -LiteralPath $taskTarget) { throw 'Preserve and merge existing task routing first.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
Copy-Item -LiteralPath $taskSource -Destination $taskTarget
if ((Get-FileHash -LiteralPath $taskSource).Hash -ne (Get-FileHash -LiteralPath $taskTarget).Hash) {
    throw 'Routing copy verification failed.'
}
```

Recheck ignored/untracked status and read the resulting note in the target.
The personal installer can also prepare this one file using an explicit
`DILDOVANDER_WORKSPACE_ROOT` and `--managed-path personal/dildovander-routing.md`;
preview with `--dry-run`, preserve conflicts, and account for its machine receipt
writes before use. The narrow copy above needs no installer or receipt changes.

Before deleting a disposable worktree, inspect ignored routing as well as Git
status. Merge any intended lasting delta into its editable durable source and
owning checkout after backing up existing content outside the disposable tree.
Do not blanket-sync or overwrite branch-specific instructions. Windows local
tooling, Cloud containers and the VPS retain their own paths, identities,
credentials and acceptance; copying this note provisions none of those runtimes.

## Scheduled Codex maintenance

The weekly DildoVander Codex Maintenance automation uses native worktree execution
with no local environment setup script selected. Verify the supplied checkout is
a linked worktree distinct from the owning main checkout before checks or repair.
Use that worktree for every project command and write. If creation or identity
verification fails, report blocked; never fall back to the main checkout. Keep
proposed repairs in the worktree and report its path/branch. Do not automatically
synchronize, commit, push or merge maintenance changes. Preserve intended ignored
guidance outside a disposable worktree before any authorized cleanup.
