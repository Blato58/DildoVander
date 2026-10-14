# Native firmware twin host

The long-lived offline host composes production `SafetyController`,
`SelectionSequence`, `DeviceControlRuntime`, `PersistentCommandJournal`,
`PendingProposal`/`TurnPollGuard`, `WandPresentation`/`PhysicalPresentation`,
`PhysicalEffects` and `DanceCoordinator`. No provider, network, GPIO, speaker,
camera or SD device is contacted. It supports deterministic synthetic proposals,
accepted driver writes, synthetic tone enqueue evidence and a synthetic dance
media cursor. This proves software behavior only; it never proves hardware,
audible speech, external service integration or measured position.

## Build and test

From the repository root (Python 3.10+):

```powershell
python kufr/simulator/native/build_host.py --output "$env:TEMP/kufr-twin/native-twin.exe"
python kufr/simulator/native/test_host.py
```

The build helper reuses `firmware/tests/run_show_tests.py` compiler discovery:
g++, an active MSVC developer environment, or installed Windows Build Tools.
All objects and executables go beside the requested output, outside source.
Tests use temporary storage and perform no hardware or paid calls.

Linux with g++ can use the same Python commands or CMake:

```sh
cmake -S kufr/simulator/native -B /tmp/kufr-twin
cmake --build /tmp/kufr-twin
python3 kufr/simulator/native/test_host.py --executable /tmp/kufr-twin/kufr-native-twin
```

## Process and line protocol

```sh
native-twin --instance sim-development --boot unique-boot-id --journal /private/instance/journal.bin --build source-digest
```

All four arguments are mandatory. Instance, boot and build identifiers are
1-64 ASCII letters, digits, `.`, `_` or `-`. The storage parent must exist.
The server owns instance identity, creates a fresh boot identity per process,
supplies the verified build digest and injects the clock. A storage lock rejects
concurrent owners of one journal. Existing journal files are never deleted or
reinitialized: corrupt/unreadable storage fails closed. Writes flush a temporary
file, atomically replace the journal, then flush the containing directory on
POSIX or use Windows write-through replacement.

Optional `--cad <64 lowercase hexadecimal characters>` names the validated CAD
manifest mapping selected by isolated host configuration. It is emitted exactly
as `source.cadManifestRevision`; without it the field remains null. This metadata
does not introduce CAD geometry or kinematic parameters into the firmware model.

There is no unsolicited stdout. Every newline-terminated request yields one
full JSON snapshot on stdout; startup failures go to stderr and exit nonzero.
Requests are ASCII words, bounded to 16384 bytes. Oversize records are drained
without accumulating them and get one rejection, preserving response alignment.
Operation UUIDs must be canonical lowercase UUID-shaped strings.

| Command | Behavior |
| --- | --- |
| `SNAPSHOT` | Observe without advancing time or changing outputs. |
| `TICK <absolute-ms>` | Advance monotonically by at most 60000 ms per request; run 10 ms control passes. Time is bounded to 4102444800000 ms. |
| `TEXT <uuid> <1-6> <main\|short>` | Build a deterministic synthetic service response, apply the real poll/proposal guards, journal admission and selected compiled scene. |
| `STOP` | Disable/fault physical outputs, stop dance and presentation; explicit inspection is required. |
| `INSPECT` | Explicit simulated operator confirmation of all six props at rest/reloaded; run production recovery and inventory persistence. Reject while presentation/dance is active. |
| `FAULT none 0` | Clear injected failures/jams; this does not recover latched faults. |
| `FAULT servo <0-6>` | Fail future servo writes for a slot, or all slots with 0. |
| `FAULT led <0-6>` | Fail future LED writes (frame-wide). |
| `FAULT journal <0-6>` | Fail future journal writes (journal-wide). |
| `FAULT jam <1-6>` | Freeze only that model slot; provide no sensor feedback. |
| `DANCE <uuid>` | Run the production dance coordinator with a labeled synthetic 8-second, 120 BPM media cursor. Refuse while presentation/recovery owns effects. |
| `DANCE_STOP` | Request the real coordinator's stop; the next control pass observes synthetic media termination. |
| `INTEGRATION_PREPARE <clientTurn-uuid> <conversation-uuid> <utc-seconds> <hex-text>` | Build the actual production text-turn JSON request from the current coherent runtime snapshot. |
| `INTEGRATION_BIND <server-operation-uuid>` | Bind the server-assigned operation identity exactly once to the prepared handoff. |
| `INTEGRATION_RESULT <utc-seconds> <hex-response>` | Parse the production operation response, check the original correlation/snapshot/expiry and admit any validated proposal through the production presentation owner. |
| `INTEGRATION_CANCEL` | Clear only pending integration/proposal data; do not stop, recover or change an admitted presentation's outputs. |

