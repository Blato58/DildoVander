# Application and model releases (prepared, not deployed)

The maintained hosted entry is `mobilni-aplikace`. One Vue build serves
Home, Suitcase, Workshop, Archive and account routes from one canonical HTTPS
origin. Capacitor consumes the same source. Backend APIs, public guest
capabilities, account cookies and firmware credentials keep separate authority.
This guide prepares local code and configuration; no command below authorizes
production activation, paid calls or hardware operations.

## Independent artifacts

From the repository root, without a CAD toolchain:

```powershell
npm ci --prefix mobilni-aplikace
npm ci --prefix contracts
npm run build --prefix mobilni-aplikace
python scripts/ci/ui_boundary.py
python web/build-site.py --web-dist mobilni-aplikace/dist --destination <new-output>
```

`site-manifest.json` schema 2 records application source, API contract version and
all file hashes, including whether relevant source is dirty. Publication rejects
dirty provenance. It does not assert CAD verification or physical acceptance.
The output contains one compiled entry and fingerprinted assets; old online
shells are routes, not copied products. The artifact name `arcane-site` remains
for private pipeline compatibility. Recovery builds use this same compiled app
and the same packaging command; no standalone guest HTML build is maintained.
Retained schema-1 archives are not rewritten. Installer manifests/downloads and
explicit offline distributions remain separate from SPA routing.

Model generation and schema are owned by the Workshop/model tooling. A model
release is data only: `model-bundle.json`, `model.json` and declared hash-checked
assets. It carries its own source and verification provenance. The application
loads `/models/active.json` independently and stays usable if no model exists.
`bundleHash` hashes the canonical manifest (excluding its own hash), pinning all
asset hashes and provenance; metadata changes have a distinct immutable identity
even when the model JSON bytes stay unchanged.
A fixture is never silently published as production. Model publication checks
schema, compatibility, paths, sizes and hashes with the maintained app loader;
it does not regenerate geometry or recertify manufacturing ZIPs.

## Canonical routing and cache boundaries

Generate the complete reviewed compatibility snippet with:

```powershell
python web/deploy/keepsake_proxy.py --host application.example.invalid --compatibility --output <snippet> --http-map-output <http-map>
```

Use a real accepted canonical DNS host when preparing an actual configuration.
Keep its TLS/server shell and existing `/ios/`, installer files and
CloudVPSBridge locations. Replace the previous compatibility snippet as a unit;
do not include it together with another copy of `site-locations.conf` or old
`/keepsake/` static locations. Test the resulting complete server with `nginx -t`
before any separately authorized activation. The old route installer alone is
not the full identity/guest rollout procedure.

Page fallback is an exact allowlist. `/app/`, `/companion/`, `/simulator/`,
`/workshop/` and old evening URLs load the shared app so its router can handle
compatibility. `/keepsake/` claim fragments reach the inline capture unchanged,
without an HTTP redirect. Page responses permit GET/HEAD, use no-store and
no-referrer; private guest pages also carry noindex. `/app/v1/` reaches the
backend, including its errors; guest APIs and existing streams retain their
explicit route/method contracts and websocket headers. Unknown pages, callbacks,
missing assets and unrecognized API paths never receive HTML. Native callbacks
and installer/download paths must have their own verified exact server routes;
do not add them to page fallback.

Exact historical `index.html` entries and `/workshop/viewer.html` also load the
same app for router compatibility. They do not publish additional HTML files.
The HTTP CSP adds only `frame-ancestors 'none'`; it does not widen the compiled
application's separate meta script/style/connect policy.

`/assets/` uses retained fingerprinted files with immutable caching.
`/models/bundles/` serves immutable data with nosniff and a sandbox CSP.
`/models/active.json`, application HTML and release manifests use no-store.
Private APIs use no-store and have no public service-worker cache. The release
code does not install a service worker.

The private pipeline was inspected read-only at commit
`614f55e66ed64f945cd0cab35e44f4a811efdff6`. Its artifact/provenance gates remain
compatible, but its schema-1 publisher and IPv4 readback must change. The exact
[replacement workflow and readback helper](private-pipeline/README.md) are
provided as migration candidates. Production activation is separate from preparing
the private helper/workflow changes; the template executes reviewed private helpers.

