# Unified application verification

Repository implementation and isolated verification, 19 October 2026. Current
delivery and remaining acceptance are in [private acceptance](../../docs/development-coordination.md)
and [implementation PR #218](https://github.com/Blato58/DildoVander/pull/218).
Earlier implementation receipts remain in that PR's commit history and the
private historical archive. They do not establish current application acceptance.

The [architecture and migration map](application-architecture.md) describes the
single entry, routes, ownership and compatibility URLs. The practical
[account guide](account-setup.md) covers first-owner enrollment, passkeys,
recovery, grants, physical pairing and everyday use. Model/offline commands are
in [ModelBundle](model-bundle.md); publication and rollback preparation are in
the [release guide](../web/deploy/APPLICATION-RELEASES.md).

## Executed local checks

| Area | Actual checks and boundary |
| --- | --- |
| Shared Vue application | `npm ci`, `npm test`, `npm run build` on Node 24.21.0: 123 Node + 206 Vue tests across 29 Vue files (329 total), generated contracts, Vue typecheck and Vite build. The application build requires no CAD toolchain. |
| Compiled application | `node tests/application.browser.mjs`: routes, deep reloads, compatibility, early claim capture, explicit demo, focus across heartbeats, stale confirmation, Stop, embedded build revision, themes, keyboard, 1440/390/320 px and 200% text. In-app Browser inspection also checked Settings and Connections. |
| Accounts | `npm run test:accounts:fixture`: isolated real HTTPS backend, controlled private owner delivery, virtual Chrome WebAuthn authenticator and real P256 verifier, naming, invitations/replay, Viewer denial, logout/sign-in and a held real logout-cookie barrier. Providers are excluded; normal shutdown removes temporary databases/keys. |
| Director and Archive | `node tests/director.browser.mjs`, `node scripts/archive-show.browser.mjs`: two operators, revisions/TTL, scoped grants, urgent/lost/global Stop, private prophecy, guest duel privacy, chronicle zero/tie/withdrawn states, keyboard/mobile and actual 68-byte partial PNG download. API fixtures are synthetic. |
| Media and native UI | `node tests/workshop-media.browser.mjs`: actual hashed worklet under production CSP, two PCM frames, one photo, software drain and zero CSP/page/external errors. `node tests/debug.browser.mjs` checks the compiled native-bridge fixture, engines, consent, memory/admin and layouts. These are synthetic endpoints/devices, not physical media acceptance. |
| Backend | Actual .NET 10 build with zero warnings and default, `--identity`, `--show`, `--show-live-boundary`, `--twin`, `--twin-media` suites. Identity includes real P256 verification, migrations/backups/recovery, last-owner concurrency and object authorization; Show integration adds 24 HTTP authorization assertions. Backend sources and retained binaries are unchanged through the later native/UI fixes. |
| Portable firmware | Genuine MSVC host rebuild: 18 host and 21 media tests. `python -B kufr/firmware/tests/run_show_tests.py`: 20 checks and 55 actual serialized messages; offline bank 3/3. No ESP32 was connected or flashed. |
| Contracts and viewer | 37 valid/45 invalid corpus cases and 2479 browser/.NET differential checks; retained WebGL math, lifecycle, twin/media/timeline, source isolation and stale-state regressions. Standalone `file://` viewer ran at desktop/mobile widths with networking disabled and zero page errors. |
| Release and design | Independent 55-file compiled-site assembly, generated tokens/design integrity, compiled DV10 CSP and 37 references; export/certification, bounded ZIP binding, atomic publication/rollback and deployment regressions. Windows skips Linux symlink activation. No local CAD geometry generation was claimed. |

Focused identity regressions cover exact native cancellation IDs, listener cleanup,
pre-aborted requests, delayed Blob conversion, current 401 versus context changes,
logout/sign-in ordering and preserving an account during passkey ceremonies.
Local contact time comes only from validated snapshots/correlated heartbeats,
is isolated per device, and never derives from pairing/demo/connect attempts.
Workshop/account changes dispose private controllers and fence late callbacks;
inspection and replay never acquire actuator authority.

## CI and review evidence

All current checks and the final merge gate are reported on PR #218. A dated
successful macOS software execution for application commit `97394db` is
[job 117940550139](https://github.com/Blato58/DildoVander/actions/runs/39216095203/job/117940550139):
pinned LAN, Debug/continuous voice, BLE secret/status policy, account/guest/public
origin policy, actual production scheduler/URLSession tests and unsigned Simulator
build. It includes cold anonymous CSRF sign-in/enrollment POSTs, cookie isolation,
queued zero-HTTP cancellation, admission races, priority Stop and genuine 401.
Those API/URLProtocol fixtures do not exercise an actual iOS authenticator.

The earlier [job 117928062001](https://github.com/Blato58/DildoVander/actions/runs/39212241042/job/117928062001)
compiled but failed a cookie assertion before the Simulator build. Its custom
URLProtocol had not modeled ordinary cookie installation. The correction adds
actual HTTPCookie parsing/storage with positive preconditions and retains strict
before/after priority-isolation assertions; subsequent macOS execution passed.
This remains modeled cookie processing, not a real HTTP/TLS cookie-pipeline test.

The accepted tracking migration changes guidance/CI only. Selector 15 tests,
site-workflow 9, YAML dependency assertions and actionlint pass. A broad local
scripts/ci run had 77 passes, 16 skips and one Windows POSIX-mode assertion
(`test_package.py:106`, expected 0755 versus 0666); the unchanged packaging suite
then passed in [Linux job 117944706728](https://github.com/Blato58/DildoVander/actions/runs/39217479360/job/117944706728).
The latest combined head still requires its full CI/review result before merge.

Independent combined source reviews found no remaining actionable defects after
the recorded fixes. GitHub reviews, inline threads and current Codex status must
be read again immediately before merge. Historical successful jobs do not replace
the current-head aggregate, including the actual CAD/manufacturing verification.

## Remaining acceptance boundaries

- Signed iPhone: actual Associated Domains/passkeys, Keychain upgrade/resume and
  user switching, native media permissions, physical BLE possession/reconnect and
  LAN identity/pinning. Unsigned builds and browser fixtures do not prove these.
- Hardware: actual ESP32 effects, measured servo positions, acoustics, assembly,
  power and physical safety. Portable tests are software evidence only.
- Production: real HTTPS/AASA, nginx syntax/health, persistent-store permissions,
  private pipeline migration and the first legacy-to-atomic cutover/rollback pair.
  No production activation or credential rotation was performed.
- No paid provider inference, signing, firmware flashing or physical actuation was
  performed. Bound offline ZIP publication is rejected; maintained standalone HTML
  composition remains available. Archive retention is still manual-only.
- Automatic approval review rejected cleanup of one interrupted synthetic Temp
  directory, including a bounded file attempt, with reason `blocked by policy`.
  It was retained; later fixtures cleaned up normally. No bypass was attempted.
