# Dual-theme validation, 2026-10-20

This receipt covers the local implementation based on original-design checkpoint
`5f784a1d95b201cc50cacd33f4cf69655399ab45`. CI and signed-device acceptance
are separate evidence. [Theme architecture and restoration](THEMES.md).

## Automated checks

| Check | Result |
| --- | --- |
| `python scripts/generate_design_tokens.py --check` | Generated CSS, registry/runtime/types, native launch and installer current |
| `npm test --prefix design-system` | 14 Node tests and 9 Python tests pass; includes palette preservation, fallback, blocked storage, persistence, cross-tab updates and text contrast |
| `python scripts/check_design_system.py` | Token references, shared imports, legacy palettes and export boundary pass |
| `python -m unittest discover -s scripts -p test_design_system.py` | 10 isolated integrity tests pass; fixture includes the generator's authored theme runtime dependency |
| `npm test --prefix mobilni-aplikace` | 126 Node tests and 213 Vitest tests pass |
| `npm run build --prefix mobilni-aplikace` | Contract check, Vue typecheck and Vite build pass |
| `npm run test:browser --prefix mobilni-aplikace` with `PLAYWRIGHT_CHANNEL=msedge` | Both identities, all 14 route views, synthetic owner settings/admin, draft retention, persistence, cross-tab synchronization, keyboard navigation, claim isolation and CSP pass |
| `node mobilni-aplikace/tests/workshop-offline.browser.mjs` with the same browser channel | Real packaged file viewer, both identities at 1440/390, same canvas retained, zero network requests/page errors |
| `npm run test:browser --prefix design-system` with `UI_BROWSER_CHANNEL=msedge` | Components, simulator, disconnected servo panel, both installer states and generated synthetic viewer pass in both identities |
| `npm test --prefix firmware/simulator` with a freshly compiled host bridge | Dashboard, native bridge, conversation, TLS/control socket and media socket checks pass, including the bootstrap asset request |
| `node --test firmware/diagnostics/servo-bench/web/tests/panel.test.mjs` | 11 tests pass |
| Servo panel Python HTTP tests | 3 tests pass |
| `node --test mechanika/final/test_viewer.cjs` | 56 tests pass, including theme selection uniform/material preservation |
| `python -m unittest discover -s mechanika/final -p test_viewer_document.py` and `test_viewer_style.py` | 14 tests pass; offline bootstrap precedes CSS and retained snapshot works without the shared source tree |
| `python scripts/ci/ui_boundary.py` | 14 entry points; no CAD toolchain dependency |
| `python scripts/ci/cad_impact.py` | Conservatively flags `preview.py` as manufacturing input; its only change moves source-reference HTML rendering into the tested CAD-independent presentation helper |
| Plist parsing and `git diff --check` | Pass |

The host bridge was built in a temporary directory using MSVC C++14 with
`/W4 /WX`, from the five sources documented by `firmware/simulator/README.md`.
No firmware image or physical device was changed. Dependencies were installed
with existing lockfiles for contracts, mobile, design-system and simulator.
Node 25.9.0 emitted engine-version warnings against pinned package expectations;
all listed checks completed successfully. Vite emitted chunk-size and existing
mixed-import warnings. No dependencies or lockfiles were changed.

## Rendered evidence

The established Playwright harness was used because it repeats the same
theme/state/viewport matrix and forbids external traffic and hardware actions.
An in-app Browser opening attempt timed out; it was not used as evidence.

The compiled app was rendered at 1440×1000, 390×844 and 320×844 under both
identities, with a 200% text check at 320px. The route matrix includes home,
connections, suitcase, diagnostics, director, Workshop, archive, duel, chronicle,
keepsake, sign-in, settings, administration and not-found. Unauthenticated and
invalid guest-capability screens remain explicitly gated. Synthetic owner
settings/admin were additionally rendered at 1440/390; switching preserved the
same management input and its unsaved value without any mutation request.
No real account/passkey enrollment or live private visitor data was used.

Shared components were checked at 320/390/768/1280 with native dialog focus,
tabs, field errors, disabled/loading/empty states, local fonts, 44px controls,
reduced motion and 200% text. Simulator, servo panel, installer states and
retained viewer were checked at 320/390/1280. Viewer checks exercised camera,
docks, timeline dialog focus, assembly context and visibility recovery; a literal
`file://` document also worked with networking disabled. The Vue offline viewer
used a clearly labeled synthetic ModelBundle, not a new CAD output. The generated
source-reference document was also rendered through `render_reference_document`
at 320/1280 under both identities over `file://` with networking disabled,
unchanged synthetic evidence text, and zero additional requests or page errors.
The source-reference HTML call site in `preview.py` was syntax-checked without
importing or running CAD. Its conservative manufacturing classification means
this UI work does not recertify a future manufactured package from these sources.

Screenshots/logs remain in the local temporary evidence directories
`dildovander-application-browser`, `dv-theme-shared-screens`, and the
`dildovander-workshop-*` packaged-fixture directories. They are not source
assets or published artifacts. Parent inspection compared both visual
identities and desktop/mobile layouts; the immutable original-token test
independently protects Olivander's original palette, typography and base layout.

## Boundaries

No geometry, manufacturing verification, model release, production configuration,
account authorization, hardware actuation or paid AI call was changed/run.
Web/API mocks demonstrate presentation and state preservation, not real backend
authentication. Native launch/default appearance is configured and parsed;
macOS CI and a signed iPhone still own Swift/build and physical acceptance.
Cross-device preference synchronization is unavailable because the existing
profile has no preference API; same-origin tabs and local reloads are supported.
The supplied Codex palette was used without claiming pixel-perfect native
Codex reproduction.
