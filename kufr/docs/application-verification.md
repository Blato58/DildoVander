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
| Shared Vue application | `npm ci`, `npm test`, `npm run build` on Node 24.21.0; 88 Node regressions and 111 Vue tests passed. Real components, retained protocol regressions, strict compiled CSP and lazy feature routes. |
| Rendered application | `npm run test:browser`: shared navigation, direct reload, legacy destinations, unknown routes, early private claim capture, explicit demo, focus across heartbeats, stale confirmation, Stop, theme, keyboard, 1440/390/320 px layouts and 200% text. The shell measures the bottom navigation so enlarged labels do not conceal the end of the page. |
| Accounts | `npm run test:accounts:fixture`: actual HTTPS backend, controlled private owner delivery, Chrome virtual WebAuthn authenticator, real passkey verifier and naming, invitation/replay denial, Viewer authorization, logout and safe return. A held real logout response proves that a new sign-in waits for its cookie update. Fixture configuration excludes providers; its normal shutdown removes its private temporary database and keys. |
| Backend | Build with zero warnings/errors; default regression and `--identity`, `--keepsake`, `--model-releases`, `--twin`, `--twin-media`. Tests cover real P256 WebAuthn, roles/grants/CSRF/origins, concurrency, recovery, last-owner/key rules, migration/backup restoration, private archive tickets/assets and revocation. |
| Portable firmware integration | `python kufr/simulator/native/build_host.py --output …` built the genuine portable C++ twin with MSVC. `--twin-media` used this executable for framing, cancellation, backpressure, input ownership and software drain. No ESP32 was connected or flashed. |
| Workshop offline | `node tests/workshop-offline.browser.mjs` opened the maintained bundled viewer through `file://` with networking disabled: desktop/mobile, selection, camera, assembly, visibility, source-semantic overlays and cable motion; zero network requests and page errors. Models were explicitly synthetic. |
| Workshop media | `node tests/workshop-media.browser.mjs`: fake browser devices and isolated API responses, two PCM frames, one accepted photo, software drain, zero external requests/page errors. This is not acoustic or provider evidence. |
| Model and release integrity | Shared browser/.NET rejection corpus, stdlib model export/certification tests, scoped source policy and validation refresh, bounded ZIP binding, immutable storage, idempotent publication and failed-publication protection. No mechanical calculations were substituted with fixtures. |
| CAD separation | `python scripts/ci/ui_boundary.py`, selector/impact/workflow regressions, actual compiled-site assembly and independent app build without CAD packages. The accepted incremental manufacturing pipeline and its trusted cache rules are retained. |
| Shared design and guests | Generated-token and design checks, design-system browser checks, Vue token scanning and `check_dv10.py --web-dist …`; historical DV10 source receipts remain separate. |
| Deployment preparation | Local schema-2 publication/rollback and routing tests; reviewed replacement private pipeline template. No private workflow was changed, deployment dispatched, server file modified or production data accessed. |

The browser suites block external traffic. Paid providers, real actuator
execution and manufacturing publication were not part of these checks.
The final backend `--identity` run includes the final naming/recovery endpoints;
the broader legacy default, twin and media runs preceded that bounded change.
They retain their actual scope rather than being presented as new native proof.

## Unverified boundaries

- Windows cannot run Xcode or the Swift harnesses. The configured macOS workflow
  checks native transport, Debug, BLE policy, Identity and the unsigned iOS
  Simulator build. Its result must be recorded separately from local web tests.
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
- Automatic approval review rejected recursive cleanup of one interrupted,
  isolated test directory under Windows Temp, without a detailed reason. That
  directory contains synthetic test state; later fixtures verify normal cleanup.

Production activation, credential rotation, paid calls, signing and flashing
require their existing authorization boundaries. No such action is implied by
the implementation or its software receipts.