The retained `publish-site.py` and `verify-site.py` implement schema-1 recovery;
their regression tests remain in `test_legacy_site.py`. They are not the
schema-2 application publisher. Before production rollout, update the vetted
private pipeline helper set to the new `release_store.py` contract and review its
invocation. Do not run old and new publishers against the same store: they use
different locking/retention contracts. A first migration from legacy HTML also
requires a paired configuration/legacy-release rollback plan; a schema-1 release
is deliberately rejected by the schema-2 rollback validator.

## Durable stores and atomic activation

An administrator must separately provision filesystem ownership/ACLs. The
application publisher identity needs write access only to its chosen application
store (example `/var/www/dildovander`); model publishers need the distinct model
store (example `/var/lib/dildovander/models`). The web server gets read access.
Backend identity keys and databases are never in either public store. Public
model publication requires an authorized publisher; filesystem permission is a
required gate, not an application account role inferred by the CLI.

The local `release_store.py` tool never connects to SSH or calls providers:

```sh
python web/deploy/release_store.py application publish --root /absolute/staging/app --source /absolute/built-app
python web/deploy/release_store.py model publish --root /absolute/staging/models --source /absolute/selected-bundle
python web/deploy/release_store.py application rollback --root /absolute/staging/app
python web/deploy/release_store.py model rollback --root /absolute/staging/models
```

These examples target explicitly provisioned local staging stores. Application
activation uses Linux atomic directory-symlink replacement. Publication is
serialized with an exclusive directory lock. Interrupted stale locks require
operator inspection; the tool never guesses that another writer is dead.
Content is checked and staged on the destination filesystem, then renamed to an
immutable identifier before activation. Existing content at that identifier must
match exactly. Model `active.json` switches atomically only after validation;
its previous pointer remains available for rollback. Application `current`
switches only after provenance/inventory checks and additive fingerprinted asset
publication; previous assets remain for open tabs and rollback. Invalid or failed
staging leaves the prior active release usable. No automatic cleanup deletes
models, archives, prior app releases or guest data.

For an authorized rollout, back up private configuration first, verify compatible
backend/API versions and DB upgrade/recovery, stage both artifacts, validate full
nginx configuration, then activate. Check backend health, canonical deep links,
account session/antiforgery, public Archive, scoped guest claim, websocket stream,
installer manifests and a missing-asset/API 404. If a check fails, activate the
recorded compatible previous app/model and recheck. Do not roll back a database
schema by replacing application files. Production health and nginx syntax have
not been verified by local Python tests.

## Identity persistence

`backend/deploy/application-identity.env.example` keeps accounts explicitly
disabled until provisioning is complete. DataRoot owns the identity SQLite DB;
DataProtectionRoot owns durable server keys outside app releases. Both must be
backend-owned under `/var/lib/kufr-backend`, already permitted by the systemd
service. With DV10 enabled, retain its established key root, application name and
key purposes; never rotate or replace keys during app activation. Back up the
identity database and keyring with the existing private archive recovery process.
Set the exact HTTPS origin and relying-party DNS name; never derive either from
an untrusted Host header. Bootstrap is separately provisioned, hashed and
expiring. No plaintext bootstrap secret belongs in CI artifacts or public code.

## Selecting a published model in the application

`ModelReleases` is disabled by default. The prepared
`backend/deploy/model-releases.env.example` names the same durable model
store as the public `/models/` route. Enabling it requires separately reviewed
filesystem permissions and the optional `model-releases-systemd.conf.example`
drop-in: the existing backend sandbox permits writes only beneath its ordinary
private data directory. Never grant the backend write access to application
code, guest archives or arbitrary publisher paths for this feature. Keep bundle
content immutable and writable only by the trusted publisher.

The administration panel lists the active reference and published model/source
provenance. Catalog reads validate manifests and declared inventory; complete
asset hashes and semantic shape are checked again on selection. The server
accepts only an existing immutable bundle hash, not a URL, upload or path. Only
Owner/Administrator accounts with a passkey authentication from the previous
ten minutes, valid same-origin antiforgery and explicit confirmation can change
the active reference. The observed previous hash is required, so a stale page
cannot overwrite another administrator's change. Unverified exports and test
fixtures cannot be selected. Trusted published CAD verification provenance is
backed by a hash-pinned `verification-json` asset: the successful report must
match the model ID/content hash, source revision and scoped source hashes, and
must explicitly report that no physical test was performed. Selection does not
run CAD or establish physical acceptance.

