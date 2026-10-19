# Unified application verification

Repository implementation and isolated verification, 19 October 2026. Current
source identity and acceptance are recorded in the `KUFR-APPLICATION` work item
and its receipts under `docs/project/`. This document does not establish a
production installation or signed-device acceptance.

The [architecture and migration map](application-architecture.md) describes the
single entry, routes, source ownership and compatibility URLs. The practical
[account guide](account-setup.md) covers controlled owner enrollment, passkeys,
recovery, device grants, physical pairing and everyday use. Model/offline
commands are in [ModelBundle](model-bundle.md); application, model and download
publication are in the [release guide](../web/deploy/APPLICATION-RELEASES.md).

## Local evidence

| Area | Actual checks and boundary |
| --- | --- |
| Shared Vue application | `npm ci`, final `npm test`, `npm run build` on Node 24.21.0; final 91 Node regressions and 160 Vue tests across 27 files passed. The standalone build checked contracts, Vue types and 173 Vite modules without CAD. Real components, retained protocol regressions, strict compiled CSP and lazy feature routes. |
| Rendered application | `npm run test:browser`: shared navigation, direct reload, legacy destinations, unknown routes, early private claim capture, explicit demo, focus across heartbeats, stale confirmation, Stop, theme, keyboard, 1440/390/320 px layouts and 200% text. The shell measures the bottom navigation so enlarged labels do not conceal the end of the page. |
| Accounts | `npm run test:accounts:fixture`: actual HTTPS backend, controlled private owner delivery, Chrome virtual WebAuthn authenticator, real passkey verifier and naming, invitation/replay denial, Viewer authorization, logout and safe return. A held real logout response proves that a new sign-in waits for its cookie update. Fixture configuration excludes providers; its normal shutdown removes its private temporary database and keys. |
| Backend | Build with zero warnings/errors; default regression and `--identity`, `--keepsake`, `--model-releases`, `--twin`, `--twin-media`. Tests cover real P256 WebAuthn, roles/grants/CSRF/origins, concurrency, recovery, last-owner/key rules, migration/backup restoration, private archive tickets/assets and revocation. |
| Portable firmware integration | `python kufr/simulator/native/build_host.py --output …` built the genuine portable C++ twin with MSVC. `--twin-media` used this executable for framing, cancellation, backpressure, input ownership and software drain. No ESP32 was connected or flashed. |
| Workshop offline | `node tests/workshop-offline.browser.mjs` opened the maintained bundled viewer through `file://` with networking disabled: desktop/mobile, selection, camera, assembly, visibility, source-semantic overlays and cable motion; zero network requests and page errors. Models were explicitly synthetic. |
| Workshop media | `node tests/workshop-media.browser.mjs` against compiled production `dist` and its strict CSP: same-origin hashed capture worklet, fake browser devices and isolated API responses, two PCM frames, one accepted photo, software drain, zero CSP violations/external requests/page errors. Packaged Capacitor URL-policy cases are separate assertions. This is not WKWebView, acoustic or provider evidence. |
| Model and release integrity | Shared browser/.NET rejection corpus, stdlib model export/certification tests, scoped source policy and validation refresh, bounded ZIP binding, immutable storage, idempotent publication and failed-publication protection. No mechanical calculations were substituted with fixtures. |
| CAD separation | `python scripts/ci/ui_boundary.py`, selector/impact/workflow regressions, actual compiled-site assembly and independent app build without CAD packages. The accepted incremental manufacturing pipeline and its trusted cache rules are retained. |
| Shared design and guests | Generated-token and design checks, design-system browser checks, Vue token scanning and `check_dv10.py --web-dist …`; historical DV10 source receipts remain separate. |
| Deployment preparation | Local schema-2 publication/rollback and routing tests; reviewed replacement private pipeline template. No private workflow was changed, deployment dispatched, server file modified or production data accessed. |

