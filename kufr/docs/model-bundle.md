# Independent model, application and offline releases

The integrated viewer is `/workshop` in `kufr/mobilni-aplikace`.
`WorkshopView.vue` owns controls and feature state. `engine.mjs` owns the WebGL
lifecycle around the original extracted `viewer_engine_core.js` algorithms,
GPU buffers, triangle picking and camera gestures; `dispose()` cancels frames,
removes handlers and releases GPU resources. GPU objects and model payloads stay
outside Vue deep reactivity. Selection uses semantic part IDs, never array order.

`twin_core.js`, `timeline_core.js` and `twin_media_core.js` are shared parsers.
`twin_media_controller.js` is the single headless browser HAL used by Vue and the
legacy manufacturing viewer adapter. It emits presentation snapshots, not HTML.
Vue owns the video element, settings, device choices, buttons and transient
dialogue. Leaving the route, changing source/boot/mode/permission, Stop or stale
authority closes local capture/playback, clears private buffers and revokes
preview URLs. Media start/permissions/photo submission require explicit actions.
Audio drain is software graph evidence, never measured audible output.

## ModelBundle v1

Each release directory contains `model-bundle.json` and declared data assets.
The manifest schema is `model-bundle.schema.json`; runtime validation is
`src/features/workshop/model-bundle.mjs`. `loadBundle` verifies manifest identity,
asset paths, total size, byte counts, SHA-256 and semantic data before rendering.
The filesystem CLI additionally rejects symlinks and undeclared files.

- `schemaVersion: 1`, stable `modelId`, revision, units `mm` and coordinates
  `right-handed-z-up` identify the model independently of the app Git revision.
- `bundleHash` is SHA-256 of UTF-8 canonical JSON of the complete manifest with
  the `bundleHash` property removed: recursively lexicographically sorted object
  keys, compact JSON, preserved array order, no trailing newline. Manifest fields
  contain strings and integers, avoiding cross-language floating point formats.
  All provenance/compatibility/asset metadata participates in this identity.
- `assets` declare confined relative paths, byte lengths, SHA-256 and kind.
  Exactly one `model-json` asset contains the existing indexed triangle format;
  additional `bom-json` assets are data only. A verified release contains exactly
  one `verification-json` asset holding its successful CAD report. The model asset
  has its own digest.
- `compatibility.viewerSchema: 1` is a semantic contract, not an app SHA match.
  Provenance records `cad-export` or `synthetic-fixture`, model source revision
  and explicit `cadVerification`. Export defaults to `not-verified`; exporting a
  viewer never recertifies geometry, manufacturing archives or physical assembly.
- Model JSON includes stable named mesh `id`/`name`, group, color, bounds, indexed
  geometry, identity transform and an explicit null parent ID. V1 tessellations are in
  global model coordinates; nonidentity transforms and nonnull parent links are
  explicitly unsupported in v1 and rejected. IDs derive from CAD semantic names,
  not mesh order. Local-coordinate hierarchy requires a future contract.
  Assembly guide steps/patterns, cable routes, connections, BOM references,
  source evidence, presentation target, effects/compiled plan and telemetry
  mapping remain data. Telemetry and recordings do not belong in the bundle.
- Limits: 128 KiB manifest, 96 MiB total assets, 32 assets, 10,000 parts, bounded
  indices/routes/assembly steps. Absolute paths, traversal, backslashes, encoded
  path characters, unknown schema versions and executable asset kinds fail closed.
  Metadata is rendered through Vue text interpolation; no model HTML executes.

The published reference `/models/active.json` is
`{schemaVersion:1,modelId,bundleHash,manifestPath:"bundles/<bundleHash>/model-bundle.json"}`.
The loader pins both full manifest identity and model ID, follows no redirects
and sends no credentials to model resources. Publishing and rollback use the
separate durable release store described in `kufr/web/deploy/APPLICATION-RELEASES.md`.

The immutable `/models/bundles/` location serves every declared JSON-kind asset
as `application/json` regardless of its safe filename. Extension MIME mappings
are disabled; `nosniff` and sandbox headers remain required. Capacitor reads these
public resources through a bounded, credential-free native transport against the
configured HTTPS application origin, including assets and the download index.
Missing models produce an honest unavailable state; unrelated app routes work.
Production loading rejects synthetic fixtures. Development opts in explicitly
with `/workshop?fixture=1`; a fixture always carries a visible synthetic label.

