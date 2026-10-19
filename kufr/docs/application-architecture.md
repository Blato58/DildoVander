# One DildoVander application

Repository architecture, 2026-10-19. Acceptance and environment limits are
recorded in `docs/project/work-items/KUFR-APPLICATION.json`; never infer signed
device or production acceptance from this document.

The application source is `kufr/mobilni-aplikace`: Vue 3 SFCs, TypeScript,
Vite, Vue Router, and the existing Capacitor Swift adapters. Hosted and iOS
builds use the same components. The .NET backend remains the authority for
accounts, resource grants, archives and operations. Device connection classes
remain the authority for local snapshots, lease and command reconciliation.

## Ownership and integration contract

- `src/application` owns the router, shell, notifications and theme preferences.
- `src/components` owns typed Vue presentation primitives using Vapour Chrome.
- `src/features/suitcase/SuitcaseView.vue`, `ConnectionsView.vue`, and
  `DiagnosticsView.vue` own device journeys and existing native adapters.
- `src/features/identity/SignInView.vue`, `SettingsView.vue`, and `AdminView.vue`
  own account UX. Identity endpoints live under `/app/v1/`; browser cookies
  never substitute for device or guest capability credentials. API errors use
  ProblemDetails. Account mutation requests require antiforgery protection.
- `src/features/workshop/WorkshopView.vue` owns model inspection, assembly,
  simulation, observation and replay. GPU state stays outside deep reactivity.
- `src/features/archive/ArchiveView.vue` and `KeepsakeView.vue` own public and
  private guest journeys. Fragment claims are captured and removed before
  application assets or router navigation can disclose them.
- Feature views have no shell markup. They use ordinary router links and share
  the application shell. Each feature owns cancellation and disposes listeners.
- The identity service owns the typed account API. Native transports restrict
  paths, methods, headers, cookies and byte limits; they are never arbitrary
  authenticated proxies. The account, guest and local hardware stores remain
  independent. Ordinary route transitions do not cancel active device operations.

## Routes and compatibility

| Existing entry | Application destination | Access |
| --- | --- | --- |
| `/`, public introduction | `/` | public, contextual home |
| Companion, `/app/` | `/suitcase` | explicit target; local pairing gates |
| discovery and provisioning | `/connections` | installation and account scoped |
| Debug panels | `/suitcase/diagnostics` | capability and role checked |
| Workshop viewer | `/workshop` | public model, explicit live authorization |
| simulator observer | `/workshop?mode=simulation` | isolated simulator target |
| local USB servo service | `/suitcase/diagnostics` | explicit local Web Serial; no network proxy |
| `/keepsake/evening/` | `/archive` | public evening, never owner data |
| `/keepsake/` | `/keepsake/` | scoped guest claim/session |
| account and administration | `/settings`, `/settings/admin` | server enforced |
| installer manifests, downloads | existing distribution URLs | distribution exception |

Do not redirect API mutations, guest exchanges, native callbacks or downloads.
No route mount may start AI work, obtain a control lease or issue an actuator
command. A mode change clears pending intent and requires explicit target choice.
Diagnostics carries the saved suitcase ID into `/workshop?mode=live&source=…`.
Only an exact returned authorized source can be selected; listing and observation
remain explicit. Settings manages evenings for an exact granted device through
the existing evening API, with confirmation and stable idempotent retries.

Conversation controls consume the canonical two-engine capabilities described
in `autonomous-live-memory.md`: GPT Live is primary and Realtime 2.1 is an
explicit alternative. Retired text/STT/PTT modes are never restored by this UI;
labelled deterministic simulation text remains separate. Native phone audio
uses the maintained authenticated WebSocket. Account admission requires device
operate authority and CSRF; the stream revalidates the persisted session, current
role and exact grant before further audio input.

Canonical conversation memory is distinct from the keepsake account association.
Its UI displays actual identity verification and separate long-term/cloud consent
flags. Private inspection and confirmed deletion retain administrator authority,
recent passkey authentication, CSRF and generation/epoch fences. Route changes
and user switching clear those private views without deleting the archive.

## Releases

ModelBundle schema and hashes identify immutable model data independently of
the application Git revision. CAD generation produces data and manufacturing
artifacts. Application build consumes optional published data without Python
CAD imports or manufacturing verification. Offline packaging composes the same
maintained viewer with an explicitly selected bundle. Unavailable models leave
other application routes usable. Fixture models must be visibly labelled.

## Identity and rollout

Use maintained ASP.NET Core Identity passkey support, persistent SQLite account
storage, protected server sessions, explicit HTTPS relying-party/origin settings,
controlled expiring bootstrap and single-use invitations/recovery. Preserve guest
capabilities and firmware credentials. Account grants do not prove physical
possession; native BLE PoP, pinning and Keychain remain required for enrollment.
Native authentication must report unsupported requirements truthfully until its
maintained bridge and associated-domain requirements are verified.

## Development and verification

From the repository root, install contract dependencies with
`npm ci --prefix kufr/contracts` and application dependencies with
`npm ci --prefix kufr/mobilni-aplikace`. Start `npm run dev --prefix
kufr/mobilni-aplikace` (loopback only). Run application `npm test`,
`npm run build` and `npm run test:browser` in that directory. The build checks
the generated standalone schema validator before Vue typechecking; strict CSP
requires no runtime JavaScript code generation. TypeScript 6.0.3 is pinned for
the installed Vue language tooling; unrelated projects retain their versions.

The hosted builder is `python kufr/web/build-site.py --web-dist
kufr/mobilni-aplikace/dist --destination <new-output-directory>`. It reads
compiled assets only. Independent model/offline commands and integrity contracts
are in `kufr/docs/model-bundle.md`; publication and
rollback preparation is in `kufr/web/deploy/APPLICATION-RELEASES.md`.

For the real passkey browser journey, first build the backend test project and
the application. Set `KUFR_TESTS_DLL` to the resulting `BackendTests.dll`, then
run `npm run test:accounts:fixture --prefix kufr/mobilni-aplikace`. This starts
only the isolated HTTPS fixture: inherited service settings are cleared, no AI
providers are registered, and accounts/keys live in temporary storage. Chrome's
virtual authenticator exercises the real WebAuthn verifier. The launcher delivers
the enrollment link privately and shuts down the fixture through its stdin
control path so temporary data is removed. This proves browser behavior, not
signed native authentication or real authenticator hardware.

Generated tokens remain checked by `scripts/generate_design_tokens.py --check`.
`scripts/check_design_system.py` scans Vue styles as well as compatibility HTML
and CSS. `scripts/check_dv10.py --web-dist kufr/mobilni-aplikace/dist` verifies
early claim capture and the exact compiled CSP script hash; historical DV10
source evidence is explicitly separate. Model fixtures and synthetic API tests
never become production verification or physical-position evidence.

No production deployment, credential rotation, paid provider calls, flashing
or physical actuation is authorized by this implementation task.
