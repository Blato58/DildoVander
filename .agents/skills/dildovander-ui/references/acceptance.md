# Targeted UI acceptance

Read only the affected section. Resolve paths from the repository root and
check this branch's source before treating a control or scenario as supported.
Use `kufr/design-system/screens.md` and `accessibility.md` for shared acceptance.
Record the actual artifact, viewport, input method and tested states; missing
device/input coverage remains a gap, not a pass.

## CAD / Workshop viewer

Start with `kufr/mechanika/final/README.md`, `viewer-template.html`, `viewer.js`,
`viewer-workshop.css` and `test_viewer.cjs`. Geometry, assembly and effect sources
include `parameters.json`, `assembly-guide.json`, `effects.json` and `harness.py`.

- **Camera and input:** exercise orbit/drag, wheel zoom, fit and named views.
  Check selection versus drag, pointer capture/cancel and recovery after leaving
  the canvas. Inspect actual pan and touch/pinch support before specifying it;
  the current README promises drag rotation and wheel zoom. Do not declare pan,
  pinch or multitouch implemented from a mockup or a desktop mouse test. Where
  an input is unavailable, report the limitation and verify existing alternatives.
- **Selection and visibility:** select from canvas, tree and inspector; clear
  via Escape/empty space; check pinned selection, hidden/isolated layers,
  transparency, dimensions and wire/endpoint selection. State and labels must
  stay coherent across mode changes and restored views; hidden geometry must
  not misleadingly remain selectable or described as visible.
- **Assembly and exploded views:** check only the real modes/controls present
  in source. Where an exploded/spread view exists, verify reset, fit, labels and
  selected-part mapping. Check cumulative assembly steps, materials/tools and
  transport visibility against `assembly-guide.json` and the README. CAD fit,
  collision checks and a visual assembly never prove printability or physical fit.
- **Effects:** use shared `effects.json` through the real generation path;
  verify affected timeline, tray/lid, light and optional audio states, Stop,
  unknown intermediate position, service reset and the existing 15 s limit.
  Viewer timing is illustrative; physical firmware timing depends on calibration.
- **Performance:** use a representative full assembly and wiring/Inspect mode;
  check resize, zoom, picking, overlays and panel toggles for stalls or excessive
  redraws. Preserve the existing scheduled drawing behavior. Report measured
  behavior when relevant; do not invent an FPS budget or add a second renderer.
- **Accessibility:** exercise labeled native controls, tree, panel focus and
  selection alternatives without relying solely on canvas hover or color.
  Verify touch target sizes, 200% zoom, narrow layouts, focus restoration,
  contrast and reduced motion against the shared accessibility document.

Run focused viewer checks when relevant: `node --check viewer.js` and
`node --test test_viewer.cjs` from `kufr/mechanika/final`. When viewer/generation
source changes, use `Regenerate.ps1 -Python <prepared-python>` from that directory
with its documented requirements. It runs source checks, generates artifacts,
verifies them and packages the result. Inspect the generated offline viewer and
its provenance; a source test alone does not prove the HTML/ZIP output. Do not
regenerate CAD for guidance-only changes or publish without task authorization.

## Simulator / Simulation Lab

Start with `kufr/simulator/README.md`, `package.json`, `dashboard.html`,
`dashboard.js`, the bridge/server source and corresponding `verify-*.mjs` checks.
Trace firmware model ownership in `kufr/firmware/src/features/` where relevant.

- **Model versus hardware:** distinguish C++ host model, local bridge/fake
  adapters, simulated firmware state and physical firmware/GPIO. Preserve visible
  SIMULACE labeling and `simulated:true`; snapshots, animation, host tests or a
  successful fake response never establish measured position, device safety,
  physical audio/video or an actual ESP32 connection.
- **State and transport:** verify the existing snapshot/event flow, affected
  phase/mode/tray/journal display, bounded event history and actual connection,
  error/reconnect states. Unknown/offline must not look healthy or measured.
  The dashboard observes `GET /v1/observe`; it cannot acquire manual lease or
  issue actuator commands. Keep control/media transport and lease boundaries.
- **Credentials and previews:** keep tokens in memory, clear the entry field,
  and keep secrets out of URLs, storage, logs and screenshots. Published static
  previews retain `data-static-preview` and cannot connect with local tokens.
- **Shared rendering:** follow existing `dashboard.js`, mobile view/state owners
  and viewer source when comparing consumers. Reuse the canonical CSS/components
  and shared effect data where already applicable; do not fork these for a visual
  change or assume the observer, CAD preview and native mobile share an engine.
  Verify every affected consumer when changing shared tokens or rendering data.
- **Checks:** `npm test` in `kufr/simulator` currently runs five Node checks:
  dashboard, bridge, conversation, control socket and media socket. Follow the
  README for prepared C++ bridge, dependencies, local TLS and runtime prerequisites.
  Run the affected tests and inspect the live dashboard for behavioral UI changes;
  mocked dashboard checks do not prove a live bridge, browser or physical board.

Native mobile web/fake, browser, signed iPhone and real hardware acceptance are
separate. Validate only the authorized runtime and state the untested boundary.
