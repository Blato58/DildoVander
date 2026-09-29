import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

import { validateMediaControlMessage } from './media-validator.mjs';

const context = {
  bootId: randomUUID(),
  controlLeaseId: randomUUID(),
  conversationId: randomUUID(),
  generation: 2,
};
const hello = {
  protocolVersion: 1,
  type: 'media.hello',
  messageId: randomUUID(),
  deviceId: 'kufr-001',
  mediaTicket: 'a'.repeat(64),
  ...context,
};
const open = {
  protocolVersion: 1,
  type: 'media.open',
  requestMessageId: hello.messageId,
  bootId: context.bootId,
  conversationId: context.conversationId,
  generation: context.generation,
  streamId: 1,
  direction: 'phoneToDevice',
  kind: 'pcmCapture',
  format: 'pcm16le',
  sampleRateHz: 32000,
  channels: 1,
  maxBytes: 1920000,
  creditBytes: 16384,
};
const end = {
  protocolVersion: 1,
  type: 'media.end',
  messageId: randomUUID(),
  ...context,
  streamId: 1,
  totalBytes: 1280,
  sha256: 'a'.repeat(64),
};
const cancel = {
  protocolVersion: 1,
  type: 'media.cancel',
  messageId: randomUUID(),
  ...context,
  streamId: 1,
};
const credit = {
  protocolVersion: 1,
  type: 'media.credit',
  streamId: 1,
  creditBytes: 16384,
};
const result = {
  protocolVersion: 1,
  type: 'media.result',
  requestMessageId: end.messageId,
  streamId: 1,
  status: 'received',
  totalBytes: 1280,
  errorCode: null,
  simulated: true,
};

function encoded(message) {
  return Buffer.from(JSON.stringify(message));
}

for (const message of [hello, open, end, cancel, credit, result]) {
  assert.deepEqual({ ...validateMediaControlMessage(encoded(message)) }, message);
}

function reject(message, change) {
  const changed = structuredClone(message);
  change(changed);
  assert.throws(() => validateMediaControlMessage(encoded(changed)));
}

reject(hello, (message) => { delete message.controlLeaseId; });
reject(hello, (message) => { delete message.mediaTicket; });
reject(hello, (message) => { message.generation = 1.5; });
reject(open, (message) => { message.streamId = 0; });
reject(open, (message) => { message.format = 'opus'; });
reject(open, (message) => { message.maxBytes = 3000000; });
reject(end, (message) => { message.sha256 = 'A'.repeat(64); });
reject(end, (message) => { message.motorDurationMs = 100; });
reject(cancel, (message) => { message.streamId = 4294967296; });
reject(credit, (message) => { message.creditBytes = 16385; });
reject(result, (message) => { message.simulated = false; });
assert.throws(() => validateMediaControlMessage(Buffer.from(
  '{"protocolVersion":1,"protocolVersion":1}',
)));
assert.throws(() => validateMediaControlMessage(Buffer.concat([
  encoded(hello), Buffer.alloc(2048, 0x20),
])));

console.log('Media control v1: open, credit, end, cancel and strict rejection passed');
