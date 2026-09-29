import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';

import { validateControlMessage } from './control-validator.mjs';

const validateMessage = validateControlMessage;

let validCount = 0;
let invalidCount = 0;

for (const expectedValidity of ['valid', 'invalid']) {
  const directory = new URL(`./fixtures/${expectedValidity}/`, import.meta.url);
  const files = (await readdir(directory)).filter((file) => file.endsWith('.json')).sort();

  for (const file of files) {
    const bytes = await readFile(new URL(file, directory));
    if (expectedValidity === 'valid') {
      assert.doesNotThrow(() => validateMessage(bytes), file);
      validCount += 1;
    } else {
      assert.throws(() => validateMessage(bytes), file);
      invalidCount += 1;
    }
  }
}

const selectionStart = validateMessage(
  await readFile(new URL('./fixtures/valid/selection-start.json', import.meta.url)),
);
const commandAck = validateMessage(
  await readFile(new URL('./fixtures/valid/command-ack.json', import.meta.url)),
);
const stateSnapshot = validateMessage(
  await readFile(new URL('./fixtures/valid/state-snapshot.json', import.meta.url)),
);
const safetyReset = validateMessage(
  await readFile(new URL('./fixtures/valid/safety-reset.json', import.meta.url)),
);
const hello = validateMessage(
  await readFile(new URL('./fixtures/valid/hello.json', import.meta.url)),
);
const welcome = validateMessage(
  await readFile(new URL('./fixtures/valid/welcome.json', import.meta.url)),
);
const heartbeat = validateMessage(
  await readFile(new URL('./fixtures/valid/heartbeat.json', import.meta.url)),
);

function expectRejectedMutation(label, original, mutate) {
  const message = structuredClone(original);
  mutate(message);
  assert.throws(() => validateMessage(Buffer.from(JSON.stringify(message))), label);
  invalidCount += 1;
}

for (const compartment of [0, 7, 1.5, '4', null]) {
  expectRejectedMutation(`compartment ${compartment}`, selectionStart, (message) => {
    message.payload.compartment = compartment;
  });
}

expectRejectedMutation('extra command field', selectionStart, (message) => {
  message.motorDurationMs = 1000;
});
expectRejectedMutation('unknown command type', selectionStart, (message) => {
  message.type = 'motor.start';
});
expectRejectedMutation('unsupported version', selectionStart, (message) => {
  message.protocolVersion = 2;
});
expectRejectedMutation('unsafe integer', selectionStart, (message) => {
  message.sequence = Number.MAX_SAFE_INTEGER + 1;
});
expectRejectedMutation('expired max age', selectionStart, (message) => {
  message.maxAgeMs = 2001;
});
expectRejectedMutation('invalid command ID', selectionStart, (message) => {
  message.commandId = 'not-a-uuid';
});
expectRejectedMutation('accepted ACK with an error', commandAck, (message) => {
  message.errorCode = 'busy';
});
expectRejectedMutation('rejected ACK without an error', commandAck, (message) => {
  message.status = 'rejected';
});
expectRejectedMutation('manual snapshot without a lease', stateSnapshot, (message) => {
  message.controlLeaseId = null;
});
expectRejectedMutation('invalid ready mask', stateSnapshot, (message) => {
  message.readyMask = 64;
});
expectRejectedMutation('reset without inspection', safetyReset, (message) => {
  message.inspectionConfirmed = false;
});
expectRejectedMutation('hello with unsupported version', hello, (message) => {
  message.protocolVersion = 2;
});
expectRejectedMutation('hello with extra bearer token', hello, (message) => {
  message.token = 'must-not-be-in-control-json';
});
expectRejectedMutation('welcome with mismatched snapshot type', welcome, (message) => {
  message.snapshot.type = 'hello';
});
expectRejectedMutation('heartbeat without lease', heartbeat, (message) => {
  delete message.controlLeaseId;
});

const oversizedMessage = Buffer.concat([
  Buffer.from(JSON.stringify(selectionStart)),
  Buffer.alloc(2048, 0x20),
]);
assert.throws(() => validateMessage(oversizedMessage), 'oversized control message');
invalidCount += 1;

console.log(`Validated ${validCount} valid and ${invalidCount} invalid control messages.`);
