import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import Ajv from 'ajv';
import addFormats from 'ajv-formats';

const document = JSON.parse(await readFile(new URL('./backend-openapi-v1.json', import.meta.url), 'utf8'));
const schemas = document.components.schemas;
const ajv = new Ajv({ allErrors: true, strict: true });
addFormats(ajv);
ajv.addFormat('binary', { type: 'string', validate: () => true });

for (const [name, schema] of Object.entries(schemas)) {
  const standalone = JSON.parse(JSON.stringify(schema).replaceAll('#/components/schemas/', 'https://kufr.local/schemas/'));
  ajv.addSchema(standalone, `https://kufr.local/schemas/${name}`);
}

function validate(name, value, expected) {
  const validator = ajv.getSchema(`https://kufr.local/schemas/${name}`);
  assert.ok(validator, `missing schema ${name}`);
  assert.equal(validator(value), expected, `${name}: ${JSON.stringify(validator.errors)}`);
}

function resolveReference(reference) {
  assert.ok(reference.startsWith('#/'), `external reference: ${reference}`);
  const target = reference.slice(2).split('/').reduce((value, segment) => value?.[segment], document);
  assert.ok(target, `missing reference: ${reference}`);
}

function walk(value) {
  if (!value || typeof value !== 'object') {
    return;
  }
  if (value.$ref) {
    resolveReference(value.$ref);
  }
  for (const child of Object.values(value)) {
    walk(child);
  }
}

assert.equal(document.openapi, '3.1.0');
walk(document);
assert.deepEqual(Object.keys(document.paths).sort(), [
  '/v1/audio/{audioId}',
  '/v1/conversations',
  '/v1/conversations/{id}',
  '/v1/conversations/{id}/opening',
  '/v1/conversations/{id}/speech',
  '/v1/conversations/{id}/transcriptions',
  '/v1/conversations/{id}/turns',
  '/v1/device-access/token',
  '/v1/diagnostics',
  '/v1/evening-memory',
  '/v1/evening-memory/{id}',
  '/v1/operations/{id}',
]);
assert.deepEqual(document.paths['/v1/device-access/token'].post.security, [{ DeviceCredential: [] }]);
assert.deepEqual(document.security, [{ DeviceAccess: [] }]);
const diagnosticSchema = document.paths['/v1/diagnostics'].get.responses['200'].content['application/json'].schema;
const validateDiagnostics = ajv.compile(diagnosticSchema);
const diagnostics = { voiceMode: 'fake', dialogMode: 'fake', catalogVersion: 'test-1', audioFormat: 'pcm16-mono-32000-wav' };
assert(validateDiagnostics(diagnostics));
assert(validateDiagnostics({ ...diagnostics, voiceMode: 'openai', dialogMode: 'responses' }));
assert(!validateDiagnostics({ ...diagnostics, credential: 'secret' }));
assert(!validateDiagnostics({ ...diagnostics, voiceMode: 'unknown' }));
assert.equal(document.paths['/v1/conversations/{id}/transcriptions'].post.requestBody.content['multipart/form-data'].encoding.wav.contentType, 'audio/wav');
assert.ok(document.paths['/v1/audio/{audioId}'].get.responses['200'].content['audio/wav']);
assert.equal(document.paths['/v1/audio/{audioId}'].get.responses['200'].headers['Cache-Control'].schema.const, 'no-store');

for (const [path, operations] of Object.entries(document.paths)) {
  for (const [method, operation] of Object.entries(operations)) {
    assert.ok(operation.operationId, `missing operationId on ${path}`);
    assert.ok(operation.responses, `missing responses on ${path}`);
    if ((method === 'post' || method === 'delete') && path !== '/v1/device-access/token') {
      assert.ok(operation.parameters?.some((parameter) => parameter.$ref === '#/components/parameters/IdempotencyKey'), `missing idempotency on ${path}`);
    }
  }
}

