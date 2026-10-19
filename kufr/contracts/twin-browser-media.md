# Native Twin browser media integration

This extends the existing Twin; physical sources remain observer-only. Browser
media is a platform HAL, never a proposal or effects engine. Server credentials
and the existing OpenAI key remain server-side. Provider tests remain fake unless
separately authorized. The owner authorized reusing the VPS provider key on
2026-10-14; no plaintext key is copied into this source tree or browser.

## Ownership

Shared C++ owns Realtime event sequencing, correlation, playback gates,
cancellation, technical limits, photo validation/fallback and pending proposals.
ESP and native adapters both consume the extracted production session engine.
The native control owner alone starts physical presentation after the shared
voice workflow finishes. Network/media waits execute outside the control loop.

The backend authenticates scoped simulation control, relays actual production
HTTP/NDJSON and bounded bytes, persists non-secret simulation settings, and
serves browser HAL frames. It never interprets a proposed slot or runs effects.
Browser owns explicit media permissions, PCM capture/playback, JPEG preview and
capture, consumed-byte/drain acknowledgements, and immediate local cancellation.

## Private native channel, v1

Existing ASCII control lines and one JSON reply remain supported. Extra commands:

- `MEDIA_START sessionUUID requestUUID realtime|gpt-live voice speed photoConsent main|short micAvailable speakerAvailable cameraAvailable`
  Availability flags are0/1. Camera availability means browser API capability;
  permission/capture still require explicit activation and preview acceptance.
- `MEDIA_CANCEL sessionUUID`
- `MEDIA_EVENT sessionUUID epoch byteLength` followed by UTF-8 production NDJSON
  bytes and one LF. Maximum line payload 65536 bytes; native parses it with the
  production codec on a bounded media worker, not the control loop.
- `MEDIA_FRAME sessionUUID epoch inputEpoch sequence pcm|jpeg byteLength` then
  exact bytes and one LF. PCM16 LE mono 24000 Hz, even length, at most9600 bytes;
  input buffering at most400ms. JPEG chunks at most32768 bytes, total262144.
- `MEDIA_RESULT requestUUID status contentType byteLength` then exact bytes and
  LF; this completes one typed private service request, maximum response65536.
- `MEDIA_PLAYED sessionUUID epoch outputEpoch sequence pcmBytes drained`
- `MEDIA_TAKE sessionUUID epoch`: take one bounded output/action for browser.

Epochs, sequences and byte totals are canonical uint64 decimal strings. A native
reply may contain `mediaServiceRequest` with requestUUID, kind, method, path,
idempotencyKey, correlation headers, contentType and byteLength, or `media` with
sessionId, bootId, epoch, inputEpoch, outputEpoch, sequence, kind, byteLength,
phase, drainedRequested, and optional transient dialogue text. After its JSON
line comes exactly byteLength bytes and one LF. Maximum attachment32768 bytes.
The image-upload service alone permits262144 bytes; native accepts JPEG as
ordered chunks of32768. A zero-byte JPEG frame explicitly declines capture.
Zero-byte media/service attachments still require their terminating LF.
Only one attachment per reply. These extensions are removed before snapshot
validation, persistence, recording and ordinary command responses. Credentials,
conversation text, PCM and JPEG never enter Twin technical telemetry.

Every reply also includes private `mediaState`: sessionId(null before start),
bootId, epoch, inputEpoch, outputEpoch, phase, active, inputEnabled and optional
error/photoWarning. `inputEnabled` is the actual VoiceAudioPort.Listen gate;
the phase label alone never enables microphone capture. HAL descriptors carry
the same gate. Current input state can overtake queued chronological output;
it must not invalidate already queued PCM of the same valid output epoch.

