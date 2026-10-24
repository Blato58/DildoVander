# Hardware-free Testing Sessions

Testing Sessions is part of the shared Vue/Capacitor application at
`/testing-sessions`. It requires the explicit **Tester** role. An Owner can issue
an ordinary single-use Tester invitation in account administration; enrollment,
passkeys, persistent sessions and account deletion use the existing identity
implementation. Owner/Administrator/Operator roles do not imply Tester.

## Architecture assessment and integration

The existing Persona Lab already called the real Live and Realtime adapters for
text and voice, with useful provider evidence and socket cleanup. It intentionally
used synthetic memory and tool results, so it was not a complete customer test.
Customer profiles/enrichment, generated wands, keepsakes and duels already had
production implementations, but customer admission required an ESP32 BLE ticket
and a device lease. The portable C++ digital twin reused the firmware state
machines, but needed an externally configured process and its deterministic TEXT
command manufactured a proposal. None of those boundaries should be removed.

The new feature composes those existing implementations under a separate
server-controlled execution scope:

- `TestingSessionService` owns account/session/character authorization, lifecycle,
  immutable selected character snapshots, consent and virtual outcomes.
- `TestingResources` instantiates the existing character, wand, decision, local
  memory, keepsake and duel services with separate databases and asset roots under
  `<Backend:DatabasePath>.testing`. Production device credentials, public gallery
  targets and vector-store IDs are excluded. No production grant is minted.
- `LabSession.Testing` is attached only by server composition, never accepted from
  a Lab create request. `ITestingConversationBridge` supplies the real isolated
  context and domain delegation to the existing provider transports. Ordinary
  Persona Lab tests retain their explicitly synthetic tools and fixtures.
- `TestingHardwareAdapter` starts the bundled native twin automatically, using
  an exact `test-<session-id>` identity and private journal. An accepted backend
  decision is delivered to the native `ACCEPTED_PROPOSAL` port. It enters the
  existing PendingProposal, WandPresentation, PhysicalEffects, safety and journal
  owners; it supplies no synthetic dialogue. Physical output ports remain virtual.
  The legacy synthetic TEXT test port is unchanged.
- A successful proposal admission is not completion. An exact operation must
  reach native idle, release its compartment, and have no recovery requirement
  before the service issues a real isolated keepsake. Interrupted processes
  retain an unknown/failed outcome and require recovery, never invented success.

The ordinary customer BLE/device contracts and production account roles are
unchanged. Test virtual identities are not added to DeviceAccessStore and cannot
redeem physical-device credentials or leases. Raw Lab end/delete is rejected for
managed Testing conversations, preventing orphaned test resources.

## Journey and modalities

The guided screen has four steps: **Sezení → Postava → Rozhovor → Výsledek**.
Sign in as Tester, accept the explicit OpenAI/content-persistence consent and
create a session. Enter a character's name and world; version and custom context
are optional. Saving with consent automatically queues the same character lookup
used by the customer journey. The background worker persists its result separately
from the HTTP request, and the screen refreshes its status. Before the first turn,
continuing from Postava selects the latest profile as the conversation snapshot.
Failed enrichment can be retried explicitly or the saved input can be used alone.
Session creation does not claim an OpenAI handshake. Saved sessions can be selected
in Sezení after refresh or application restart; the URL retains the session identity.

Text is the default. It calls the real backend/Live or Realtime provider adapter
without creating an AudioContext, opening capture, or requesting microphone
permission. Voice is explicit and reuses the existing LabVoice audio integration.
Hybrid allows typed turns on that voice transport. Text/voice reconnection uses
actual persisted turns and the same character and isolated memory context.
Provider-session continuity is preserved within an active supported socket;
changing transport reconstructs from the canonical retained history, not a
fabricated transcript. Technical provider-session deadlines remain in effect.

Assistant playback can be muted independently. Text input with spoken output
opens the existing output socket without capture or microphone permission; each
typed turn streams actual provider PCM through the same engine adapter. The
ordinary HTTP text path remains audio-free. Live does not have an invented
incremental typed-input event: a new provider turn reconstructs the actual saved
context. No unrelated TTS engine or silent model substitution is added. GPT Live photo input
remains unsupported; the existing consented Realtime image path is available.
A separate consented uploaded image can replace the camera for private portrait
generation. Uploads are prepared/bounded, never represented as new AI outputs.

A character may have multiple saved revisions; an active session retains the
selected snapshot. Enrichment is authorized by saving with OpenAI consent; retries
and wand/media generation are explicit provider operations. Six positions are
physical slots: generated identities/lore/art and
combat abilities come from the existing per-character generation contracts.

## Feature coverage matrix

Classification describes implemented paths, not live-provider or physical
acceptance. Real services still require their normal backend configuration and
consent; unavailable providers produce errors.

