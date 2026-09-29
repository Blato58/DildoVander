import { createHash, timingSafeEqual } from 'node:crypto';

export const mediaHeaderBytes = 16;
export const maximumMediaPayloadBytes = 4096;
export const maximumMediaBytesByKind = Object.freeze({
  1: 1_920_000,
  2: 2 * 1024 * 1024,
  3: 512 * 1024,
});

function assertUint32(value, name, allowZero = true) {
  if (!Number.isInteger(value) || value < (allowZero ? 0 : 1) || value > 0xffffffff) {
    throw new Error(`invalid ${name}`);
  }
}

export function encodeMediaFrame({ kind, streamId, chunkSequence, payload }) {
  if (![1, 2, 3].includes(kind)) {
    throw new Error('invalid media kind');
  }
  assertUint32(streamId, 'streamId', false);
  assertUint32(chunkSequence, 'chunkSequence');
  if (!Buffer.isBuffer(payload) || payload.length < 1 ||
      payload.length > maximumMediaPayloadBytes) {
    throw new Error('invalid media payload length');
  }

  const frame = Buffer.allocUnsafe(mediaHeaderBytes + payload.length);
  frame.write('KF', 0, 'ascii');
  frame.writeUInt8(1, 2);
  frame.writeUInt8(kind, 3);
  frame.writeUInt32LE(streamId, 4);
  frame.writeUInt32LE(chunkSequence, 8);
  frame.writeUInt32LE(payload.length, 12);
  payload.copy(frame, mediaHeaderBytes);
  return frame;
}

export function decodeMediaFrame(bytes) {
  if (!Buffer.isBuffer(bytes) || bytes.length < mediaHeaderBytes ||
      bytes.length > mediaHeaderBytes + maximumMediaPayloadBytes) {
    throw new Error('invalid media frame length');
  }
  if (bytes.toString('ascii', 0, 2) !== 'KF' || bytes.readUInt8(2) !== 1) {
    throw new Error('invalid media frame header');
  }
  const kind = bytes.readUInt8(3);
  const streamId = bytes.readUInt32LE(4);
  const chunkSequence = bytes.readUInt32LE(8);
  const payloadLength = bytes.readUInt32LE(12);
  if (![1, 2, 3].includes(kind) || streamId === 0 ||
      payloadLength < 1 || payloadLength > maximumMediaPayloadBytes ||
      bytes.length !== mediaHeaderBytes + payloadLength) {
    throw new Error('invalid media frame fields');
  }
  return {
    kind,
    streamId,
    chunkSequence,
    payload: bytes.subarray(mediaHeaderBytes),
  };
}

// Tracks one negotiated stream without keeping PCM/JPEG content in memory.
export class MediaStreamVerifier {
  constructor({ kind, streamId, maxBytes, totalBytes = null, startedAtMs }) {
    if (![1, 2, 3].includes(kind)) {
      throw new Error('invalid media kind');
    }
    assertUint32(streamId, 'streamId', false);
    assertUint32(maxBytes, 'maxBytes', false);
    if (maxBytes > maximumMediaBytesByKind[kind]) {
      throw new Error('maxBytes exceeds media kind limit');
    }
    if (totalBytes !== null) {
      assertUint32(totalBytes, 'totalBytes', false);
      if (totalBytes > maxBytes) {
        throw new Error('totalBytes exceeds maxBytes');
      }
    }
    if (!Number.isSafeInteger(startedAtMs) || startedAtMs < 0) {
      throw new Error('invalid start time');
    }
    this.kind = kind;
    this.streamId = streamId;
    this.maxBytes = maxBytes;
    this.totalBytes = totalBytes;
    this.lastProgressAtMs = startedAtMs;
    this.nextSequence = 0;
    this.receivedBytes = 0;
    this.hash = createHash('sha256');
    this.closed = false;
  }

  accept(bytes, nowMs) {
    if (this.closed) {
      throw new Error('media stream is closed');
    }
    try {
      if (!Number.isSafeInteger(nowMs) || nowMs < this.lastProgressAtMs ||
          nowMs - this.lastProgressAtMs >= 1000) {
        throw new Error('media stream timeout');
      }
      const frame = decodeMediaFrame(bytes);
      if (frame.kind !== this.kind || frame.streamId !== this.streamId ||
          frame.chunkSequence !== this.nextSequence ||
          this.receivedBytes + frame.payload.length > this.maxBytes ||
          (this.totalBytes !== null &&
           this.receivedBytes + frame.payload.length > this.totalBytes)) {
        throw new Error('media stream mismatch');
      }
      this.hash.update(frame.payload);
      this.receivedBytes += frame.payload.length;
      this.nextSequence += 1;
      this.lastProgressAtMs = nowMs;
      return frame.payload;
    } catch (error) {
      this.closed = true;
      throw error;
    }
  }

  finish({ totalBytes, sha256 }, nowMs) {
    if (this.closed) {
      throw new Error('media stream is closed');
    }
    this.closed = true;
    if (!Number.isSafeInteger(nowMs) || nowMs < this.lastProgressAtMs ||
        nowMs - this.lastProgressAtMs >= 1000 ||
        this.receivedBytes === 0 || totalBytes !== this.receivedBytes ||
        (this.totalBytes !== null && totalBytes !== this.totalBytes) ||
        typeof sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(sha256)) {
      throw new Error('invalid media end');
    }
    const suppliedDigest = Buffer.from(sha256, 'hex');
    const actualDigest = this.hash.digest();
    if (!timingSafeEqual(suppliedDigest, actualDigest)) {
      throw new Error('media digest mismatch');
    }
    return { streamId: this.streamId, totalBytes: this.receivedBytes };
  }
}