The backend and publisher share an exclusive `.activation-lock` file for active
reference changes, in addition to the publisher's wider staging lock. Both use
same-filesystem atomic file replacement and preserve `previous.json`; duplicate
selection does not discard the prior rollback reference. Stale locks require
operator inspection. This task does not provision that permission, enable the
feature on production, promote an unverified export, or activate a real model.

## Independent distribution downloads

Provision `/var/lib/dildovander/downloads` separately, with write access only for
the trusted distribution publisher and read access for nginx. Application and
model activation never delete or replace its bytes. The prepared static routes
serve `/downloads/index.json` without caching and immutable artifacts beneath
`/downloads/releases/<sha256>/<filename>` as attachments with `nosniff`. Unknown
download paths return 404. HTML, JavaScript and SVG are not direct downloads.

The operator-only publisher consumes existing local bytes; it does not connect
to a server, generate geometry or compare a model revision to an app revision:

```sh
python web/deploy/distribution_store.py publish --root /absolute/staging/downloads --source /absolute/verified-exports --file F2-kouzelny-kufr.zip --metadata /absolute/manufacturing-metadata.json
python web/deploy/distribution_store.py rollback --root /absolute/staging/downloads
```

The metadata JSON has `kind` (`manufacturing`, `model-archive`, `offline-viewer`),
`modelId`, `modelAssetSha256`, `label` and `version`, with optional `bundleHash`.
Model content and bundle hashes are lowercase SHA-256 values. An old
manufacturing archive without model-content evidence uses
`modelAssetSha256: null`; the UI identifies it as historical and unbound to the
selected model. A newly claimed binding must match the recorded verification
report. Model archives require their actual model content hash and are
explicitly marked `not-verified` by this publisher. Their ZIP must contain
`model-bundle.json` at its root and exactly the declared assets. The publisher
checks a confined, size-bounded extraction with the same maintained Node
validator as the application, then matches the claimed model ID, model-content
hash and optional bundle hash to those validated bytes. Missing, malformed,
undeclared or mismatched bundle content fails before index publication. Node
is required for this explicit preparation command; it does not run CAD.

Bound `offline-viewer` ZIP publication is currently rejected. The maintained
offline packager emits standalone HTML and a loose receipt, which do not
provide an equivalence-verifiable embedded model archive format. A supplied
receipt or model hash cannot establish that executable HTML renders those
bytes. Its existing standalone output remains a local download artifact;
wrapping it in a ZIP does not make it eligible for the public model-bound index.

Manufacturing promotion verifies the existing successful CAD report, recorded
source/export hashes, package checksum and complete frozen ZIP inventory. It
never imports CAD or reruns geometry; ordinary app assembly performs none of
these archive checks. The immutable ID is the archive SHA-256, and the index
records its byte count, content hash and `physicalAcceptance: false`. Offline
viewer ZIP composition remains an explicit separate task using the maintained
viewer source.

The original `/workshop/F2-kouzelny-kufr.zip` form remains a durable hard link to
the original archive bytes. A different archive cannot overwrite that URL.
For a new immutable URL with the same historical filename, use the explicit
`--without-legacy-alias` option; the old URL remains intact. Index publication
uses the destination filesystem lock and atomic replacement, preserves the
previous index for rollback, and retains every published payload. Rollback
checks the prior bytes before changing discovery. No production artifacts were
published or migrated by these preparation changes.

## Evidence boundaries

CI runs application/viewer fixtures without CAD; actual geometry changes retain
full CAD verification. Local Python tests check packaging, activation failure
handling and route allowlists. Linux CI exercises app symlink activation;
Windows cannot prove that filesystem operation. Browser and backend integration
checks prove their own layers; signed iPhone/passkey return, production nginx,
physical actuator behavior and offline `file://` inspection require their own
recorded evidence.