const bootId = 'c85743d5-1b72-41b8-8500-ae3c102b27d6';
const clientTurnId = '523ef590-ce4d-4901-91b3-79de1bfe7e35';
const conversation = {
  deviceId: 'kufr-001', bootId, generation: 1, catalogVersion: 'test-1',
  locale: 'cs-CZ', consent: { audio: false, image: false },
};
validate('CreateConversation', conversation, true);
validate('CreateConversation', { ...conversation, locale: 'en-US' }, false);
validate('CreateConversation', { ...conversation, consent: { audio: true } }, false);
validate('CreateConversation', { ...conversation, accessToken: 'secret' }, false);

const turn = {
  clientTurnId, bootId, generation: 1, text: 'Rád bych hůlku.',
  readyMask: 63, deviceRevision: 2, catalogVersion: 'test-1',
};
validate('TextTurn', turn, true);
const { text: omittedOpeningText, ...opening } = turn;
validate('OpeningTurn', opening, true);
validate('OpeningTurn', turn, false);
validate('OpeningTurn', { ...opening, readyMask: 64 }, false);
validate('TextTurn', { ...turn, text: 'x'.repeat(1001) }, false);
validate('TextTurn', { ...turn, readyMask: 64 }, false);
validate('TextTurn', { ...turn, deviceRevision: -1 }, false);
validate('TextTurn', { ...turn, clientTurnId: 'not-a-uuid' }, false);

const upload = { clientTurnId, bootId, generation: 1, deviceRevision: 2, wav: 'binary WAV placeholder' };
validate('TranscriptionUpload', upload, true);
validate('TranscriptionUpload', { ...upload, deviceRevision: -1 }, false);
validate('TranscriptionUpload', { ...upload, text: 'not part of this request' }, false);
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'device' }, true);
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'both' }, false);
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'phone', text: 'inject this' }, false);

const operation = {
  operationId: 'op-1', kind: 'turn', status: 'pending',
  correlation: {
    deviceId: 'kufr-001', bootId, conversationId: 'conversation-1',
    clientTurnId, generation: 1, deviceRevision: 2,
  },
  result: null, error: null,
};
validate('TurnOperation', operation, true);
validate('TurnOperation', { ...operation, status: 'completed', result: null }, false);
const result = { reply: 'Navrhuji další otázku.', decision: 'ask', reason: '', proposal: null };
validate('TurnOperation', { ...operation, status: 'completed', result }, true);
validate('Operation', { ...operation, status: 'failed', error: { code: 'provider_unavailable' } }, true);
validate('Operation', { ...operation, status: 'failed' }, false);
validate('Operation', { ...operation, status: 'completed', result }, true);
validate('Operation', { ...operation, kind: 'transcription', status: 'completed', result: { transcript: 'Dobrý den', source: 'deviceMicrophone' } }, true);
validate('Operation', { ...operation, kind: 'transcription', status: 'completed', result: { transcript: 'Dobrý den', source: 'unknown' } }, false);
validate('Operation', { ...operation, kind: 'speech', status: 'completed', result: { audioId: 'audio-1', expiresAt: '2026-09-29T01:00:00Z', outputTarget: 'phone' } }, true);
validate('Operation', { ...operation, kind: 'speech', status: 'completed', result: { audioId: 'audio-1', expiresAt: '2026-09-29T01:00:00Z', outputTarget: 'both' } }, false);
validate('TurnResult', { ...result, decision: 'propose' }, false);
validate('TurnResult', { ...result, proposal: { compartment: 7 } }, false);
validate('Problem', { type: 'about:blank', title: 'Conflict', status: 409, code: 'idempotency_conflict', traceId: 'trace-1' }, true);
validate('Problem', { type: 'about:blank', title: 'Conflict', status: 409, code: 'idempotency_conflict', traceId: 'trace-1', prompt: 'secret' }, false);

console.log('Backend OpenAPI E2.6: paths, security, references and request/result boundaries passed');