Telemetry animation requires matching CAD manifest revision and hardware
profile plus fresh snapshots. Unknown/mismatched/stale mappings disable movement
and show uncertainty. Replay remains historical. Observation uses account-scoped
GET requests and never obtains a control lease. Simulation commands are available
only after explicitly choosing an authorized simulation; server authorization
remains authoritative. Live sources and replay never expose simulation commands.
Media uses the same account/native cookie jar, CSRF and scoped binary transport.

## Commands and evidence boundaries

From the repository root, app builds do not import Python or run CAD:

```text
npm run dev --prefix kufr/mobilni-aplikace
npm run build --prefix kufr/mobilni-aplikace
node --test kufr/mobilni-aplikace/tests/workshop.test.mjs
npm run test:components --prefix kufr/mobilni-aplikace -- src/features/workshop
node --test kufr/mechanika/final/test_twin_media.cjs kufr/mechanika/final/test_twin_viewer.cjs kufr/mechanika/final/test_show_timeline.cjs
```

The CAD pipeline's existing `preview.py` exports `exports/model-release/` from
its actual tessellations; its geometry generation and verification commands remain
in the mechanical README. `export_model_bundle.py` itself uses only the standard
library and can package a previously exported JSON payload without CAD imports:

```text
python kufr/mechanika/final/export_model_bundle.py --payload MODEL.json --output MODEL_RELEASE
node kufr/mobilni-aplikace/scripts/validate-model-bundle.mjs --bundle MODEL_RELEASE
node kufr/mobilni-aplikace/scripts/package-offline-viewer.mjs --bundle MODEL_RELEASE --output OUTPUT/workshop.html
```

The CAD pipeline writes `model-generation.json`, binding exact model data to a
conservative mechanical/model source inventory. UI inputs are explicitly excluded;
unknown dependencies remain included. Only after actual existing `verify.py`
checks pass does it add `modelEvidence` to the report and certify that bundle.
The certificate binds model ID, model asset SHA-256, source revision and scoped
source hashes to the report; it states `physical_test_performed: false`.

An already verified artifact can be certified without geometry generation:

```text
python kufr/mechanika/final/certify_model_bundle.py --bundle MODEL_RELEASE --verification exports/verification.json --source-root kufr/mechanika/final
```

It rejects missing/failed reports, changed data or mechanical inputs, synthetic
exports and unbound evidence. UI-only changes and a later app Git revision do not
invalidate the model proof. Certification adds a report and changes the bundle
identity while preserving model bytes. Publication remains separate. Legacy
manufacturing inventory/archive verification is preserved; app builds never
import its validators. This task exercised synthetic proof tests, not actual CAD.

Downloads use independent `/downloads/index.json` metadata. The Workshop shows
immutable same-origin data/archive links with version, SHA-256 and evidence limits.
Pre-ModelBundle archives without model-data hashes appear as historical and are
never described as matching the selected geometry. HTML, JavaScript, SVG,
external URLs and synthetic manufacturing downloads are rejected. See
`kufr/web/deploy/APPLICATION-RELEASES.md` for publication.

Offline composition uses Vite and the exact Vue view, shared components, tokens,
fonts, headless HAL and WebGL engine. Its IIFE, CSS, fonts and selected model data
are inline in a serverless HTML file. CSP blocks all network connections; no
account, credentials, guest material or model fetch is required. The sidecar
manifest records separate model and viewer release metadata. Legacy immutable
manufacturing archives are preserved and not rewritten by application releases.

Small synthetic acceptance, without geometry generation or provider calls:

```text
python kufr/mechanika/final/export_model_bundle.py --fixture --output FIXTURE_RELEASE
node kufr/mobilni-aplikace/scripts/package-offline-viewer.mjs --bundle FIXTURE_RELEASE --output OUTPUT/fixture.html --allow-fixture
node kufr/mobilni-aplikace/tests/workshop-offline.browser.mjs
node kufr/mobilni-aplikace/tests/workshop-media.browser.mjs
```

The browser test opens `file://`, disables networking, checks real WebGL controls,
selection, assembly navigation, keyboard/mobile layout and screenshots. Set
`PLAYWRIGHT_CHANNEL=chrome` to use an installed Chrome instead of a downloaded
test browser. This is repeatable offline packaging evidence, not CAD acceptance,
provider evaluation, signed iPhone acceptance or physical actuation evidence.
