# Shared application implementation

Source: `mobilni-aplikace/src`. `application/App.vue` owns the persistent
shell and urgent Stop, `router.ts` lazy routes, `preferences.ts` the Pinia theme
preference. Feature views own data, cancellation and local drafts. Prefer native
semantic controls and shared `AwButton`, `AwField`, `AwNotice`, `AwDialog` and
`AwStatus`; appearance comes from canonical `--aw-*` tokens. No provider secrets
or long-lived account bearer tokens belong in browser storage.

The account service owns server sessions; native identity uses its own supported
platform bridge and Keychain. Account permission, physical phone pairing, local
connection and manual lease remain separate. User switching clears active user
grants/control and private displays without erasing independent hardware pairing.
Never replay a non-idempotent command on reconnect. Never gate an already
permitted Stop on a login, confirmation dialog or a queued ordinary request.

Guest fragments are captured in `index.html` before module imports and removed
from history; `application/guestClaim.ts` transfers them once in memory. Guest
claim/session/owner permissions do not become an account or hardware grant.
Private views require noindex and no-store. Guard asynchronous results by the
originating route/access generation even when transport ignores abort.

Workshop owns one canvas lifecycle per mounted view. Dispose GPU resources,
observers, event handlers and sockets. Model inspection, simulation, live and
replay show their mode and target; selecting a route or model cannot obtain
a hardware lease. Mapping compatibility and freshness precede live animation.
The ModelBundle contract and offline commands live beside the Workshop feature.
Never import CAD Python into an application release or recertify manufacturing
archives for a UI change.

Use `npm test`, `npm run typecheck`, `npm run build` and
`npm run test:browser` in the app. The browser suite uses synthetic endpoints
and viewports for repeatable routing/focus/privacy checks; it proves neither
signed iPhone authentication nor physical actuation. Native changes require the
macOS workflow. `scripts/ci/ui_boundary.py` prevents UI workflows from geometry
generation/verification. Run full CAD checks only for actual model inputs.
