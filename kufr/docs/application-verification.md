# Unified application verification

Repository implementation and isolated verification, 19 October 2026. Current
delivery and remaining acceptance are recorded in [Issue #227](https://github.com/Blato58/DildoVander/issues/227)
and [implementation PR #218](https://github.com/Blato58/DildoVander/pull/218).
The `KUFR-APPLICATION` records under `docs/project/` are dated historical
snapshots with their original source scope; they are no longer a live tracker.
This document does not establish production or signed-device acceptance.

The [architecture and migration map](application-architecture.md) describes the
single entry, routes, source ownership and compatibility URLs. The practical
[account guide](account-setup.md) covers controlled owner enrollment, passkeys,
recovery, device grants, physical pairing and everyday use. Model/offline
commands are in [ModelBundle](model-bundle.md); application, model and download
publication are in the [release guide](../web/deploy/APPLICATION-RELEASES.md).

## Local evidence

| Area | Actual checks and boundary |
| --- | --- |
| Shared Vue application | `npm ci`, final `npm test`, `npm run build` on Node 24.21.0; final 123 Node regressions and 206 Vue tests across 29 files passed (329 total). The standalone build checked contracts, Vue types and shared lazy feature modules without CAD. Real components, retained protocol regressions, strict compiled CSP and lazy feature routes. |
| Show Pack Vue | `node tests/director.browser.mjs` and `node scripts/archive-show.browser.mjs`: compiled Director two-operator conflict, scoped grants, urgent/lost Stop, route navigation, guest wizard identity/private prophecy, scoped duel moves, chronicle zero/tie/withdrawn and authorized event selection; actual partial PNG bytes, keyboard and 1440/390 px layouts. All APIs in these suites are synthetic. |
| Rendered application | `npm run test:browser`: shared navigation, direct reload, legacy destinations, unknown routes, early private claim capture, explicit demo, focus across heartbeats, stale confirmation, Stop, theme, keyboard, 1440/390/320 px layouts and 200% text. The shell measures the bottom navigation so enlarged labels do not conceal the end of the page. |
| Accounts | `npm run test:accounts:fixture`: actual HTTPS backend, controlled private owner delivery, Chrome virtual WebAuthn authenticator, real passkey verifier and naming, invitation/replay denial, Viewer authorization, logout and safe return. A held real logout response proves that a new sign-in waits for its cookie update. Fixture configuration excludes providers; its normal shutdown removes its private temporary database and keys. |
| Backend | Build with zero warnings/errors; current default regression and `--identity`, `--show`, `--show-live-boundary`, `--twin`, `--twin-media`; archive/model suites also passed before the Show integration. Tests cover real P256 WebAuthn, roles/grants/CSRF/origins, concurrency, recovery, last-owner/key rules, migration/backup restoration, private archive tickets/assets and revocation. |
| Portable firmware integration | `python -B kufr/firmware/tests/run_show_tests.py` passed 20 offline checks and 55 actual firmware serialized messages; offline bank tests passed 3/3. `python kufr/simulator/native/build_host.py --output …` rebuilt the genuine portable C++ twin with MSVC; host 18/18 and media 21/21 passed. `--twin-media` used this executable for framing, cancellation, backpressure, input ownership and software drain. No ESP32 was connected or flashed. |
| Workshop offline | `node tests/workshop-offline.browser.mjs` opened the maintained bundled viewer through `file://` with networking disabled: desktop/mobile, selection, camera, assembly, visibility, source-semantic overlays and cable motion; zero network requests and page errors. Models were explicitly synthetic. |
| Workshop media | `node tests/workshop-media.browser.mjs` against compiled production `dist` and its strict CSP: same-origin hashed capture worklet, fake browser devices and isolated API responses, two PCM frames, one accepted photo, software drain, zero CSP violations/external requests/page errors. Packaged Capacitor URL-policy cases are separate assertions. This is not WKWebView, acoustic or provider evidence. |
| Model and release integrity | Shared browser/.NET rejection corpus, stdlib model export/certification tests, scoped source policy and validation refresh, bounded ZIP binding, immutable storage, idempotent publication and failed-publication protection. No mechanical calculations were substituted with fixtures. |
| CAD separation | `python scripts/ci/ui_boundary.py`, selector/impact/workflow regressions, actual compiled-site assembly and independent app build without CAD packages. The accepted incremental manufacturing pipeline and its trusted cache rules are retained. |
| Shared design and guests | Generated-token and design checks, design-system browser checks, Vue token scanning and `check_dv10.py --web-dist …`; historical DV10 source receipts remain separate. |
| Deployment preparation | Local schema-2 publication/rollback and routing tests; reviewed replacement private pipeline template. No private workflow was changed, deployment dispatched, server file modified or production data accessed. |

The browser suites block external traffic. Paid providers, real actuator
execution and manufacturing publication were not part of these checks.
The integration also preserves accepted Show Pack main revision
`838ac64983bb7eb8a989d5026fbda542a9370e4d`. Its Director, wizard identity,
private prophecy, duel and chronicle use the shared Vue application. Account
Show authorization checks exact device grants and route aliases; firmware cue
polling and physical outcome reports retain device authority. An operator relay
is labelled separately. Routine Stop has reserved native request capacity;
ordinary offline actions reject a selected-cloud/local-device mismatch.
Chronicle administration lists authorized devices and existing evenings, accepts
opaque historical evening IDs, and rejects a mismatched response. Public duel
choices use only the scoped player grant. Linked accounts receive public wizard
identities and do not acquire private prophecy access.

Production browser verification exposed a Windows line-ending error in early
capability capture: the raw CRLF inline script hash differed from the browser's
HTML-normalized LF hash. The Vite CSP hook now hashes parser-normalized text;
LF, CRLF and CR regressions preserve the strict policy. Compiled browser checks
cover the actual early grant capture and reject CSP violations.

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
  `39202887317` passed native transport, Debug, BLE policy, Identity and the
  unsigned iOS Simulator build for source-v7. `KUFR-APPLICATION-native-v2` records
  that result; the new Show integration requires a fresh macOS run. Signed-device acceptance remains inconclusive.
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

Accepted main `706c3dd89344081fd42f6a27d95afb65b13c5182` adds dependency
automation and one native pin regression. At that integration, fresh mobile checks passed 315 tests
and build; all 56 rebuilt files are byte-identical to runtime-v6. Backend
source identity is unchanged. Dependency controller tests pass 20 Node and
19 Python cases. Source-v9/runtime-v7 explicitly transfer the prior browser
and backend evidence through those identical bytes; no additional browser
execution is claimed for this integration.

Current native account corrections replace overlapping-request rejection with a
bounded FIFO shared by ordinary account requests and the whole passkey ceremony.
Strict cancellation commands have a separate bounded lane and an immutable
published Cookie/CSRF snapshot. That transport cannot accept cookies, credentials
or cached responses; Stop remains available during options, credential HTTP
and a held same-account session refresh. Old queued scopes cannot use the next
credential. Delayed native refresh cancellation preserves the account only
during authentication; current cookie loss and real HTTP401 still clear it.
A delayed Blob body checks generation and cancellation before bridge dispatch.
The 21 focused Vue identity tests pass. The Foundation URLSession harness is
configured to compile the production scheduler and policy; actual macOS execution
belongs in a source-bound native receipt. These tests do not exercise the
UIKit passkey dialog or a physical iPhone.

CI `39212241042` compiled the Foundation identity harness but failed its first
committed-cookie assertion before the Simulator build. That assertion lacked a
positive precondition: the custom URLProtocol had not modeled ordinary cookie
installation. The corrected fixture explicitly applies normal response cookies
through HTTPCookie parsing/storage and asserts renewal, committed/new-login
cookies before late replies, logout removal and unchanged cookies afterwards.
Priority replies use the actual nil cookie store. The cookie-fixture correction
models cookie processing and does not claim a real HTTP/TLS
cookie pipeline. The failed native-v3 receipt remains immutable; fresh macOS
execution is required for the corrected source.

Request-scoped native cancellation now carries UUIDs from both account bridge
methods into a bounded task registry. Abort removes ordinary/priority waiters
before HTTP dispatch; admission/cancellation races have a final dispatch guard.
Active HTTP cancellation is best effort and cannot undo a server mutation.
Source-v11: 327 app tests, production build, six compiled browser journeys, 55-file
site assembly, design integrity and compiled DV10/CSP checks pass. Native
zero-HTTP cancellation/races/queue progress/bounds/401 harness cases have
independent source review; fresh macOS execution remains required.

Source-v12 retains valid anonymous CSRF independently of authenticated priority
credentials. Cold native sign-in/enrollment options have a production-helper
URLSession regression; real Swift execution remains a fresh macOS CI gate.
Settings identifies the assets actually running with an embedded Git revision
and explicit dirty/unknown provenance; it never fetches a mutable release pointer.
Connections records only validated local snapshots/correlated heartbeats and
labels the phone clock/current app session, never a pairing or connect attempt.
Combined local tests pass 123 Node + 206 Vue (329 total); independent combined
source review found no remaining actionable defect. Signed-device evidence is
still separate from these software checks.
