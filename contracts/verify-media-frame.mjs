import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

import {
  decodeMediaFrame,
  encodeMediaFrame,
  MediaStreamVerifier,
  maximumMediaPayloadBytes,
} from './media-frame-v1.mjs';

const payload = Buffer.from([0, 1, 127, 255]);
const frame = encodeMediaFrame({ kind: 1, streamId: 7, chunkSequence: 0, payload });
assert.equal(frame.length, 20);
assert.equal(frame.toString('ascii', 0, 2), 'KF');
assert.equal(frame.readUInt32LE(4), 7);
assert.equal(frame.readUInt32LE(12), payload.length);
assert.deepEqual(decodeMediaFrame(frame).payload, payload);
assert.equal(encodeMediaFrame({
  kind: 2,
  streamId: 0xffffffff,
  chunkSequence: 0xffffffff,
  payload: Buffer.alloc(maximumMediaPayloadBytes),
}).length, 4112);

for (const changed of [
  Buffer.from(frame.subarray(0, 15)),
  Buffer.concat([frame, Buffer.from([0])]),
  Buffer.from(frame.map((value, index) => index === 0 ? 0 : value)),
  Buffer.from(frame.map((value, index) => index === 2 ? 2 : value)),
  Buffer.from(frame.map((value, index) => index === 3 ? 4 : value)),
]) {
  assert.throws(() => decodeMediaFrame(changed));
}
const wrongLength = Buffer.from(frame);
wrongLength.writeUInt32LE(3, 12);
assert.throws(() => decodeMediaFrame(wrongLength));
assert.throws(() => encodeMediaFrame({ kind: 1, streamId: 0, chunkSequence: 0, payload }));
assert.throws(() => encodeMediaFrame({ kind: 1, streamId: 1, chunkSequence: 0, payload: Buffer.alloc(4097) }));
assert.throws(() => new MediaStreamVerifier({
  kind: 3, streamId: 1, maxBytes: 512 * 1024 + 1, startedAtMs: 0,
}));

const first = Buffer.from([1, 2, 3]);
const second = Buffer.from([4, 5]);
const stream = new MediaStreamVerifier({
  kind: 1, streamId: 7, maxBytes: 5, totalBytes: 5, startedAtMs: 100,
});
assert.deepEqual(stream.accept(encodeMediaFrame({
  kind: 1, streamId: 7, chunkSequence: 0, payload: first,
}), 150), first);
assert.deepEqual(stream.accept(encodeMediaFrame({
  kind: 1, streamId: 7, chunkSequence: 1, payload: second,
}), 160), second);
const digest = createHash('sha256').update(first).update(second).digest('hex');
assert.deepEqual(stream.finish({ totalBytes: 5, sha256: digest }, 170), {
  streamId: 7,
  totalBytes: 5,
});
assert.throws(() => stream.accept(frame, 180), /closed/);

function expectAborted(frameBytes, nowMs, expectedMessage) {
  const verifier = new MediaStreamVerifier({
    kind: 1, streamId: 7, maxBytes: 5, startedAtMs: 100,
  });
  assert.throws(() => verifier.accept(frameBytes, nowMs), expectedMessage);
  assert.throws(() => verifier.accept(frame, nowMs + 1), /closed/);
}

expectAborted(encodeMediaFrame({ kind: 1, streamId: 7, chunkSequence: 1, payload }), 110, /mismatch/);
expectAborted(encodeMediaFrame({ kind: 1, streamId: 8, chunkSequence: 0, payload }), 110, /mismatch/);
expectAborted(encodeMediaFrame({ kind: 2, streamId: 7, chunkSequence: 0, payload }), 110, /mismatch/);
expectAborted(frame, 1100, /timeout/);
const zeroStreamId = Buffer.from(frame);
zeroStreamId.writeUInt32LE(0, 4);
expectAborted(zeroStreamId, 110, /fields/);

const digestFailure = new MediaStreamVerifier({
  kind: 1, streamId: 7, maxBytes: 5, startedAtMs: 0,
});
digestFailure.accept(frame, 1);
assert.throws(() => digestFailure.finish({ totalBytes: 4, sha256: '0'.repeat(64) }, 2), /digest/);
assert.throws(() => digestFailure.finish({ totalBytes: 4, sha256: digest }, 3), /closed/);

console.log('Media frame v1: header, size, order, timeout, limits and SHA-256 passed');
