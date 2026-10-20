# Device/backend media v1

The backend exposes `GET /v1/conversations/{id}/device-media` as a persistent
WebSocket for an autonomous physical device. The application subprotocol is
`kufr-device-media-v1`. This is a device/backend connection; the phone is not a
relay. Production clients must use WSS with normal hostname/certificate checks.
The current ESP transport migration is unfinished; this endpoint alone does not
advertise a device capability or prove physical audio operation.

The upgrade requires a current device `Authorization: Bearer ...`, a nonempty
UUID `Idempotency-Key` header, and the exact subprotocol. Browser account cookies
do not authorize this route. Credentials never appear in the URL or frames.
Every admitted input/control checks current bearer authority and the exact
device/conversation/session registry binding; hardware authority is checked as
well. An independent one-second timer also expires an idle or blocked session.
The first complete opening frame rechecks current bearer authority before any
shared admission or provider work; upgrade-time authentication is not sufficient.

## Opening and lifetime

The first complete message must be UTF-8 JSON with exactly these properties:

```json
{"type":"open","engine":"gpt-live","request":{}}
```

The `request` above stands for the complete existing `RealtimeTurnRequest`, not
an empty request. It must pass the same strict parser, canonical original
`operationEpoch`, opening and catalog/snapshot validation as the existing HTTP
entry point. `engine` is `gpt-live` (primary `gpt-live-1`) or the explicit
`realtime` alternative (`gpt-realtime-2.1`). The core admission, immutable
correlation, provider lifetime, memory, usage and generation/visual write gates
are shared; there is no alternate admission or provider retry implementation.
Opening bytes are cleared after admission on every terminal path.

The connection has an absolute 180-second lifetime including its opening, and a
five-second opening deadline. A reconnect cannot extend the old lifetime or
replay ambiguous microphone/provider input. The existing HTTP Realtime lifetime
and turn limits are unchanged. No provider key or provider protocol moves onto
the ESP.

## Ordered PCM

Server JSON events retain the existing `session`, `started`, `listening`,
`speech_stopped`, `audio_cancel`, `playback_checkpoint`, `completed`, `closed`
and other retained engine events. Audio uses binary PCM rather than base64 JSON.
An additive `media.operation` event binds the exact current operation and
response generation before the corresponding `started`/`listening` event, and
again after a generation-changing `audio_cancel`. Only a fully sent current
binding authorizes scoped cancellation. Before admitting the next turn, the
transport atomically retires the old binding or waits for an already claimed
cancel to settle. HTTP clients receive no extra event.
An output declaration precedes every new output stream:

```json
{"type":"media.output","stream":1,"responseGeneration":0,"format":"pcm16-mono-24000"}
```

Input is authorized by a server declaration binding its original input ID:

```json
{"type":"media.input","stream":2,"inputId":"UUID","format":"pcm16-mono-24000","maxFrameBytes":4096,"credit":1}
```

Stream numbers are server allocated, nonzero and never reused in a connection.
Repeating `listening` for the same input preserves its stream and sequence;
neither old samples nor old input IDs are relabelled. One previous input binding
is retained for an already credited frame in flight. The core session discards
ended input under its original identity. The device owns its half-duplex
microphone/playback gate; delayed samples captured before playback remain part
of their original input envelope.

Binary messages share the 16-byte KF v1 header layout with local media:

| Offset | Field |
| --- | --- |
| 0..1 | ASCII `KF` |
| 2 | version `1` |
| 3 | kind `1` input PCM, `2` output PCM |
| 4..7 | unsigned little-endian stream number |
| 8..11 | unsigned little-endian sequence, starting at zero |
| 12..15 | unsigned little-endian payload length |

Each payload is nonempty, even-length PCM16 mono 24 kHz, at most 4096 bytes.
Direction/kind interpretation belongs to this subprotocol; it is not an
authorization or semantic extension of phone/LAN `media-control-v1`.
WebSocket fragmentation is reassembled before header and sequence admission.
Duplicate/out-of-order chunks, invalid lengths and unknown streams terminate
the connection; samples are never silently replayed or relabelled.

Input has one frame of credit. After the actual provider upload accepts it, the
server sends `{"type":"media.ack","stream":2,"sequence":0}`. An ambiguous or
failed upload terminates the whole provider session. A sender must await this
ACK before another frame; it must bound its own capture queue and explicitly
fail on overload. The connection accepts at most 8,640,000 input PCM bytes.

