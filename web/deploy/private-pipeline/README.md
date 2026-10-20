# Private application pipeline migration (prepared only)

Read-only audit on 2026-10-19 inspected private repository
`Blato58/DildoVander-deploy` main
`614f55e66ed64f945cd0cab35e44f4a811efdff6`:

- `.github/workflows/kufr-site-deploy.yml`
- `.github/workflows/reuse-build.yml`
- `scripts/ci/verify-build.cjs`
- `scripts/ci/package.py`

During that audit no secret values, server configuration or production data were
read, no private files were changed and no workflow was dispatched. The current
template uses independently maintained operational helpers from the reviewed
private dispatch commit, rather than checking out application helper code at runtime.

## Verified compatibility

The reusable build gate already checks a successful direct public-main
`workflow_dispatch` run, exact repository/workflow/run provenance, immutable
artifact ID and GitHub SHA-256 digest, current main commit/tree, outer
`build-manifest.json` schema 1 and safe tar extraction. Those outer package
contracts remain unchanged. The public artifact name remains `arcane-site`.
They are compatible with the new **inner** `site-manifest.json` schema 2.

The audited legacy site workflow is incompatible after extraction. It calls
`publish-site.py` and `verify-site.py`, which require the old schema-1 multi-page
release and full manufacturing ZIP, uses an IPv4 origin, deletes legacy iOS CAD
files, and prunes old site releases. It does not publish durable model/download
stores. It must not consume a schema-2 artifact unchanged.

## Exact prepared replacement

After reviewing the diff against the audited private commit, copy
`kufr-site-deploy.yml.template` over the private repository's
`.github/workflows/kufr-site-deploy.yml`. No change is needed to the audited
reusable build, artifact downloader, outer package or provenance verifier.
Review again if private main changed since the audit; do not overwrite unrelated
private edits. Import and review `release_store.py` and
`private-pipeline/verify-application.py` under the same private `web/deploy/`
paths, recording their candidate hashes separately from older import provenance.
This is a local template, not evidence of a dispatched workflow.

The replacement retains the existing private-repository/main/dispatch guards,
`backend-production` environment, serialized production group, immutable
artifact download, safe extraction and immediate current-main recheck. It
executes operational helpers maintained alongside the private workflow at its
reviewed dispatch SHA. No application checkout executes as a production helper.
Trust gates and checkout remain pinned to reviewed action revisions.

The replacement validates clean schema-2 app provenance and its exact expected
source SHA, requires a compatible existing rollback release, and activates only
the application with `release_store.py`. HTTPS readback uses a canonical DNS
origin and checks every app file hash, deep links, private-page headers, missing
API/assets, AASA/native domain configuration and backend health. It never reads
guest content or calls providers. Redirects are rejected. Failed readback
restores and verifies the previous application. An idempotent retry does not
roll back an already-active release on a later readback failure.

The workflow does not build code, run CAD, verify a manufacturing ZIP, switch a
model, republish a download, delete legacy files or prune retained releases.
It needs Python 3 and the existing runner/artifact tooling. Node is not required
for app publication. Explicit model/archive publication remains a separate
operator task described in [the release guide](../APPLICATION-RELEASES.md).

## Provisioning and first transition

Set the private environment variable `KUFR_APPLICATION_ORIGIN` to the exact
accepted `https://<DNS-host>` (no path, credentials or nondefault port). Do not
reuse `KUFR_BACKEND_PUBLIC_IPV4` for account/passkey operation. Configure the
backend relying-party/origin and Apple App ID separately; readback deliberately
fails if the HTTPS AASA route is missing or does not contain a webcredentials
application. No new secret is introduced by this template.

Before enabling this workflow, provision the application publisher ACL and
read-only nginx access, install the reviewed canonical generated routing
snippet, preserve installer/CloudVPSBridge locations, and provision independent
`/var/lib/dildovander/models` and `/var/lib/dildovander/downloads` stores. Existing
archives must be imported byte-for-byte with `distribution_store.py` and their
old `/workshop/<revision>-kouzelny-kufr.zip` aliases verified before removing the
old route configuration. App deployment has no authority to clean these stores.

The first schema-1 to schema-2 transition is a **paired configuration cutover**,
not an ordinary template dispatch. Preserve the exact old `current` target and
private backup of the complete nginx server/snippets, stage the compiled app and
new generated snippet without activation, verify the full nginx configuration,
and review the configuration-plus-symlink restore procedure together. Existing
`release_store.py application rollback` deliberately rejects schema-1 releases;
the template therefore refuses that first transition before activation. Actual
server layout and configuration were outside this audit, so no unverified
automatic first-cutover script is supplied. The current tracked routing and
publish commands are the concrete artifacts for that separately reviewed step.

Once a schema-2 release and the matching configuration are active, the ordinary
template can validate and restore that release. The preflight can be rehearsed
locally without network or activation:

```sh
python web/deploy/private-pipeline/verify-application.py --local /absolute/staged-schema2-app --expected-sha <verified-public-40hex-sha> --preflight-root /absolute/local-schema2-store --base https://application.example.invalid
python -m unittest discover -s web/deploy -p test_private_pipeline_preparation.py
```

The helper's network readback is reserved for a separately authorized rollout.
Local tests use synthetic responses; they do not prove production nginx,
certificate trust, Apple association or passkey behavior.
