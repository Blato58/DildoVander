# Local-first CAD and independent model releases

From the application repository root on Windows:

```powershell
./scripts/cad.ps1 build
./scripts/cad.ps1 publish
./scripts/cad.ps1 status
./scripts/cad.ps1 verify
./scripts/cad.ps1 rollback
./scripts/cad.ps1 build --force
```

`publish` builds then deploys a **preview** with the existing SSH alias
`vps-dildovander`. `deploy` uploads an existing bundle without generating CAD.
`verify` performs lightweight viewer schema, compatibility, inventory, size and
SHA-256 checks. It never certifies mechanical or physical acceptance.
`rollback` swaps back to the previous model in the selected channel and checks
its HTTP content; a failed check restores the original active pointer.

Prerequisites: an existing Python environment with `requirements.txt` installed,
Node (the maintained viewer validator needs no npm installation), SSH/SCP and
the configured Windows OpenSSH agent. Set `KUFR_CAD_PYTHON` or pass `-Python`
to select an existing environment. Otherwise the wrapper uses the local CAD
`.venv/Scripts/python.exe` when present, then `python`. It never installs tools.
Linux/Actions uses the same engine through `python scripts/cad.py COMMAND`.

## Generation and caching

The default writes only `exports/preview/model-release/{model-bundle.json,model.json}`.
It shares `preview.model_payload`, actual normal/transport scene geometry,
stable part names, metadata and twin manifest with the manufacturing pipeline.
It does not import `build.py`, run test suites, collision/assembly verification,
the slicer, printable STEP/STL/3MF exporters, PNG renderers or ZIP packaging.

The persistent cache defaults to `%LOCALAPPDATA%/DildoVander/cad` on Windows
and `~/.cache/DildoVander/cad` elsewhere. `KUFR_CAD_CACHE` or `--cache PATH`
selects another directory outside the CAD source tree, shared across worktrees.
Source content, external document snapshots, generator recipe and Python/package/
platform identity select a complete bundle. A hit validates the bundle then skips
all geometry construction. A miss constructs the scene and reuses per-part
tessellations by normalized BREP content, mesher recipe and runtime. Changed
metadata can reuse identical shapes; changed or damaged tessellations regenerate.
The cache is a local optimization, never verification evidence or a signature.
Do not restore it from an untrusted producer.

Output includes cache HIT/MISS, mesh hit/miss totals, bundle hash and elapsed time.
`--force` / `-Force` bypass cache reads; it does not grant certification or bypass
deployment checks. Generation is serialized per cache. Source changes during
generation abort publication. Stale locks require inspection of the owning process;
the tool never removes another builder's lock automatically.

## Deployment and production boundary

`deploy` validates with the same viewer loader, snapshots the exact bundle, and
copies it plus the small maintained Python publisher to a private temporary SSH
workspace. The server needs Python 3, not CAD, Node, npm or the application build.
The server validates exact inventory, sizes, manifest identity, compatibility,
asset hashes and verification bindings. It shares the immutable `bundles/HASH`
store and `.activation-lock` contract with the existing publisher/backend selector.
It atomically switches `active.json`, retaining `previous.json`. HTTPS readback
checks the pointer, manifest and every asset; failed readback restores the prior
pointer while still holding the activation lock. No app deployment, service restart,
archive deletion or pruning occurs. An already active hash skips the model upload
after checking current HTTP integrity; the small per-command helper still transfers.

Preview uses a separate store and URL namespace. Production requires a verified
CAD bundle with the existing report bound to model bytes and source hashes:

```powershell
./scripts/cad.ps1 deploy --channel production --bundle PATH_TO_VERIFIED_MODEL_RELEASE
./scripts/cad.ps1 status --channel production
./scripts/cad.ps1 rollback --channel production
```

Host provisioning is separate from model publication. `/etc/dildovander/cad.json`
must be root-owned, non-writable by others, schema 1, with separate `preview` and
`production` records containing `root`, canonical HTTPS `baseUrl`, `viewerSchema: 1`
and `enabled: true`. Preview root/URL are `/var/lib/dildovander/model-previews`
and `/model-previews`; production uses `/var/lib/dildovander/models` and `/models`.
An unprovisioned/legacy host fails clearly before model upload or activation.
Production provisioning requires the compatible schema-2 application migration.
The operational repository owns host configuration, private backups and migration
commands. Its installer defaults to a plan; `--apply` explicitly installs routes
with configuration backup, nginx checks and recovery. Legacy application/releases,
iOS URLs, authentication, guest data and archives remain untouched by CAD commands.

## Manual fallback and manufacturing

Dispatch **Generate CAD model (manual only)** (`kufr-cad-generate.yml`) on the
chosen source ref and download `cad-model-preview`. It calls the same Python
engine and produces exactly the same unverified ModelBundle contract. It has no
production secrets, SSH, deployment, slicer, manufacturing gates or unrelated
checks. Its hosted cache is ephemeral; persistent incremental reuse is local.
Automatic PR/push/merge CI has no dependency on this workflow.

Full manufacturing verification remains an explicitly expensive operation:

```powershell
./scripts/cad.ps1 verify --manufacturing
# Equivalent existing entry point:
./kufr/mechanika/final/Regenerate.ps1 -Python PATH_TO_CAD_PYTHON
```

This retains the original tests, actual geometry checks, slicer evidence,
manufacturing ZIP and certification path. It may regenerate manufacturing
exports separately; a lightweight preview is never promoted by relabeling it.
CAD checks do not establish physical assembly, printing or hardware acceptance.

## Focused workflow tests (no full CAD generation)

```powershell
python -m unittest discover -s kufr/mechanika/final -p test_fast_build.py
python -m unittest discover -s kufr/mechanika/final -p test_part_cache.py
python -m unittest discover -s kufr/web/deploy -p test_cad_host.py
python -m unittest discover -s kufr/web/deploy -p test_release_store.py
python -m unittest discover -s scripts/ci -p test_cad_commands.py
python -m unittest discover -s scripts/ci -p test_site_workflow.py
```

These tests use small synthetic payloads and shapes to exercise cache corruption,
source invalidation, force, schema checks, transfer tampering, idempotency,
preview/production separation and rollback after simulated HTTP failure. They do
not claim a fresh full-scene build, manufacturing certificate or production rollout.
