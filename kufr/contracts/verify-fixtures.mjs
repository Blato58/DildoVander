import assert from 'node:assert/strict';
import { TextDecoder } from 'node:util';
import { readFile, readdir } from 'node:fs/promises';

import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { getNodeValue, parseTree } from 'jsonc-parser';

const decoder = new TextDecoder('utf-8', { fatal: true });
const maximumControlMessageBytes = 2048;

function assertStrictJsonTokens(node, source) {
  if (node.type === 'number') {
    const token = source.slice(node.offset, node.offset + node.length);
    if (!/^(0|[1-9][0-9]*|-[1-9][0-9]*)$/.test(token)) {
      throw new Error(`non-integer JSON token: ${token}`);
    }
  }

  if (node.type === 'object') {
    const keys = new Set();

    for (const property of node.children ?? []) {
      const [keyNode, valueNode] = property.children;
      const key = keyNode.value;
      if (keys.has(key)) {
        throw new Error(`duplicate JSON key: ${key}`);
      }

      keys.add(key);
      assertStrictJsonTokens(valueNode, source);
    }
  } else if (node.type === 'array') {
    for (const child of node.children ?? []) {
      assertStrictJsonTokens(child, source);
    }
  }
}

function parseStrictJson(bytes, maximumBytes = Infinity) {
  if (bytes.length > maximumBytes) {
    throw new Error(`message exceeds ${maximumBytes} UTF-8 bytes`);
  }

  const source = decoder.decode(bytes);
  const errors = [];
  const root = parseTree(source, errors, {
    allowTrailingComma: false,
    disallowComments: true,
  });

  if (!root || errors.length > 0) {
    throw new Error('invalid JSON syntax');
  }

  assertStrictJsonTokens(root, source);
  return getNodeValue(root);
}

const schemaBytes = await readFile(new URL('./control-message-v1.schema.json', import.meta.url));
const schema = parseStrictJson(schemaBytes);
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv, ['uuid']);
const validateSchema = ajv.compile(schema);

function validateMessage(bytes) {
  const message = parseStrictJson(bytes, maximumControlMessageBytes);
  if (!validateSchema(message)) {
    throw new Error(ajv.errorsText(validateSchema.errors));
  }

  return message;
}

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

const oversizedMessage = Buffer.concat([
  Buffer.from(JSON.stringify(selectionStart)),
  Buffer.alloc(maximumControlMessageBytes, 0x20),
]);
assert.throws(() => validateMessage(oversizedMessage), 'oversized control message');
invalidCount += 1;

console.log(`Validated ${validCount} valid and ${invalidCount} invalid control messages.`);
