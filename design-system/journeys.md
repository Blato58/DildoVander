# Active journeys

This is the interaction contract for the existing Vapour Chrome consumers.
Use the current implementation and feature state as truth; mockups do not add
routes or capabilities. Keep Czech action names literal and guest stories playful.

## Operator

The shared Vue web/Capacitor application starts at **Domov**, with actual
connection readiness and the current public evening. **Kufr** (`/suitcase`)
owns live voice, text, dance and deliberate manual operation. **Připojení**
(`/connections`) owns local pairing, network preparation and the independent
account/device association. **Diagnostika** (`/suitcase/diagnostics`) groups
native phone tests and explicit local USB service. **Dílna**, **Archiv** and
**Nastavení** share the same persistent shell. Backend phone test conversations
remain distinct from a guest conversation owned by the ESP32. Opening a view
never starts a session, AI work, USB connection or hardware lease.

Account sign-in uses passkeys, with explicit reauthentication for sensitive
administration. Account roles/grants, guest capability links and physical BLE
proof/pinning remain separate. User changes clear active user control and private
derived views; independent Keychain hardware pairing is retained for explicit
reconnection. Linking a guest keepsake to an account is an explicit owner action
bound to that account and access epoch; a public/share link cannot do it.

Keep Stop available across navigation. Changing a tab preserves drafts and
ongoing operations; it does not cancel them. In-flight physical commands are
never replayed after reconnect. Refresh authoritative status before enabling
actions; an acknowledgement is not measured movement. Unknown connection,
uncalibrated state and absence of a manual lease are separate conditions.

The iOS installation entry distinguishes an unavailable signed build from an
available download. Safari installation is a platform handoff; the page cannot
report successful installation or suitcase readiness. Keep registration/signing
recovery secondary and provide a route back to the project or web simulation.

## Visitor pickup

The public evening starts with a wand index, then the selected result, artwork,
downloads and story. Keep the selected identity visible and follow the latest
result only when the visitor deliberately chooses it. Public retrieval is
read-only; a partial export failure does not hide a usable story or image.

The private portal binds every request, continuation and download to its
originating route and access context. Route change or hiding private content
invalidates that context. Abort is advisory; stale results must still be ignored.
Returning reloads server truth. A request already accepted by the server can
finish there; leaving the page does not promise cancellation. Portrait consent,
owner authorization and static public pickup remain separate. The archive is
manual-deletion-only; access expiry never deletes a keepsake.

## Model, assembly and live observation

The model remains the main workspace. Show current mode, camera orientation,
selection and visibility recovery without requiring an open inspector. Assembly
context comes from the actual guide: current step, working location, orientation
and relevant parts stay visible when detailed instructions are collapsed.
Restoring visibility preserves the current step and safety state. Provide
buttons and keyboard alternatives to touch orbit, pan, pinch and selection.

Simulation, a static preview and live observer telemetry are explicitly named.
Show the source and last received message; describe disconnected values as
historical. Silence is not a disconnect when the protocol has no heartbeat.
A new observer connection starts a new local timeline. The observer never
acquires a manual lease or issues actuator commands.

The USB servo service panel is real hardware. Connection is separate from a
valid Stop acknowledgement and from a one-use Arm. Readiness must derive from
the existing controller gates. Calibration commands and readback do not prove
physical position; recorded limits still require the operator's inspection.

## Shared feedback and verification

Each persistent interruption says what happened, what remains usable or saved,
and the next supported action. Use `notice` with `detail`, trusted `action` and
`live: false` for snapshot-driven readiness. Announce meaningful transitions
separately. Retry and cancel appear only where the feature implements them.
Keep essential state understandable without color, touch targets at least 44px,
focus visible, and primary actions clear at 320/390px as well as desktop widths.

Verify real consumers and offline/generated outputs through their owning paths.
Synthetic browser results establish software behavior, not signed iPhone,
microphone/camera quality, deployed service behavior or physical acceptance.
