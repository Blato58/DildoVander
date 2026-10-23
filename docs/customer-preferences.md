# Guest language and conversation preferences

The shared web/iOS application offers Czech and English interface text. The
header selector affects presentation only. Signed-out visitors have a separate
device preference; each signed-in account has its own preference on that device.
Switching accounts reads that account's choice instead of copying the previous
visitor's language. If local storage is unavailable, the selector explicitly
describes the choice as temporary. It does not claim cross-device synchronization.

Conversation language is a separate, server-owned preference. Under **My account**,
a guest with a character can select Czech (`cs-CZ`) or English (`en-US`) and a
gentle, cheeky, or adult roast style. Editing the form does not send a mutation;
the guest saves the choices explicitly. The interface language does not change
the saved conversation language.

## Account and session boundaries

`GET /app/v1/customer/conversation-preferences` reads the current account's
character-bound settings. `PUT` accepts strict JSON up to 2048 bytes with:

- `schemaVersion: 1`, `profileId`, and `profileRevision`;
- `expectedPreferenceRevision` for optimistic concurrency;
- `conversationLanguage` and `tone` from the supported choices.

The backend owns account authorization, CSRF/origin checks, character ownership,
revision checks, deletion fences, and persistence. A conflict requires a fresh
read before another save. The UI aborts stale requests and clears settings when
the account or character changes; late responses cannot refill another account's
form. The native bridge permits only the exact GET/PUT route and bounds the
account transport's PUT body to 2048 bytes.

New start tickets capture a trusted, immutable preference snapshot. Changing
preferences affects the next new conversation; accepted tickets, retries,
recovery, and existing conversations retain their captured context. Character
reference metadata is not a trusted conversation preference. The guest's tone
can lower the host's ceiling and cannot bypass host policy, an ongoing opt-out,
or the separate explicit AI and personal-memory consents. Ongoing no-swearing and
roast opt-outs belong to the conversation and remain in effect when a provider
call fails and a new call continues that conversation.

Account export includes saved conversation preferences with their character
binding. Technical export expiry does not delete character, keepsake, or memory
content. Account deletion and manual-only retention keep their existing meaning.

## Verification

The focused frontend tests cover malformed responses, profile binding, independent
interface language, explicit save, concurrency conflicts, and late responses after
an account switch. The compiled application browser fixture exercises persistence,
both themes, narrow screens, and enlarged English text using synthetic APIs.
The backend's `--customer-preferences` tests exercise authenticated HTTPS behavior;
`--voice-providers` and `--conversation-engines` cover language request shapes and
provider lifecycle behavior. Run the native identity harness on macOS to verify
the route/method/body boundaries.

These checks do not establish signed iPhone installation, actual voice quality,
BLE proximity, physical operation, production migration, or App Store acceptance.
