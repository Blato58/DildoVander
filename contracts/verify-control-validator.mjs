import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { parseStrictJson } from './control-validator-core.mjs';
import { validateControlMessage } from './control-validator.mjs';

// The compiler belongs only in generation and this differential test, never in
// the runtime graph consumed by NativeDeviceConnection or the browser shell.
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv, ['uuid']);
const referenceSchema = ajv.compile(parseStrictJson(await readFile(new URL('./control-message-v1.schema.json', import.meta.url))));
function reference(bytes) {
  const message = parseStrictJson(bytes, 8192);
  const diagnosticSnapshot = message.type === 'state.snapshot' && Object.prototype.hasOwnProperty.call(message, 'diagnostics');
  if (diagnosticSnapshot && bytes.length > 4096) throw new Error('message exceeds 4096 UTF-8 bytes');
  if (bytes.length > 2048 && !diagnosticSnapshot && message.type !== 'conversation.text'  && message.type !== 'conversation.result' && message.type !== 'offline.outbox') throw new Error('message exceeds 2048 UTF-8 bytes');
  if (!referenceSchema(message)) throw new Error(ajv.errorsText(referenceSchema.errors));
  return message;
}
function outcome(validator, bytes) {
  try { return { message: validator(bytes) }; }
  catch (error) { return { error: error.message }; }
}
let compared = 0;
function compare(bytes, label) {
  assert.deepEqual(outcome(validateControlMessage, bytes), outcome(reference, bytes), label);
  compared++;
}
for (const validity of ['valid', 'invalid']) {
  const directory = new URL(`./fixtures/${validity}/`, import.meta.url);
  for (const name of (await readdir(directory)).filter(name => name.endsWith('.json')).sort()) {
    const bytes = await readFile(new URL(name, directory));
    compare(bytes, `${validity}/${name}`);
    if (validity !== 'valid') continue;
    const message = JSON.parse(bytes.toString('utf8'));
    for (const key of Object.keys(message)) {
      const missing = structuredClone(message); delete missing[key];
      compare(Buffer.from(JSON.stringify(missing)), `${name}: missing ${key}`);
      for (const value of [null, -1, 0, 1.5, Number.MAX_SAFE_INTEGER + 1, 'invalid', [], {}]) {
        compare(Buffer.from(JSON.stringify({ ...message, [key]: value })), `${name}: mutated ${key}`);
      }
    }
  }
}
// Eight completed outcomes are a bounded data reply, not an actuator command.
// This new reply legitimately exceeds the ordinary 2 KiB control budget while
// retaining the common 8 KiB UTF-8 ceiling and schema's eight-record bound.
const boundedOutbox = JSON.parse(await readFile(new URL('./fixtures/valid/offline-outbox-reply.json', import.meta.url), 'utf8'));
const completedOutcome = { operationId: '11111111-1111-4111-8111-111111111111', deviceId: 'synthetic-contract-device',
  bootId: boundedOutbox.bootId, generation: 1, localEveningId: '33333333-3333-4333-8333-333333333333', eveningId: null,
  sourceMode: 'offline_scripted', compartment: 2, catalogVersion: 'ollivander-private-party-v1',
  startedAtDeviceMs: 1000, completedAtDeviceMs: 12000, timeConfidence: 'boot_monotonic', outcome: 'completed_by_timing' };
boundedOutbox.records = Array.from({ length: 8 }, () => ({ ...completedOutcome }));
const outboxBytes = Buffer.from(JSON.stringify(boundedOutbox));
assert.ok(outboxBytes.length > 2048 && outboxBytes.length <= 8192);
compare(outboxBytes, 'bounded eight-record offline outbox exceeds ordinary control budget');
assert.equal(validateControlMessage(outboxBytes).records.length, 8);
const ninthOutcome = Buffer.from(JSON.stringify({ ...boundedOutbox, records: [...boundedOutbox.records, completedOutcome] }));
compare(ninthOutcome, 'offline outbox refuses a ninth outcome');
assert.throws(() => validateControlMessage(ninthOutcome));
const pendingOutcomes = Buffer.from(JSON.stringify({ ...boundedOutbox, records: boundedOutbox.records.map(record => ({ ...record, outcome: 'pending' })) }));
compare(pendingOutcomes, 'offline outbox accepts no pending or inferred outcome');
assert.throws(() => validateControlMessage(pendingOutcomes));
// Epochs are uint64 decimal strings, including values beyond JS safe integers.
const epochSelection = JSON.parse(await readFile(new URL('./fixtures/valid/selection-start.json', import.meta.url), 'utf8'));
for (const epoch of ['0', '1', '9007199254740993', '18446744073709551615']) {
  const bytes = Buffer.from(JSON.stringify({ ...epochSelection, operationEpoch: epoch }));
  compare(bytes, `valid operation epoch ${epoch}`); validateControlMessage(bytes);
  const outcomeBytes = Buffer.from(JSON.stringify({ ...boundedOutbox, records: [{ ...completedOutcome, operationEpoch: epoch }] }));
  compare(outcomeBytes, `valid recorded outcome epoch ${epoch}`); validateControlMessage(outcomeBytes);
}
for (const epoch of ['', '00', '01', '-1', '+1', '1.0', '1e0', '18446744073709551616', '99999999999999999999', 1]) {
  const bytes = Buffer.from(JSON.stringify({ ...epochSelection, operationEpoch: epoch }));
  compare(bytes, `invalid operation epoch ${epoch}`); assert.throws(() => validateControlMessage(bytes));
  const outcomeBytes = Buffer.from(JSON.stringify({ ...boundedOutbox, records: [{ ...completedOutcome, operationEpoch: epoch }] }));
  compare(outcomeBytes, `invalid recorded outcome epoch ${epoch}`); assert.throws(() => validateControlMessage(outcomeBytes));
}
const epochStateGet = { protocolVersion: 1, type: 'state.get', messageId: epochSelection.commandId,
  deviceId: epochSelection.deviceId, bootId: epochSelection.bootId, includeOperationEpoch: true };