## Playback, availability and cancellation

Controls are strict JSON, at most 1024 bytes. Each has a nonempty UUID
`requestId`. A non-cancel control has one outstanding credit, acknowledged only
after its actual application by
`{"type":"control.ack","requestId":"UUID","status":204}`.
Known availability refusals return status `409` and a bounded `code`, preserving
the connection. Realtime retains its original immutable snapshot: an identical
availability update is a no-op, while a changed snapshot returns
`conversation_restart_required` and requires a new correlated conversation.
Live retains its existing availability-update semantics. Up to 128 exact
request/body/status receipts are retained. An identical retry recovers
the receipt without reapplying the control; a changed body with the same ID
fails closed. No receipt is an authority to replay a physical action.
Input/control credit is separate from worker/send-task completion. The owner
returns the next credit before ACK bytes can become visible, so an immediate
legal peer response cannot race a still-unwinding send continuation.

Controls use these exact fields:

- `playback`: `type`, `requestId`, `playbackId`, `pcmBytes`, `drained:true`.
  The issued checkpoint ID and exact PCM frontier must match. Only actual local
  drain completion can supply this evidence; a pause is not provider completion.
- `played`: `type`, `requestId`, `operationId`, `responseGeneration`. This is the
  existing scoped completion barrier, never a direct actuator command. A typed
  reply barrier is installed before releasing that playback; next-operation
  admission waits for successful publication of this exact ACK. Refused played
  controls return `media_playback_mismatch` without releasing the domain barrier.
  Replaying an old cached receipt never installs a barrier on a later operation.
- `availability`: `type`, `requestId`, `bootId`, `generation`, `readyMask`,
  `deviceRevision`, and optional original canonical decimal-string
  `operationEpoch`. Existing immutable availability/epoch rules apply.
- `cancel`: `type`, `requestId`, `operationId`, `responseGeneration`. Both must
  match this session's latest emitted operation and current generation. The
  authenticated operation owner cancels it, then the connection ends. A cancel
  ACK is not guaranteed. Before publication, `current_operation_not_ready` is a
  typed 409; a stale binding returns `media_cancel_mismatch`. Both preserve the
  session and leave the journal untouched. Mismatch never falls back to a
  global Stop or incidental connection cleanup.

Cancellation claims, generation advancement and retirement before next-turn
admission share one short RAM gate. The durable cancellation attempt runs
outside that gate. A known refusal releases the claim; success or an unknown
mutation outcome permanently closes this stream's admission authority. Claim
settlement never waits for an ACK or a writer that is waiting on the claim.
An urgent current cancel also closes a next-admission waiter blocked on a played
ACK without requiring that ACK to drain. Unknown/failed ACK publication cannot
admit the next operation.

The device must mute/invalidate local queued output immediately on its own Stop
or cancellation; it must not wait for a network ACK. Backend technical or Lab
ACKs do not become production keepsake receipts.

## Ownership and failure bounds

There is one receive owner and one serialized socket sender. Provider events
remain inside their original write/generation gate until sends complete. There
is no unbounded output queue. One input-upload worker and one fixed control-reply
pump run independently. The input owner permits one provider upload and one
credited queued frame, clearing uploaded PCM before its ACK. The reply pump
retains at most one in-flight receipt, one reserved normal reply and one urgent
refusal reply. Urgent refusals cannot fill the normal reserved slot. A refused
urgent cancel can still be validated when its slot is full;
its receipt stays in the bounded ledger and an exact retry can recover it after
ACK credit becomes available. It creates no additional sender task. Non-cancel
controls require their single ACK credit before application. A blocked provider
or output send cannot prevent the receiver from applying a targeted cancel.
Every socket send has a
two-second deadline; expiry aborts and cancels the session rather than replaying.
The receive message limit is 8192 bytes. Any failure/status payload sent after
upgrade is JSON, never HTTP ProblemDetails; invalid frames may close without a
payload. Normal terminal events precede the close handshake. Cleanup observes
the outstanding receive after transport cancellation before clearing its borrowed
buffer; cancellation alone is not completion of that memory lease.

Current fake-provider and real local-socket tests are selected with
`dotnet run --project backend-tests/BackendTests.csproj -- --device-media`.
These tests do not prove TLS deployment, actual ESP/iPhone audio, physical DMA
tail bounds or hardware latency. Those checks remain separate from software
protocol acceptance.