The browser suites block external traffic. Paid providers, real actuator
execution and manufacturing publication were not part of these checks.
The final integration preserves main revision
`09a7cb32c7148b812ebbcd1326d3e16ba8d4ba9a`: the canonical GPT Live and
Realtime engines, persistent native audio WebSockets and scoped conversation
memory. After this integration, the backend build, full default regressions,
`--identity`, `--twin` and `--twin-media` passed against the rebuilt genuine
portable C++ executable. Account WebSockets revalidate persisted sessions,
roles and device grants before further audio. Retired modes remain rejected.
These checks do not contact an AI provider or establish native device acceptance.

The combined source review found no remaining actionable findings. Delayed
cleanup and delayed status responses have dedicated regressions: changing an
account synchronously redacts private phone state before awaiting USB/native
cleanup, and old responses cannot populate the next account. A compiled native
bridge fixture exercises both current engines, actual consent flags and
administration at desktop/mobile widths. Vue interaction tests cover photo
attach/discard. The synthetic bridge does not establish UIKit or microphone
acceptance.

PR review subsequently found retained private Workshop media on account loss.
The correction also clears server-derived observation snapshots, source lists
and recordings on account changes or authorization failures. Polling stops;
late polls, uploads and controller callbacks cannot restore the retired context.
Normal same-account unmount retains server cancellation. Reconnection requires
explicit source selection; public model inspection and locally imported replay
remain independent. Mounted Vue and headless media regressions cover these paths.

Observer requests now propagate cancellation to browser fetch. A cancelled native
bridge response is ignored before parsing or applying an old unauthorized result.
Method-missing fixtures cover portable cancellation and secure native public-load
request IDs. These narrowly scoped checks do not establish whole-app iOS 15
compatibility or replace signed-device testing.

PR CI run `39194919939` passed the complete backend checks but exposed failures
in Linux legacy-site subprocess startup, CAD packaging fixtures, the Swift Debug
test actor boundary, and two historical header-allowlist false positives in the
secret scan. Those failures are preserved in the PR run. Corrections retain the
real locking, CAD proof, actor and secret-detection gates. Independent review also
found production worklet CSP and packaged-native public model resolution defects;
their regressions now exercise production `dist` and the native public adapter.
The updated CI run is recorded separately after it completes.

Native public-resource tests validate paths, size limits, cancellation ordering,
canonical downloads and actual model hash/JSON parsing with a mocked bridge,
including non-`.json` assets. They do not execute Foundation TLS/delegate behavior.

## Unverified boundaries

- Windows cannot run Xcode or the Swift harnesses. The macOS job in PR run
  `39196137977` passed native transport, Debug, BLE policy, Identity and the
  unsigned iOS Simulator build. `KUFR-APPLICATION-native-v1` records that software
  result separately for the earlier source; the merged native changes require
  a fresh macOS run. Signed-device acceptance remains inconclusive.
- Actual passkey associated domains, signing, Keychain upgrade/resume behavior,
  physical BLE possession, LAN reachability, iPhone audio/camera and real hardware
  remain device acceptance steps. Browser and portable C++ tests do not prove them.
- No actual model/manufacturing regeneration was performed locally. Focused
  stdlib proof tests passed; broader legacy regeneration tests require the CAD
  environment (`numpy`/`cadquery`). Synthetic geometry is rejected in production.
- Linux atomic-symlink behavior is skipped on Windows; local Python tests do not
  prove actual nginx syntax, TLS, persistent-store ACLs or VPS health.
- The existing private site pipeline requires the prepared migration. Its first
  schema-1 to schema-2 cutover deliberately fails closed until the real nginx,
  app and rollback configuration are reviewed together. Published historical
  archives remain immutable. Bound offline ZIP publication is rejected; the
  verified standalone offline HTML command remains available.
- Automatic approval review rejected cleanup of one interrupted, isolated test
  directory under Windows Temp, including a bounded individual-file attempt,
  with the stated reason `blocked by policy`. That directory contains synthetic
  test state; later fixtures verify normal cleanup. No cleanup bypass was used.

Production activation, credential rotation, paid calls, signing and flashing
require their existing authorization boundaries. No such action is implied by
the implementation or its software receipts.