validateControlMessage(Buffer.from(JSON.stringify(epochStateGet)));
for (const message of [{ ...epochStateGet, includeOperationEpoch: false }, { ...epochStateGet, type: 'safety.stop' }]) {
  assert.throws(() => validateControlMessage(Buffer.from(JSON.stringify(message))));
}
// Exercise lexical and UTF-8 safety independently of schema validity.
for (const bytes of [Buffer.from('{"x":1,"x":2}'), Buffer.from('{"x":1e0}'), Buffer.from('{"x":1.0}'),
  Buffer.from('{/* comment */"x":1}'), Buffer.from([0xc3, 0x28]), Buffer.alloc(8193, 0x20)]) compare(bytes, 'strict raw bytes');

const runtime = new URL('./control-validator.mjs', import.meta.url).href;
const fixture = new URL('./fixtures/valid/selection-start.json', import.meta.url).href;
const withoutCodeGeneration = spawnSync(process.execPath, ['--disallow-code-generation-from-strings', '--input-type=module', '-e', `
  globalThis.Function = function () { throw new Error('Runtime attempted dynamic compilation'); };
  const { readFile } = await import('node:fs/promises');
  const { validateControlMessage } = await import(${JSON.stringify(runtime)});
  validateControlMessage(await readFile(new URL(${JSON.stringify(fixture)})));
  validateControlMessage(new TextEncoder().encode(JSON.stringify(${JSON.stringify(boundedOutbox)})));
`], { encoding: 'utf8' });
assert.equal(withoutCodeGeneration.status, 0, withoutCodeGeneration.stderr);
console.log(`Standalone validator matches AJV for ${compared} fixture/mutation/raw cases; runtime import and validation pass with Function blocked and V8 string code generation disabled.`);

// Diagnostics extend only an explicitly requested data snapshot, with a 4 KiB
// ceiling. Ordinary control and legacy snapshot limits remain unchanged.
const diagnosticSnapshot = JSON.parse(await readFile(new URL('./fixtures/valid/state-snapshot-diagnostics.json', import.meta.url), 'utf8'));
const diagnosticBytes = Buffer.from(JSON.stringify(diagnosticSnapshot));
validateControlMessage(diagnosticBytes);
const diagnosticAtLimit = Buffer.concat([diagnosticBytes, Buffer.alloc(4096 - diagnosticBytes.length, ' ')]);
compare(diagnosticAtLimit, 'diagnostic snapshot at4096 bytes');
validateControlMessage(diagnosticAtLimit);
const diagnosticOverLimit = Buffer.concat([diagnosticAtLimit, Buffer.from(' ')]);
compare(diagnosticOverLimit, 'diagnostic snapshot over4096 bytes');
assert.throws(() => validateControlMessage(diagnosticOverLimit), /4096/);
const secretDiagnostic = structuredClone(diagnosticSnapshot);
secretDiagnostic.diagnostics.storage.token = 'forbidden';
assert.throws(() => validateControlMessage(Buffer.from(JSON.stringify(secretDiagnostic))));
const legacySnapshot = structuredClone(diagnosticSnapshot); delete legacySnapshot.diagnostics;
const legacyBytes = Buffer.from(JSON.stringify(legacySnapshot));
assert.throws(() => validateControlMessage(Buffer.concat([legacyBytes, Buffer.alloc(2049 - legacyBytes.length, ' ')])), /2048/);
