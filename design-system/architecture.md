# Vue application and Vapour Chrome

The maintained online product is the Vue 3 application in
`mobilni-aplikace`. Web and Capacitor consume the same SFCs, router and
feature services. See [application boundaries and migration map](../docs/application-architecture.md).
Swift owns native capabilities, not a second screen implementation.

## Presentation and state

- `tokens.json` remains the single design source. Use `arcane-workshop.css`,
  semantic `--aw-*` values and shared `aw-*` styles. Generate changes with
  `scripts/generate_design_tokens.py`; do not edit generated tokens.
- `mobilni-aplikace/src/components` contains typed Vue primitives: `AwButton`,
  `AwField`, `AwNotice`, `AwStatus` and `AwDialog`. Prefer slots and events over
  markup factories. Domain components live with their feature.
- `src/application` owns the persistent shell, lazy routes, safe login return
  paths, notices and Pinia theme preferences. Forms and drafts stay local.
- `features/suitcase/deviceSession.ts` owns the active transport and shallow
  device snapshot. Native/protocol classes retain lease, revision, pinning,
  reconciliation and Stop rules. Views cannot invent hardware readiness.
- `features/identity` owns account/session presentation. Server authorization,
  guest capabilities and physical phone pairing remain independent authorities.
- `features/archive` owns route-scoped guest clients, consent and media object
  URL lifetimes. A stale response cannot restore departed private content.
- `features/workshop` owns model loading, GPU lifecycle and view modes. Keep
  GPU objects and frame state outside deep Vue reactivity. Inspection and replay
  never acquire actuator control.

Telemetry updates patch stable Vue nodes; they must not rebuild a screen or
replace a focused input. Physical confirmations carry their originating target
and revision and are invalidated when either changes. Retain active operations
across ordinary navigation; urgent Stop remains in the shell.

## Application and distribution boundaries

The hosted site builder consumes the compiled app, independently of CAD.
ModelBundle data is schema/version/hash checked and published as an immutable
release with a separate active reference. Application and model provenance need
not share a Git SHA. An unavailable model leaves other routes usable.
The offline packager bundles the same maintained Vue viewer with a selected
model; it must work through `file://` without networking or account credentials.

`ui.js` and its playground remain compatibility APIs for local USB service tools,
old test harnesses and immutable archived manufacturing distributions. They are
not the renderer for new online screens. Preserve externally required iOS
installation pages as distribution adapters. Do not silently rewrite previously
verified manufacturing archives when changing application code.

## Verification

In `mobilni-aplikace`: `npm ci`, `npm test`, `npm run typecheck`,
`npm run build`, `npm run test:browser`. Install contract dependencies with
`npm ci --prefix contracts` from repository root. Browser tests use isolated
synthetic APIs, real compiled components and desktop/mobile sizes; no provider
calls or physical control are involved. Native changes also require the macOS
workflow and signed-device acceptance where specified.

Run design token drift and compatibility checks after shared styling changes:
`python scripts/generate_design_tokens.py --check`,
`python scripts/check_design_system.py`, and `npm test --prefix design-system`.
Use the affected service/CAD compatibility checks when changing those consumers.
Normal application UI verification must never run CAD generation or full
manufacturing validation; `scripts/ci/ui_boundary.py` enforces that boundary.
