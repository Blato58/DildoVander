# AI Persona Lab

Open **Persona Lab** in the application navigation and sign in. Every authenticated
account can test, including viewers without a device grant. The lab requires an
online account; cached offline device access does not authorize cloud tests.

Choose a saved persona, GPT Live (`gpt-live-1`) or Realtime 2.1
(`gpt-realtime-2.1`), a Czech scenario, and an empty or synthetic memory fixture.
Preparing a session does not call OpenAI. Sending text, starting voice, or running
a scenario suite does. Text uses these same conversation engines. Retired
Responses conversations and STT/dialog/TTS pipelines remain retired.

## Test, inspect, evaluate

- Start voice explicitly and grant microphone permission. Interrupt speech or
  stop voice before switching to text. Stop/end, navigation, logout, backgrounding
  and capture failure retire the microphone, playback graph and socket.
- Realtime supports a consented, metadata-free JPEG prepared in the browser,
  at most 640 by 640 pixels and 45,000 bytes. It is volatile, expires after
  15 minutes and is removed on explicit withdrawal/end/deletion. GPT Live image
  input is not advertised by this lab.
- **SIMULATION** is the default and cannot dispatch to a device. Deliberate
  **REAL SUITCASE** testing requires a paired native connection, the exact device
  operating grant, explicit confirmation and advertised `voiceTestContext`
  firmware support. Unsupported firmware stays unavailable; it never falls back
  to a production conversation. The device model and voice must match the frozen
  test configuration. A one-use ticket binds the device, boot and admitted
  revision for 90 seconds; the conversation retains the shared 180-second limit.
  Hardware tests reuse the existing suitcase conversation and physical gates.
  Synthetic memory never reads/writes guest or evening archives, images and guest
  binding are refused, and durable test provenance prevents keepsake publication,
  including delayed outcomes after restart. Technical physical outcome records
  remain authoritative; a proposal or transcript does not prove movement.
  Scoped voice Stop uses the originating test fingerprint as well as device,
  boot and connection generation. An old test cannot stop a later guest. The
  existing global hardware Stop keeps its separate explicit meaning. Device
  camera capture is currently disabled in hardware Lab mode.
- Inspect the transcript, version snapshot, synthetic context and chronological
  diagnostics. Evaluate all seven 1-5 metrics and attach comments to exact turn IDs.
  Results belong to their tester. Administrators can inspect system diagnostics;
  diagnostic exports omit transcript, images, audio and credentials.
  Configuration contains an explicit administrator system-diagnostics action.
  It loads the newest 20 sessions across testers with their last 20 technical
  events and marks omitted sessions/events. Its export excludes owner identities,
  instructions, memory and evaluation content.
- Transcript recording is opt-in per session. Otherwise text is held only in
  bounded RAM for the interaction, expires after 15 minutes of inactivity and is
  cleared on voice disconnect/end/deletion. Persisted turn IDs/timings remain.
  Reconnect creates a new provider context from permitted retained history and
  fixtures; unavailable words and partially spoken audio are not restored.
- A scenario suite makes actual provider calls and saves the exact persona,
  model, fixture/scenario snapshot and narrowly defined assertion results. The
  21 Czech scenarios contain 66 scripted turns with per-turn IDs, timings and
  results. The eight-turn dialogue-recall case asks for earlier fictional names
  and facts without repeating the expected answer in its retrieval questions,
  including a corrected alias and intervening topics. Lexical matches remain
  limited evidence; a person must assess negation, provenance and meaning.
  Each case is capped at eight turns and each suite at 120 turns. Lexical
  checks are observations, not human quality scores. Compare matching scenarios
  and fixtures before drawing conclusions from A/B ratings. Acoustic, network
  and physical behavior stays `not_exercised` when a text script cannot prove it.
  A tool-failure case records failure only when an actual Live client delegation
  received the simulated failure result; absent delegation is not a pass.

## Persona publication