Every response appends `commandResult` with `accepted`, a stable `code`,
`orderingHealthy`, and accepted `servoWrites`, `vibrationWrites`, `lightWrites`
counters (decimal strings). The transport removes this command-only envelope
before ingesting the strict observer snapshot. It is not a hardware acceptance
receipt. Duplicate/conflicting journal operations refuse re-execution, including
after restart; the 32-entry production journal history retains its normal bound.

Integration uses `ollivander-private-party-v1` and the production
`ConversationTextCodec`, `ConversationStartCodec`, `ConversationSession` UTF-8
validation, `TurnOperationCodec` and poll/proposal guards. PREPARE requires a
UUID boot identity and accepts at most 4000 decoded UTF-8 bytes/1000 characters
(the production JSON body also has a 4096-byte bound). Its one response includes
`externalRequest: {body: <actual JSON object>, correlation: {...}}`.
This sensitive request contains visitor text and is a transport handoff only:
the parent must strip both `externalRequest` and `commandResult` before storing,
logging or publishing telemetry. Later snapshots contain neither visitor text
nor raw service replies. The host never retains the text after that handoff.

The parent creates the conversation against its isolated backend using the
actual source boot/generation, submits the returned JSON object unchanged, and
binds the server's resulting operation ID. A single handoff may be pending.
RESULT accepts at most 4096 decoded response bytes; non-completed responses
are rejected while leaving the handoff pending for a later completed response.
Corrupt JSON, unrelated operation/correlation, changed runtime snapshots,
regressing UTC, more than 60 UTC or monotonic seconds of latency and expired
proposals refuse presentation. Stop clears the handoff. Completed ask/no-selection
results return `integration-reply-ready-no-audio`; they produce no simulated
speech or physical presentation. Valid proposals use `PendingProposal` and
`WandPresentation.Start`, with journal admission before effects.

This protocol permits integration testing through an externally configured
backend without replacing response parsing or device decisions with a C# model.
The host itself has no HTTP/provider adapter. Tests submit deterministic codec
fixtures offline; no real provider calls or paid tests are performed.
The parent sends `INTEGRATION_CANCEL` after integration failure/timeout to permit
another request without waiting for the handoff deadline. Cancellation never
reverses an already admitted operation; `STOP` owns that separate transition.

`PhysicalPresentation.SelectVariant(bool)` is idle-only and refuses active
presentation/decoration ownership. Production callers that do not invoke it
retain first/main, then subsequent/short behavior.

## Evidence boundary

The driver records accepted post-scaling 108-byte GRB frames, servo pulse writes,
OE and vibration state. The HAL enforces one lit segment, one vibrator, outputs
disabled during register writes and no concurrent servo/vibration command.
An injected failed write leaves the previous accepted frame/pulse intact.
Servo estimates come exclusively from production calibrated timers and masks.
`physicalVerification` always remains `unavailable`.

The separate normalized position model uses synthetic 1100/1900 us endpoints
and 500 ms full travel. Only accepted servo writes with outputs enabled move
the model. Disabling outputs and Stop freeze its position. A jam freezes the
model without altering the production firmware estimate: a timed `raised`
estimate with a jammed model at zero is intentional evidence of the absent
feedback channel. Inspection explicitly puts the simulated model at rest.
After every process boot `model` is null and production estimates stay unknown
until explicit inspection. A journal cannot reconstruct a pose reached before
restart, so the host never emits an automatic return to zero. Inspection then
explicitly establishes the simulated model at rest. Only the journal, not
physical position, persists across process restart; no command is replayed.

`audio` reports `simulated-tone-enqueued`, `simulated-tone-idle`,
`simulated-track-enqueued` or `simulated-track-drained`. No audio samples are
rendered. Native presentation/dance diagnostics retain their original
`ExportPresentationTrace` schema, evidence, sequence and drop count. Dance
trace time is the synthetic media cursor, distinct from device monotonic time.
The source uses the generated score's bundle hash; unavailable CAD revision
stays null. External speech, STT, live conversations and camera commands fail
explicitly as unsupported; their success is never simulated.