Service kinds are conversation-create/delete, image-upload/poll, realtime-start,
realtime-input, realtime-played and realtime-playback. Paths are allowlisted to
the isolated backend and exact simulated identity/conversation/session. The
network adapter holds the actual device token and supplies HTTP status/raw body;
C++ interprets results. No arbitrary URL, path, command or physical identity is
accepted from a browser. Streaming NDJSON arrives via MEDIA_EVENT; media-start
request acceptance returns its HTTP status separately.
Native creates and parses the actual production conversation-start request and
response. The network adapter binds authentication to the simulated instance;
it does not choose a conversation, proposal, slot or dialogue outcome. Service
headers contain no provider key. Native session workers use actual wall-clock
time and coherent ConversationRuntimeState snapshots while the owner keeps
processing control/Stop. Worker completion is handed back to the control owner
before calling PendingProposal/WandPresentation.

Stop invalidates media epochs immediately, clears queues and wakes waits. Late
events, frames, service responses and playback ACKs cannot revive an old session
or admit a proposal. Playback drain succeeds only for the current output epoch
after the browser reports completed scheduled playback; suspended/disconnected
audio fails or times out. This is software enqueue/drain evidence, never proof
of audible sound. Partial photo upload or lost acceptance is never replayed.

Wall-clock limits remain the production limits: photo120s, text/speech45s,
Realtime480s with per-turn80s, GPT-Live190s (input180s), at most10 turns.
Integration browser media disables injected clock controls. Deterministic tests
use explicit fake external services/media fixtures and retain injected device
time; real network deadlines always use wall time.

## Scoped browser API

All writes require the existing Secure/HttpOnly Twin cookie, exact Origin and
control scope for the selected native simulation. Observe/ingest/live cannot
access settings writes or media payloads. Only one media actor owns an instance.

- GET/PUT `/v1/twin/instances/{id}/settings`: expectedRevision plus engine
  fake|realtime|gpt-live, voice cedar|marin|coral, speed0.8–1.2 (GPT-Live1),
  photoConsent, and presentationVariant main|short. Revision is decimal string.
  Availability reports provider/media capabilities without keys. Changes reject
  active operations or explicitly stop and close the old owned session first.
- POST `.../media/start`: operationId UUID, expectedBootId, explicit audioConsent and optional
  photo consent and activated micAvailable/speakerAvailable, plus camera API
  capability. The native owner admits the workflow from its current snapshot.
- POST `.../media/cancel`: current session/epoch, always scoped; local browser
  closes input and stops playback immediately before this request. A pending or
  lost admission is cancelled with operationId/expectedBootId bound to the same
  controller; a queued start cannot revive it.
- GET `.../media/state`: private current phase/session/epochs and bounded
  nonpersistent dialogue. It is separate from observer snapshots.
- GET `.../media/output`: one bounded native HAL frame/action, private to actor.
- POST `.../media/input`: raw PCM with session/epoch/inputEpoch/sequence headers.
- POST `.../media/photo`: consciously accepted metadata-free JPEG, ≤640×640 and
 262144 bytes, with current session/epoch headers; native validates it.
- POST `.../media/played`: session/epoch/outputEpoch/sequence/pcmBytes/drained.

Browser capture keeps at most400ms of PCM including the current HTTP upload.
When network jitter fills that allowance, it discards the oldest **unsent**
samples and retains the newest audio with a visible warning. This avoids ending
the conversation for a transient backlog; some speech can be lost. Submitted
blocks remain sequential, are never replayed, and still count toward the bound.
Transport failures and invalid sequences retain their normal cancellation rules;
the native400ms queue and epoch/gate validation are unchanged.

The browser removes JPEG APP metadata and comments from its canvas output,
retaining only thumbnail-free canonical JFIF and the original encoded scan and
tables. This adapts browser ICC metadata to the existing firmware photo policy;
the production `ValidCostumeJpeg` validator remains authoritative and unchanged.
Successful input/photo transport returns `X-Twin-Media-Accepted-Bytes`. This
counts received bytes only; photo success requires the subsequent native state
without `photoWarning`. A HTTP ACK alone never proves validation or analysis.

Browser device selection remains local to browser; no device labels or unique
browser device IDs enter technical telemetry. Camera and microphone require
explicit activation. AudioContext must be unlocked by user action. Reload or a
new observer never restarts the native device or replays an input. Source switch
aborts subscriptions and cancels that browser's media HAL, preserving device
journal/recovery. Replay never calls any media or control endpoint.