The initial effective production instructions are saved as an immutable baseline.
Every edit creates a new draft. Testing a draft does not change production.
Publishing and rollback require administrator authority, recent authentication,
CSRF/origin validation, explicit confirmation and the expected active revision.
Publication pins instructions for **new** production conversations; existing
conversations retain their snapshot and safety policy. Model/voice fields are test
defaults; the production session retains its explicit engine/voice selection.
To restore a known version, select it in history, inspect the diff and explicitly
roll back. Older source baselines restore their saved instructions as well.

## Reliability and diagnosis

The previous HTML debug tester was removed during Vue consolidation; its native
successor remained under suitcase diagnostics with device/service access. The Lab
provides account-scoped testing independently of that device access boundary.

The production Live startup fix waits for the correlated provider
`session.started` before announcing listening. Previously iOS could fill its
192,000-byte PCM queue (four seconds) while the provider was still initializing.
Cancelled Realtime receives now abort/drain the outstanding SDK read before
disposing its enumerator. Retired audio generations cannot affect a new input
observer, and teardown releases pending upload readiness waiters.

For an incident, record model, persona revision, session/turn/event identifiers,
platform and the approximate UTC time. The timeline distinguishes connection,
provider, cancellation and context trimming. Export sanitized diagnostics, then
correlate with the existing [server diagnostics](../../codex/README.md).
Missing usage/latency/recovery observations stay unknown. Cost is an observed
conservative subtotal from provider usage, not a billing receipt; response timings
cover completion of the measured turn, not necessarily time to first sound.
Platform is a diagnostic client claim, never authorization. Realtime token
subtotals sum actual per-turn provider usage once; Live reports measured seconds
without inventing token counts. Coverage identifies how many turns supplied usage.

The source proxy generator enables HTTP/1.1 Upgrade, disables buffering and sets
65-second inactivity timeouts for `/app/v1/`. Server WebSocket keepalives run every
20 seconds. Updating source does not update an already installed nginx profile;
operational rollout must regenerate/review that profile through its owning flow.

## Configuration and verification

`PersonaLab:DatabasePath` defaults to `Backend:DatabasePath + ".persona-lab.sqlite"`.
Keep this database in persistent, private service storage. It is separate from
current guest, evening and returning-guest production memory. Deleting a Lab test
never deletes those stores. These technical limits are configurable:

| Setting | Default | Range |
| --- | ---: | ---: |
| `PersonaLab:MaxConcurrentSessions` | 4 | 1-16 |
| `PersonaLab:MaxConcurrentSuites` | 2 | 1-4 |
| `PersonaLab:MaxSessionSeconds` | 180 | 30-180 |
| `PersonaLab:MaxSuiteSeconds` | 900 | 30-1800 |

From the repository root, offline checks (also in backend CI):

```text
dotnet run --project backend-tests -- --persona-reliability
dotnet run --project backend-tests -- --persona-lab
dotnet run --project backend-tests -- --persona-lab-transport
```

Explicit paid diagnostics require existing backend provider configuration; never
paste credentials into arguments or logs:

```text
dotnet run --project backend-tests -- --gpt-live-smoke
dotnet run --project backend-tests -- --persona-lab-provider-smoke
dotnet run --project backend-tests -- --persona-provider-soak 1800
```

The soak uses one persistent Realtime provider connection with fictional text
every 30 seconds and discards audio after counting it. It reports observed
failures, latency and usage. It does not prove browser microphone, acoustic
barge-in, signed iPhone, reverse-proxy rollout or hardware acceptance. Browser
regressions use synthetic account/provider data and are identified accordingly.

Verification on 2026-10-20 used existing backend credentials: the Lab runtime
completed two text turns with each supported provider. Live completed in 31.98 s
and Realtime in 7.32 s (whole tests, not first-audio latency). A separate 1,800 s
Realtime connection completed 60 turns with zero failures or disconnects; mean
turn completion was 1,773 ms and p95 2,127 ms. These small observations do not
establish a production failure rate or persona-quality improvement. The original
persona remains unchanged. Automated scenario runs retain the exact input script
and its SHA-256 for comparison across configurations. The hardware Lab carrier
and scoped Stop require matching firmware support; no real suitcase/iPhone
acceptance is implied by source or synthetic tests.