| User-facing feature | Classification | Testing path / evidence boundary |
| --- | --- | --- |
| Sign-in, invitations, passkeys, account sessions | Fully functional using real services | Existing Identity; Tester invitation; no admin elevation |
| Own account settings and deletion | Fully functional using real services | Own passkeys/settings; deletion saga also removes test resources |
| Multiple character profiles and edits | Fully functional using real services | Test-owned persisted profiles; active snapshot frozen |
| OpenAI character enrichment/corrections | Fully functional using real services | Existing enrichment adapter and consent/revision fences |
| Multi-turn typed conversation, persona and follow-ups | Fully functional using real services | Existing Live/Realtime adapters; actual provider errors and usage |
| Microphone voice, interruption, playback and reconnect | Functional with virtual hardware | Existing browser/native audio transport; no ESP32 prerequisite |
| Text/voice/hybrid continuity | Fully functional using real services | Canonical saved history/context; reconnect may create new provider session |
| Independent assistant audio output | Fully functional using real services | Explicit microphone-free spoken text socket or mute supported voice/hybrid PCM |
| Dynamic wand candidates, lore, selection and abilities | Fully functional using real services | Existing generation + narrow decision service; accepted slot only |
| Six compartments, servos, LEDs, vibration, presentation/retraction | Functional with virtual hardware | Native production state machines and accepted output writes |
| Connection, readiness, Stop, faults, recovery | Functional with virtual hardware | Exact virtual session only; simulated inspection and native journal |
| Camera replacement / Realtime vision | Functional with virtual hardware | Consented image upload; Live vision remains unavailable |
| Canonical wand artwork and keepsake story | Fully functional using real services | Existing provider jobs/assets, explicit generation; no preview substituted |
| Private AI portrait | Fully functional using real services | Consented photo + existing portrait provider and stored output |
| Personal test history, memory and archive | Fully functional using real services | Separate persistent stores; exact owner/session asset authorization |
| Current/evening/returning character local memory | Fully functional using real services | Existing HybridMemory/EveningMemory; account-isolated evening and consented character binding |
| Production cloud vector indexes | Read-only/inspectable configuration boundary | Deliberately excluded; no test content written to production indexes |
| Two-character combat, abilities, rounds and outcomes | Fully functional using real services | Existing DuelStore/rules, both test-owned participants, durable outcomes |
| AI duel commentary | Fully functional using real services | Explicit real provider commentary; spoken device narration unavailable |
| Duel physical effects | Functional with virtual hardware | Accepted domain round drives native virtual effect acknowledgment |
| Persona Lab scenarios, comparisons, evaluations and diagnostics | Fully functional using real services | Existing separate Lab sessions; Testing session remains resumable |
| Test operator management and diagnostics | Functional with virtual hardware | Own lifecycle, fault injection, mode, hardware and provider evidence |
| Production users, grants, device provisioning, global settings | Read-only/inspectable product contracts | Tester denied by production server authorization; no test role preview elevates identity |
| Tonight / Director production show operation | Read-only/inspectable product contracts | Production controls remain separately authorized; Testing exposes scoped session/duel effects |
| Public evening gallery/QR, publication and sharing | Read-only/inspectable | Public product remains usable; test content stays private and is not published into production |
| Workshop/CAD/assembly and existing simulation controls | Read-only/inspectable | Existing viewer, with its own simulation evidence; no CAD regeneration needed |
| Static demo, support, privacy and installer | Fully functional using existing application | Existing routes retained; demo remains clearly separate from real testing |
| Physical BLE pairing/PoP, GPIO, power cut, real sensors/audio acoustics | Genuinely hardware-only | Cannot establish with a virtual adapter or browser test |
| Physical calibration, load, jamming and measured position | Genuinely hardware-only | Simulated commands/positions are not physical acceptance |
| Retired Responses dialogue / STT-dialog-TTS / Realtime PTT | Not implemented in current product | Retired endpoints remain retired; no alternate conversation engine introduced |

## Diagnostics and failure testing

The dashboard separates real backend connectivity, configured provider availability,
confirmed provider handshake, actual response/tool/usage records and explicitly
virtual hardware events. Configuration or a connected WebSocket alone is not a
confirmed OpenAI session. Only provider acknowledgments and responses establish
those states. Generated assets are loaded through the exact account-owned test
resource route, not through public gallery URLs.

Stop/cancel remains scoped to the selected test; ending cancels its provider work
and shuts down its native process. Disconnect, servo failure, missing camera and
recovery are deliberate hardware fault controls. Provider/network failures are
reported from actual requests, not converted to synthetic successes. Automated
fixtures can inject provider failures, timeouts, duplicate and concurrent work;
these are controlled test doubles, not live provider behavior. Broad production
fault injection is not exposed.

Diagnostic exports exclude transcript/character/memory/image content and tokens,
provider credentials, bearer authorization and one-use socket tickets. Provider
session IDs and sanitized error codes are technical evidence. Account-owned chat
content stays visible only inside the authenticated journey with explicit consent.

