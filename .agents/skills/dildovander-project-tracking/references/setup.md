# Setup and recovery

The tracker is `Blato58/DildoVander-tracking`, a private repository with Actions
disabled, no production workflows, runners, environments or deployment secrets.
`DildoVander-deploy` remains owner controlled. It could not safely be reused:
the inspected owner CLI identity has admin access and `repo`/`workflow` scopes;
the ordinary process can retrieve those credentials. A script allowlist cannot
isolate these capabilities.

## Owner provisioning (not completed by source installation)

1. Register a dedicated GitHub App with Repository **Issues: read/write** and
   automatic Metadata read. Select **only** the private tracking repository.
   Do not grant Contents, Actions, Workflows, Administration, Environments,
   Secrets, Deployments or runner access. No event subscription/webhook needed.
2. Either inject a short-lived installation token from a trusted issuer as
   `DILDOVANDER_COORDINATION_TOKEN`, or configure the one-shot issuer below.
   Never provide the owner's OAuth/PAT to the coordination client.
3. A separate OS/container identity provides host credential isolation. The owner
   may use the existing desktop profile instead; in that case owner keyring,
   connectors and SSH remain reachable to the process. The helper's restricted
   App calls do not provide OS isolation. Never describe that profile as isolated.
4. Run `python scripts/coordination.py doctor`, then `discover`. Verify issue
   create/comment succeeds in a disposable private task. Verify denied private
   deployment source, workflow write, dispatch, environments/secrets and runner
   operations using the actual restricted identity in an inert test repository
   with equivalent grants. Never send a production dispatch to test a denial.
   `doctor` performs read-only denial probes and does **not** prove write denials
   or absence of other process credentials. Empty repositories can return 404
   without proving permission denial; perform acceptance against real resources.

The existing owner bootstrap created Issues and pinned the hub; this is not a
restricted-identity verification. No dedicated App/token was available in the
initial migration. Do not substitute a broad token to pass startup checks.

### Automatic installation tokens

In the chosen execution account, generate the dedicated App's RSA private key in its
GitHub settings and place it outside every checkout with access restricted to
that account. Set the following non-secret environment values for the process
(or chosen OS user), then restart the client:

```powershell
$env:DILDOVANDER_COORDINATION_APP_ID = 'APP_ID'
$env:DILDOVANDER_COORDINATION_INSTALLATION_ID = 'INSTALLATION_ID'
$env:DILDOVANDER_COORDINATION_KEY_FILE = 'C:\secure-agent-profile\coordination.pem'
python scripts/coordination.py doctor
python scripts/coordination.py discover
```

App ID is on the App settings page; installation ID is the numeric suffix of
its installation settings URL. Never paste the PEM or a token into chat. The
client invokes `coordination-token.mjs` through a captured pipe using existing
Node.js built-ins. It checks installation account, App ID and Issues-only grants,
requests only this repository and Issues/Metadata permissions, then checks the
issued token's actual repository selection. JWTs/tokens remain in process memory;
each command obtains a fresh short-lived token. Do not invoke the issuer directly
or enable HTTP debug logging. Python 3.11+ and Node 24 are required in this mode.

For an isolated fresh Codex CLI profile, `apps._default.enabled=false` disables
apps by default; ensure no per-app enable override or broad MCP connection remains.
This setting alone does not isolate an owner OS keyring or SSH agent. Inspect the
actual available tools and credential access before accepting the environment.

## Private personal Project

Use a separate classic PAT with **only `project`**, a short expiration, and the
same user as the Project owner. Fine-grained PATs do not support personal Projects.
Never add owner `repo`/`workflow` scopes to the routine coordination credential.
The App continues to handle private Issues; the Project token handles only the board.

Configure these non-secret settings for the execution account:

```powershell
$env:DILDOVANDER_COORDINATION_PROJECT_OWNER = 'OWNER_LOGIN'
$env:DILDOVANDER_COORDINATION_PROJECT_NUMBER = 'PROJECT_NUMBER'
$env:DILDOVANDER_COORDINATION_PROJECT_TOKEN_FILE = 'C:\secure-agent-profile\project-token.dpapi'
```

On Windows the file contains a DPAPI-encrypted SecureString created by the same
OS user. Input the token through `Read-Host -AsSecureString`, serialize it with
`ConvertFrom-SecureString`, and save outside checkouts. Alternatively inject
`DILDOVANDER_COORDINATION_PROJECT_TOKEN` securely; configure only one source.
The helper decrypts in a captured pipe, checks identity and exactly `project`
scope, and rejects public boards. Renew this PAT before its expiration; App
installation-token renewal does not renew a personal Project PAT.

Dedicate this Project exclusively to tracking-repository Issues. Personal
project-only credentials redact Issue content; the adapter matches the native
Title exactly and refuses absent or duplicate active titles. Do not manually
add Issues from other repositories. Native auto-add may take time; a missing
card stops a claim before writing its Issue event. No local mapping database.

Status options: Backlog, Ready, In Progress, Blocked, In Review, Done. Use Priority
P0-P3 and Area Backend/Firmware/iOS/Web/UI/CAD/Infrastructure/AI/Hardware/Other;
text fields Agent/session, Verification, Public PR. Views: All work, In Progress,
Blocked, In Review, Hardware. Auto-add open tracker Issues excluding the
`coordination` label; closed items become Done. The hub remains open, off-board.

Configured claims set In Progress; HANDOFF/RELEASE sets Ready; DONE release sets
In Review without closing the Issue. `status ISSUE --session SESSION --status
Blocked|"In Review" --body-file FILE` requires the sole active claimant and
records evidence. Verified `complete` closes the Issue and confirms Done plus
its public PR/evidence. Field writes are read back. A partial write leaves the
Issue event intact: inspect/reconcile rather than blindly replaying a release.
Without Project configuration, Issues remain usable and no board sync is claimed.
An expired Project credential blocks board transitions but not Issue discovery.

## Supported integration and limits

Tracked root AGENTS invokes this skill automatically on substantive work. Skills
and `scripts/coordination.py` travel with the public code into future worktrees.
Desktop and CLI use the same command; inject the short-lived token when launching
the configured process. Cloud additionally needs Python, network access to
api.github.com and separately provisioned credentials; Windows keyring/SSH state
does not transfer. No Cloud runtime or independent agents were launched during
bootstrap. Source guidance is not proof of installed/working Cloud integration.

401/403: obtain a renewed correctly scoped installation token from the trusted
issuer. 404: check installation selection and resource existence. Rate limits:
bounded GET retries honor Retry-After up to ten seconds; no automatic POST retry.
After ambiguous writes re-discover using the same stable key/session. Duplicate
creation keys and conflicting claims require explicit reconciliation. The API
has no atomic issue creation or distributed claim lease. No public fallback.

## Archive and migration safety

The owner-only historical ZIP and per-entry SHA-256 manifest are prepared outside
the public checkout. Publish them under `archive/legacy-tracker/` in this private
repository using a reviewed owner commit, then download and verify every entry.
Regular Issues-only agents must not be able to read that Contents archive.
Only after remote verification and task reconciliation delete obsolete public
internal Issues with the owner account: `gh issue delete NUMBER --repo
Blato58/DildoVander --yes` for the candidates recorded in the private migration.
If authority is unavailable, use each Issue's sidebar **Delete issue** as owner.
Public implementation PRs stay intact. Deletion cannot erase prior public Git
history, notifications or caches. Do not claim deletion before readback.
