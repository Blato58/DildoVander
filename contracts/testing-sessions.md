# Account-owned virtual testing execution

This is additive to [customer sessions](customer-sessions.md). Physical start,
BLE PoP, device credentials, production leases and guest authorization retain
their existing contracts. An account cookie never becomes a physical credential.

`/app/v1/testing-sessions` requires an authenticated, current **Tester** role.
All mutations retain account CSRF/origin protections. Resource owner is derived
from the authenticated principal; no caller-provided owner or preview role grants
production authority. Unowned resources fail without exposing private content.
Owner/Administrator are not implicit test identities.

Session IDs are server-generated 32-character hexadecimal identifiers; the
virtual hardware identity is exactly `test-<session-id>`. Creation uses a stable
client operation UUID and validates engine, input mode, OpenAI consent and saved
content consent. Retry with changed operation parameters conflicts. Supported
engines remain `gpt-live-1` and `gpt-realtime-2.1`, never an implicit substitute.

Sessions and characters persist in separate test stores. Selection freezes an
immutable character snapshot. Provider operations revalidate scope and operation
state. Realtime tickets retain the existing one-use, short-lived, origin/account
session/security-stamp constraints; reconnect uses actual retained history.
Client generations/AbortSignal reject stale work after account/session changes.

Saving a test character with OpenAI consent durably queues enrichment in the
isolated character store. Name and world are sufficient; description/version are
optional. The enrich endpoint returns promptly and ensures queued/running work
without duplicating it. Character reads expose the current background status and
result. Clients select the latest profile before beginning a conversation; later
enrichment does not change an already started conversation's snapshot.

After a failed text request the client reads the saved conversation, since the
server may have persisted the user turn before provider failure. It never replays
the mutation on refresh, and blocks a new HTTP turn while conversationActive is
reported by the server.

The existing Lab session gets a server-only Testing context and bridge. The
public Lab session request cannot supply that context. Managed session end and
delete use the Testing API, not the ordinary Lab lifecycle. Production publication,
hardware tickets and script suites do not operate on managed Testing sessions.

Only a validated real backend decision may enter the native hardware port. Native
proposal admission and terminal simulation completion are separate evidence.
Terminal completion requires the exact admitted operation, released compartment,
idle runtime and no recovery requirement. Lost process state is unknown/failed,
not confirmed completion. No virtual event is physical-position evidence.

Private assets are served only beneath the exact authorized testing session and
keepsake IDs, with no-store caching. Generated media, current/evening/returning
memory and duel results use the existing domain services under the isolated test
root. Production vector indexes/gallery targets/device credentials are excluded.
Explicit session/character/account deletion is scoped and fenced against late
work; no production data root is a deletion target.

See [the runbook and feature matrix](../docs/testing-sessions.md) for routes,
provider/modality limitations, build packaging, offline tests and separately
opt-in paid acceptance. Browser CI uses controlled provider doubles and virtual
hardware; neither constitutes real OpenAI or physical acceptance.
