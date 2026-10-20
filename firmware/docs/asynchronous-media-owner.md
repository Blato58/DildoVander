# Asynchronous media ownership

`RealtimeSessionEngine` has an explicit asynchronous mode shared by platform
callers. Its reader admits bounded events and calls `Poll()` during idle periods
as well as after input. Admission is not playback or control completion. The
existing HTTP mode remains during caller migration; the asynchronous ports are
not yet the production media composition.

`VoicePlaybackPort` reserves immutable drain handles at event admission.
`BoundedPlaybackOwner` retains at most four ordered raw barriers in addition to
its fixed 64 KiB PCM workspace. Later PCM can queue behind a barrier but cannot
pass it before its receipt is consumed. Same-frontier actions can share one raw
proof; facade receipts are distinct, monotonic and single-use. Cached receipts
must still match the current owner scope. Cancellation abandons old actions and
fences the original generation before a worker flush; old cleanup cannot stop a
successor session.

The engine retains at most four metadata actions. Pause never publishes a
proposal. Checkpoints retain their original playback ID, PCM count and response
generation. Completion checks the original domain observation and expiry before
drain, after drain and after its exact played ACK, then invokes its callback
once. New audio or metadata after a pending completion fails closed. Queue
admission, a provider pause or an unrelated ACK cannot supply this proof.
`VoiceControlPort` submissions are RAM-only; the platform retains one fresh
request ID and exact envelope for each monotonic handle. Unknown outcomes are
not replayed as new work. Every pending action retains its original deadline.

Input upload captures its input ID, sequence, epoch and budget under a short
mutex, then releases that mutex before network I/O. A replacement listener never
relabels an old captured block. Platform shutdown must fence playback/control,
stop and join borrowers, and only then clear or destroy their storage.

## ESP capture boundary

`EspIdfVoiceAudio::SetCaptureEnabled` changes only capture admission and its
capture epoch. It does not reset the playback converter, increment the TX epoch,
change amplifier mute or wait for DMA. The playback worker owns those actions.
The legacy `Listen` method remains for callers not yet migrated.
Scoped capture requests and nonblocking dequeues carry the raw Begin session.
Old-session cleanup cannot disable a successor microphone or dequeue its PCM;
legacy capture changes cannot alter a scoped TX owner.

ESP has no verified AEC. Live keeps its input timeline continuous using
same-length zero PCM for speaker-tainted blocks; Realtime discards those blocks.
Classification covers the whole borrowed RX read and conversion, using a
separate output-taint version and sticky unsafe-tail state. Speaker samples are
not retained in the RX FIR for later quiet blocks. New Begin flushes the prior
software/DMA tail before publishing a fresh capture lease. Stop and immediate
mute remain independent of that bounded worker I/O.

Profile B does not gain a new hardware mute: the existing software gate and
silence-flush policy remain. Host tests prove ordering and refusal behavior with
simulated DMA, not the acoustic tail, board timing, actual task-stack margin or
peak heap. Those measurements belong to the exact-build bench checklist.