## Build and verification

Linux backend builds compile and bundle the existing portable twin automatically
using Python 3 and a C++14 compiler. The packaged executable lives at
`testing-native/kufr-native-twin`; the tester supplies no IDs, firmware process or
serial/BLE configuration. Windows server development may point the server-only
`TestingSessions:HardwareExecutable` setting at the built native twin. A missing
binary is an explicit hardware-runtime configuration failure, never simulated
readiness. The iOS client talks to the same backend and uses its bounded native
account transport allowlist. Native iPhone acceptance requires macOS/device checks.

Offline focused checks (controlled providers, no OpenAI charges):

```sh
dotnet build backend-tests
dotnet run --project backend-tests --no-build -- --tester-identity
dotnet run --project backend-tests --no-build -- --testing-resources
dotnet run --project backend-tests --no-build -- --testing-conversations
dotnet run --project backend-tests --no-build -- --testing-hardware
dotnet run --project backend-tests --no-build -- --testing-session-http
dotnet run --project backend-tests --no-build -- --testing-sessions
npm test --prefix mobilni-aplikace
npm run build --prefix mobilni-aplikace
node mobilni-aplikace/tests/testing-sessions.browser.mjs
```

## Opt-in real-provider acceptance

No paid run follows from building, CI, or opening the dashboard. Use a separately
authorized backend with securely provisioned existing provider configuration and
a Tester account. Do not paste a key into the browser or diagnostic export.

1. Verify backend API readiness, configured `gpt-live-1`, and bundled virtual
   runtime. Start a text session with recording/OpenAI consent; create and enrich
   a fictional character. Confirm actual enrichment model/status/usage.
2. Send several typed messages with browser microphone permission denied. Confirm
   `provider_session` acknowledgment, actual assistant turns, latency/usage, and
   absence of getUserMedia. Refresh and resume the same conversation identity.
3. Trigger wand selection. Inspect generated candidates, validated decision, native
   admission, terminal `completed_by_timing` simulation evidence, and issued test
   keepsake. Generate real artwork/story and inspect persisted provider outputs.
4. Upload an explicitly consented photo and request a private portrait. Confirm
   actual provider-generated asset bytes and durable readiness; never treat an
   uploaded source/preview as the generated portrait.
5. Prepare a second test character/session, complete its wand journey, then run
   combat and explicitly request AI commentary. Inspect saved outcome and virtual
   effect acknowledgments. Reopen completed sessions and isolated memory.
6. Explicitly try voice/hybrid if a microphone is available, mute playback,
   interrupt and reconnect, checking context continuity. Separately create a new
   session with `gpt-realtime-2.1`; no silent engine fallback is acceptable.
7. Test unavailable model/key, cancellation, virtual disconnect/servo fault,
   recovery and a second account's denial. End and explicitly delete test data.
   Confirm the production gallery/memory/devices were untouched.

Record engine/model, test/session IDs, provider handshake/session ID, sanitized
request/error/usage evidence and generated asset status. Paid quality acceptance,
actual iPhone execution and physical suitcase acceptance are separate outcomes.

## Changed component inventory

| Component | Changes |
| --- | --- |
| `backend/Features/Identity` | Tester role, Lab capability without staff privileges, test-data account-deletion integration |
| `backend/Features/TestingSessions` | Durable scoped sessions/characters, isolated production-service composition, virtual transport, real resources and lifecycle APIs |
| `backend/Features/PersonaLab` | Server-only Testing bridge, canonical context, scoped cancellation and microphone-free real PCM output |
| `backend/Program.cs`, `backend/backend.csproj` | Feature composition and automatic portable native-twin packaging |
| `firmware/simulator/native/main.cpp` | Accepted backend proposal input at the simulation hardware boundary; production ESP32 firmware unchanged |
| `mobilni-aplikace/src/features/testing-sessions` | Responsive dashboard, character/chat/media/memory/duel/fault/diagnostic workflows |
| `mobilni-aplikace/src/features/persona-lab` | Reused voice pipeline with capture disabled and independent output mute |
| Shared application and identity UI | Tester navigation, route guards, normal invitations/settings and bounded asset transport |
| `mobilni-aplikace/ios/App/App/AccountTransportPolicy.swift` | Exact test API/asset allowlist and Tester identity decoding |
| `backend-tests/Testing*`, `TesterIdentityHttpTests.cs` | Real HTTPS/passkey authorization, domain/provider-double, memory, native runtime, lifecycle and consent-race regressions |
| Application/voice component and browser tests | Text/no-microphone, typed PCM, continuity, provider evidence, errors, cancellation and desktop/mobile rendering |

Validation results belong to the implementation delivery receipt. The manual
real-provider procedure above was not executed as part of nonpaid implementation.
