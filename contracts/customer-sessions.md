# Account-owned customer conversations, protocol v1

The maintained Vue/Capacitor application offers `/me/` for the account's own
character, conversations and keepsakes. Staff retain their exact device/resource
permissions. Customer passkey enrollment never assigns staff roles or grants.

## Start and identity

1. A trusted device registers `POST /v1/customer-sessions/device-challenge` using
   its existing device bearer token. The v1 body contains `bootId`, a random
   `challenge`, `generation`, `catalogVersion`, and decimal-string
   `operationEpoch`. Registration alone does not open the customer service.
2. An authorized device manager enables customer admission through
   `PUT /app/v1/customer/admin/devices/{deviceId}` with `{ "enabled": true }`.
3. An explicitly initiated native BLE connection reads the registered challenge.
   Advertising names and CoreBluetooth peripheral handles are discovery aids.
   The backend, not radio signal strength, verifies the device identity.
4. The signed-in account posts `/app/v1/customer/start-tickets` with v1,
   `operationId`, device/boot/challenge, and its own `profileId/profileRevision`.
   `openAiConsent: true` is required after the unchecked disclosure naming
   OpenAI, voice audio/transcripts and character context for spoken replies.
   `personalMemoryConsent` remains a separate optional choice and defaults to
   false. Ownership is server-derived. Missing consent returns
   `409 openai_consent_required` without issuing a ticket; legacy tickets
   without recorded consent cannot be redeemed.
5. The phone sends the compact [BLE message](customer-ble-v1.schema.json).
   The trusted device redeems it at `POST /v1/customer-sessions/redeem` using
   `{protocolVersion:1,ticket,operationId,bootId,challenge}`. Redemption captures
   immutable ownership and context in the operation journal before voice starts.
6. The reply contains the existing `conversationId`, `expiresAt`, `limits`, and
   `customerSessionId`. Both production voice engines consume the same bounded
   character projection before their first provider response. The device reports
   `context_ready` separately from an actual provider `running` acknowledgement.

Tickets are random 256-bit values, hash-indexed and protected for exact issuance
replay. They expire after 90 seconds; registered challenges expire after 120
seconds. Expiry prevents first use, not retrieval of an accepted operation.
Neither bearer tokens, service PoP, profiles nor provider keys travel over BLE.
The customer transport has a 512-byte frame ceiling. CoreBluetooth uses long
GATT writes rather than truncating a frame to an arbitrary MTU; incomplete or
oversized replies fail closed. Customer and provisioning transports share one
lifecycle and never expose service controls through the public endpoint.

## Arbitration and recovery

All conversation creation uses one durable per-device SQLite lease, including
legacy/admin starts. Customer ticket redemption reserves that lease atomically;
the firmware additionally admits work through the existing physical owner.
Identical retries retain the operation and conversation. An uncertain response,
phone disconnect or phone timeout never grants the device to another guest.
The account's session list includes `expiredOperations` only for its expired,
never-redeemed tickets. This authoritative evidence permits a fresh explicit
start after a rejected admission; accepted operations are never in that list.

After backend restart, active customer sessions become `recovering`; providers
are never replayed automatically. Device `GET /v1/customer-sessions/device`
returns admission mode, original operation/session/conversation and end request.
The firmware may reconcile an uncertain start with a terminal report. A newly
booted, physically idle owner can assert `POST /v1/customer-sessions/device-idle`
with its currently registered boot. This retires only older-boot leases.

`POST /v1/customer-sessions/{id}/device-state` is device-authenticated and bound
to the current boot. Only a terminal physical-runtime acknowledgement releases
the customer lease. Account `POST /app/v1/customer/sessions/{id}/end` requests
termination of that exact owned session. BLE `end` is only a poll/wake hint; it
does not grant global Stop. Operator Stop retains priority. Locking or losing
the phone after accepted start does not stop the independent suitcase.

## Phone refresh and interruption recovery

Customer and operator views refresh the existing account-authorized sessions.
Active sessions are polled every 2.5 seconds. A transient read failure marks the
display stale and retries with delays increasing from one second to a maximum
of 30 seconds; it never creates a replacement conversation. A restored session
can be refreshed without a locally pending start operation or a BLE connection.

Backgrounding the page cancels its refresh work. Foreground, network reconnect
and the manual refresh action request authoritative state again. An account
change or unmount aborts obsolete reads and fences late responses. Account
changes clear the previous account's private state before loading the new one.

An uncertain start retains its original operation and ticket request. Only a
definitive server rejection or an authoritative expired-operation result allows
that identity to be retired; phone timeouts do not release a device lease.
Owners may request an end while a session is recovering. The display keeps the
request pending until runtime acknowledgement rather than presenting the HTTP
acceptance or BLE disconnect as physical completion. Operator Stop remains a
separate, higher-priority control.

Known Bluetooth, permission and network failures have actionable messages and
allowlisted diagnostic codes. Unknown native error strings are not displayed.
The component/browser fixtures and recovered-journal tests verify software
reconciliation; they do not prove iPhone background behavior or physical Stop.

## Private data, jobs and outputs

Character input and enrichment live in the existing account data root's additive
`customer-characters.sqlite` store, using transactional revisions and durable
leased jobs. The journal owns tickets, session snapshots and device arbitration.
Cross-store deletion records a durable intent in the same transaction as the
profile removal. The hosted worker and session routes replay journal revocation
and live-context invalidation before acknowledging that intent. Permanent
profile-store and journal tombstones also block provider startup during a failed
replay; late work cannot recreate deleted data. Ordinary profile edits preserve
active snapshots. Explicit new-character selection creates a fresh profile
identity and personal-memory scope while retaining earlier conversations.

The stable guest identity derives from account plus profile identity, never a
character name. Current profile use does not authorize long-term personal memory
or cloud indexing. Shared character facts, if cached in a later version, must
exclude customer descriptions and corrections. This version does not share an
enrichment cache between accounts.

A confirmed physical outcome captures its original customer account/session in
the archive's private ownership table before generation. Existing account
associations and epoch checks cover lists, details and assets. Ownership metadata
never enters the public story snapshot or generation prompt. Public galleries,
static QR pickup, old claims and manual-only archive retention remain in place.

## Compatibility, rollback and acceptance

Customer admission defaults off. It requires backend customer-session protocol1
and firmware advertising customerStart1; service-provisioning-only firmware is
not compatible. Disabling admission requests the current customer's end but does
not erase their profile, session or archive. A rollback must first disable
admission and reconcile active devices; retain the additive databases/tables and
data-protection keys. Older servers do not understand customer leases, so do not
serve legacy starts from an older binary while customer sessions remain active.

Offline checks use `--customer-profiles`, `--customer-sessions`, contract
fixtures, Vue tests/browser fixtures, native Foundation tests and the firmware
host/ESP-IDF build. These are not paid-provider or physical-device acceptance.

Real-device acceptance remains explicit: register on an installed iPhone → save
character → connect to the selected ESP32 → start → hear the correct first
personalized line → lock the phone → finish → see the keepsake on the same
account on another device. Repeat with two phones racing, disconnect during an
uncertain start, restart, profile deletion during a job, and an attempted service
command. Verify actual Wi-Fi/audio/BLE coexistence and Stop on the prototype.
