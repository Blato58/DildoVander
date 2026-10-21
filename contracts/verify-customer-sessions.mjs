import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const schema = JSON.parse(await readFile(new URL('./customer-ble-v1.schema.json', import.meta.url)));
const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const validate = ajv.compile(schema);
const uuid = '11111111-2222-4333-8444-555555555555';
const start = { v: 1, op: 'start', ticket: 'a'.repeat(43), operationId: uuid };
const status = { v: 1, deviceId: 'synthetic-device', bootId: uuid, challenge: uuid,
  capabilities: { customerStart: 1 }, state: 'starting', operationId: uuid };
for (const value of [start, status, { v: 1, op: 'challenge' }, { v: 1, op: 'status', operationId: uuid },
  { v: 1, op: 'end', sessionId: uuid }, { ...status, state: 'recovering', challenge: '' }]) {
  assert.equal(validate(value), true, JSON.stringify(validate.errors));
  assert.ok(Buffer.byteLength(JSON.stringify(value)) <= 512, 'compact messages fit the common native/firmware wire ceiling');
}
for (const value of [{ ...start, v: true }, { ...start, v: 1.1 }, { ...start, ticket: 'a'.repeat(257) },
  { ...start, ownerAccountId: uuid }, { ...start, serviceProof: 'forbidden' }, { ...start, op: 'wifi' },
  { ...start, operationId: 'not-a-uuid' }, { ...status, state: 'ready' },
  { ...status, capabilities: { customerStart: true } }, { ...status, profile: { name: 'private' } }]) {
  assert.equal(validate(value), false, 'invalid/privileged/private customer wire payload is rejected');
}
console.log('Customer BLE v1 schema fixtures passed; synthetic data only.');
