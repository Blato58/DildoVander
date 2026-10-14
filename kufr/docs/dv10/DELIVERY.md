# DV10 final delivery profile

This file records the final package layout and configuration commands. Use
[VPS_HANDOFF](VPS_HANDOFF.md) for actual read-only server capability evidence,
private storage, encrypted backup, isolated restore and authorization gates.
Local code packages are recorded in immutable project artifacts. No production
activation was performed here. The [static evening decision](STATIC_EVENING.md)
supersedes individual QR pickup in the original specification.

## Safe default configuration

The [separate keepsake environment](../../web/deploy/keepsake.env.example) has
`Enabled=false`, `ProviderMode=fake`, `GenerationEnabled=false`, and
`PortraitEnabled=false`. It is loaded through the existing service's reviewed
drop-in, with DB/archive/Data Protection outside code and public roots. Merely
installing code/config does not enable guest access or paid generation. Do not
substitute device/admin/provider credentials into the browser or release files.
Set `Keepsake__PublicDeviceId` explicitly to the configured suitcase whose active
evening should be public. The browser cannot choose a device. The permanent QR
contains only the approved HTTPS origin and `/keepsake/evening/`, without a token.

## Exact ingress inventory

[backend-route-contract.json](../../web/deploy/backend-route-contract.json)
contains all 60 current literal backend route/method mappings: the existing 49,
including seven authenticated keepsake administration routes, and 11 DigitalTwin
mappings. Twin routes use the isolated `127.0.0.1:5082` listener; the other
services and guest routes keep `127.0.0.1:5080`. Twin instance names follow the
source's 1-96 ASCII letter/digit, underscore, hyphen or dot rule; other parameter
identifiers retain their GUID or firmware channel/hash rules. Guest generation
and portrait cancellation are explicit POST-only paths. The public evening listing and its
exact base-asset route are GET-only. The eight proxy tests compare the
contract against current source declarations and generated examples, test admin,
Twin and guest separation, wrong methods, unknown paths, isolated upstreams,
canonical Host and streaming controls.

Regenerate the prepared configurations with the verified deployment host:

```sh
python3 kufr/web/deploy/keepsake_proxy.py --host VERIFIED_CANONICAL_HOST \
  --compatibility --output /private/staging/keepsake.conf \
  --http-map-output /private/staging/keepsake-upgrade.conf
```

The tracked examples use `keepsake.example.invalid`, which must never be
installed as a production origin. The complete profile replaces the old site
include, the legacy `dildovander-twin.conf` include and catch-all proxy, preserving
`/` and `/keepsake/` static roots and the exact legacy/API services. Unknown APIs
receive 404 rather than SPA HTML.
Keeping the old Twin `^~ /v1/twin/` prefix would bypass the exact route/method
guards. The replacement preserves `/twin`, `/healthz/twin`, sessions,
instances/snapshots/recordings, simulation commands, ingest, device-token exchange
and authenticated Twin health with their existing .NET authorization. Twin uses
ordinary HTTP; it adds no inbound WebSocket route or new listener.
The existing HTTPS shell, certificates, ACME, iOS and CloudVPSBridge locations
remain subject to the separately authorized deployment procedure. Actual
`nginx -t`, public ingress, streaming and existing-service checks remain gates.

## Public root preserved in the release

For replacing the existing public release, use the complete canonical project
builder. It preserves the project homepage, companion, workshop and simulator
alongside `/keepsake/`. After the parent commits and verifies a clean source tree,
use the verified CAD exports and already-built mobile dist from that same source
to build into a new private staging directory:

```sh
python3 kufr/web/build-site.py --web-dist kufr/mobilni-aplikace/dist \
  --destination /private/staging/dv10-web
python3 kufr/backend/deploy/keepsake_release.py \
  --backend /private/staging/backend-publish --web /private/staging/dv10-web \
  --output /private/staging/dv10-release --revision COMMITTED_SOURCE_SHA
```

The existing backend workflow supplies the already-published Linux executable;
do not package a developer build as its replacement. The release layout is:

```text
backend/backend and its published runtime files
web/index.html and site.css (project homepage)
web/companion/..., workshop/..., simulator/...
web/keepsake/index.html, app.js, keepsake.css
web/keepsake/evening/index.html, evening.js, evening.css
web/design-system/...
web/site-manifest.json
release-manifest.json
```

Stage only the **contents of web/** under
`/var/www/dildovander/releases/RELEASE_ID`; preserve that public hierarchy verbatim.
Stage the contents of `backend/` separately under the existing private backend
release directory. Never copy the enclosing combined package/backend into webroot.
Nesting the public root
under another `keepsake/` would break the portal's absolute `/keepsake/app.js`
and `/design-system/arcane-workshop.css` references. Canonical guest links remain
`/keepsake/evening/` for the public evening; `/keepsake/` remains the private
owner/recovery entry.

`build-site.py --keepsake-only` remains an independent build/preview command
requiring no CAD, Sites or Node BFF. Its root index is the public evening preview;
installing that entire output as the existing public root would replace the
project homepage and remove prior routes. Never use it for whole-root production
replacement. An alternative requires an explicitly authorized overlay of
`keepsake/` and the reviewed shared `design-system/` onto a copied, verified
existing public release, preserving its homepage and other routes. That combined
tree needs its own verified provenance inventory before packaging.

The packager verifies either full `site-manifest.json` (`schema`, `source_sha`)
or standalone `keepsake-manifest.json` against the exact committed revision and
all actual public file hashes. It rejects ambiguous manifests, mismatched hashes,
dirty standalone builds and known private paths, and keeps the backend separate.
The existing full-site manifest has no dirty-tree field, so the parent must verify
the clean source tree before the canonical build; its SHA alone cannot prove that.
The existing CI provenance packager wraps this combined output. Activate compatible
backend before dependent web, with feature flags off until authorized gates pass.
Rollback swaps compatible code/web rather than older DB/grants. Actual local
package identities and their scope are recorded in `docs/project/artifacts/`.

## Actual local verification

```text
python -m unittest discover -s kufr/web/deploy -p test_keepsake_proxy.py -v
python -m unittest discover -s kufr/backend/deploy -p test_keepsake_delivery.py -v
```

Six proxy tests and twelve delivery/recovery tests pass. Release fixtures verify
the project homepage, companion/workshop/simulator, portal and design-system
hierarchy, exact full-site revision/hash inventory, and clean standalone provenance
without creating a final artifact from the working tree. JSON/config/Python
parsing and generated-example comparisons are local checks; they do not establish
nginx runtime, independent off-VPS recovery, physical/iPhone or paid quality evidence.

The parent's actual full public build attempt is blocked by absent local
`kufr/mechanika/final/exports/verification.json` and its export manifest. The
initial standalone portal build (42 files) and Linux backend publish passed;
the revised public evening standalone build has 45 files. Neither
supplies the missing complete project site. Do not regenerate unrelated CAD to
close this delivery gate. Obtain the verified canonical exports or separately
authorize and verify the preserved-release overlay before whole-site deployment.

Hosted [CI 38613273980](https://github.com/Blato58/DildoVander/actions/runs/38613273980)
subsequently passed all 13 jobs for `9760f5ffab78c4a39ae0ddf5dc30fd87a6259247`,
including the complete canonical site/manufacturing build and unsigned native
iOS build. This verifies the builder; no production artifact was uploaded by
that PR run. Local verified CAD exports are still absent, and the local code ZIP
still contains the standalone preview. Deployment and real iPhone/physical
acceptance remain separate gates. Exact hosted check metadata is retained in
the immutable project CI artifact and receipt.
