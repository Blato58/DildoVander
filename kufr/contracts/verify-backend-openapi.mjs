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
  '/v1/admin/logs',
  '/v1/admin/logs/status',
  '/v1/admin/modes',
  '/v1/admin/memory',
  '/v1/admin/memory/guest',
  '/v1/admin/memory/records/{id}',
  '/v1/admin/memory/verify',
  '/v1/audio/{audioId}',
  '/v1/conversations',
  '/v1/conversations/{id}',
  '/v1/conversations/{id}/costume-context',
  '/v1/conversations/{id}/image',
  '/v1/conversations/{id}/gpt-live',
  '/v1/conversations/{id}/gpt-live/{streamId}/audio',
  '/v1/conversations/{id}/gpt-live/{streamId}/played',
  '/v1/conversations/{id}/gpt-live/{streamId}/playback',
  '/v1/conversations/{id}/gpt-live/{streamId}/availability',
  '/v1/conversations/{id}/opening',
  '/v1/conversations/{id}/realtime-live',
  '/v1/conversations/{id}/realtime-live/{streamId}/audio',
  '/v1/conversations/{id}/realtime-live/{streamId}/played',
  '/v1/conversations/{id}/realtime-turns',
  '/v1/conversations/{id}/speech',
  '/v1/conversations/{id}/transcriptions',
  '/v1/conversations/{id}/turns',
  '/v1/device-access/token',
  '/v1/diagnostics',
  '/v1/firmware/manifests/{channel}.json',
  '/v1/firmware/artifacts/{hash}.bin',
  '/v1/evening-memory',
  '/v1/evening-memory/{id}',
  '/v1/evenings',
  '/v1/evenings/{id}/active',
  '/v1/operations/{id}',
].sort());
assert.deepEqual(document.paths['/v1/device-access/token'].post.security, [{ DeviceCredential: [] }]);
assert.deepEqual(document.security, [{ DeviceAccess: [] }]);
for (const path of ['/v1/firmware/manifests/{channel}.json', '/v1/firmware/artifacts/{hash}.bin']) {
  assert.deepEqual(document.paths[path].get.security, []);
  assert(document.paths[path].get.responses['429']);
}
validate('FirmwareEnvelope', { schemaVersion: 1, payload: 'e30=', signature: 'MEYCIQD12345' }, true);
validate('FirmwareEnvelope', { schemaVersion: 0, payload: 'e30=', signature: 'MEYCIQD12345' }, false);
validate('FirmwareEnvelope', { schemaVersion: 1, payload: 'e30=', signature: 'MEYCIQD12345', privateKey: 'secret' }, false);
assert.deepEqual(document.paths['/v1/admin/modes'].get.security, [{ ServerAdmin: [] }]);
assert.deepEqual(document.paths['/v1/admin/modes'].put.security, [{ ServerAdmin: [] }]);
const serverModes = { revision: '00000000-0000-4000-8000-000000000001', dialogMode: 'fake', voiceMode: 'fake',
  dialogAvailable: true, voiceAvailable: true, activeConversations: 0, lifetime: 'until_restart', personalityId: 'party-original', personalityLifetime: 'persistent',
  personalities: [{ id: 'party-original', name: 'Původní party kufr', description: 'Původní personalita', promptSha256: 'a'.repeat(64) }] };
validate('ServerModes', serverModes, true);
validate('ServerModes', { ...serverModes, personalityLifetime: 'until_restart' }, false);
validate('PersonalityInfo', { ...serverModes.personalities[0], promptSha256: 'bad' }, false);
validate('ServerModeChange', { expectedRevision: serverModes.revision, dialogMode: 'fake', voiceMode: 'fake', personalityId: 'ollivander-trunk' }, true);
validate('ServerModes', { ...serverModes, voiceMode: 'unknown' }, false);
validate('ServerModes', { ...serverModes, credential: 'secret' }, false);
validate('ServerModeChange', { expectedRevision: serverModes.revision, dialogMode: 'responses', voiceMode: 'openai' }, true);
validate('ServerModeChange', { expectedRevision: 'stale', dialogMode: 'responses', voiceMode: 'openai' }, false);
const diagnosticSchema = document.paths['/v1/diagnostics'].get.responses['200'].content['application/json'].schema;
const validateDiagnostics = ajv.compile(JSON.parse(JSON.stringify(diagnosticSchema).replaceAll('#/components/schemas/', 'https://kufr.local/schemas/')));
const diagnostics = { voiceMode: 'fake', dialogMode: 'fake', catalogVersion: 'test-1', audioFormat: 'pcm16-mono-32000-wav' };
assert(validateDiagnostics(diagnostics));
assert(validateDiagnostics({ ...diagnostics, voiceMode: 'openai', dialogMode: 'responses' }));
assert(!validateDiagnostics({ ...diagnostics, credential: 'secret' }));
assert(!validateDiagnostics({ ...diagnostics, voiceMode: 'unknown' }));
assert.equal(document.paths['/v1/conversations/{id}/transcriptions'].post.requestBody.content['multipart/form-data'].encoding.wav.contentType, 'audio/wav');
const imageEndpoint = document.paths['/v1/conversations/{id}/image'].post;
const correctionEndpoint = document.paths['/v1/conversations/{id}/costume-context'].put;
assert.equal(imageEndpoint.requestBody.required, true);
assert.deepEqual(Object.keys(imageEndpoint.requestBody.content), ['image/jpeg']);
assert.deepEqual(imageEndpoint.requestBody.content['image/jpeg'].schema,
  { type: 'string', format: 'binary', maxLength: 262144 });
assert.equal(imageEndpoint.responses['202'].content['application/json'].schema.$ref, '#/components/schemas/ImageAccepted');
assert.equal(correctionEndpoint.responses['200'].content['application/json'].schema.$ref, '#/components/schemas/CostumeOperation');
for (const endpoint of [imageEndpoint, correctionEndpoint]) {
  assert(endpoint.parameters.some(parameter => parameter.$ref === '#/components/parameters/ConversationId'));
  assert(endpoint.parameters.some(parameter => parameter.$ref === '#/components/parameters/IdempotencyKey'));
  for (const name of ['Client-Turn-Id', 'Boot-Id', 'Generation', 'Device-Revision', 'Catalog-Version']) {
    const parameter = endpoint.parameters.find(parameter => parameter.name === name);
    assert(parameter?.required, `required costume correlation header ${name}`);
    assert.equal(parameter.in, 'header');
    if (name === 'Client-Turn-Id' || name === 'Boot-Id') assert.equal(parameter.schema.format, 'uuid');
    if (name === 'Generation' || name === 'Device-Revision') assert.equal(parameter.schema.minimum, 0);
    if (name === 'Catalog-Version') assert.equal(parameter.schema.maxLength, 128);
  }
  for (const status of ['400', '401', '403', '404', '408', '409', '413']) {
    assert.equal(endpoint.responses[status].$ref, '#/components/responses/Problem');
  }
}
assert.equal(correctionEndpoint.responses['429'].$ref, '#/components/responses/Problem');
assert.equal(document.paths['/v1/conversations/{id}'].delete.responses['503'].$ref, '#/components/responses/Problem');
const validateCorrection = ajv.compile(correctionEndpoint.requestBody.content['application/json'].schema);
assert(validateCorrection({ description: 'ě'.repeat(240) }));
assert(validateCorrection({ description: '' }));
assert(!validateCorrection({ description: 'x'.repeat(241) }));
assert(!validateCorrection({ description: 'cloak\nnew instruction' }));
assert(!validateCorrection({ description: 'cloak', imageUrl: 'https://untrusted.example/image.jpg' }));
assert.ok(document.paths['/v1/audio/{audioId}'].get.responses['200'].content['audio/wav']);
assert.equal(document.paths['/v1/audio/{audioId}'].get.responses['200'].headers['Cache-Control'].schema.const, 'no-store');

for (const [path, operations] of Object.entries(document.paths)) {
  for (const [method, operation] of Object.entries(operations)) {
    assert.ok(operation.operationId, `missing operationId on ${path}`);
    assert.ok(operation.responses, `missing responses on ${path}`);
    if ((method === 'post' || method === 'put' || method === 'delete')
      && path !== '/v1/device-access/token' && path !== '/v1/admin/modes' && !path.startsWith('/v1/admin/memory')
      && !path.endsWith('/{streamId}/audio') && !path.endsWith('/{streamId}/played')
      && !path.endsWith('/{streamId}/playback') && !path.endsWith('/{streamId}/availability')) {
      assert.ok(operation.parameters?.some((parameter) => parameter.$ref === '#/components/parameters/IdempotencyKey'), `missing idempotency on ${path}`);
    }
  }
}

const bootId = 'c85743d5-1b72-41b8-8500-ae3c102b27d6';
const eveningId = '4fda8bb7-147d-4fc3-b064-3d663e13ad15';
const evening = { eveningId, name: 'Původní večer', createdAt: '2026-10-01T10:00:00Z',
  exchangeCount: 3, droppedCount: 1 };
validate('EveningsList', { activeEveningId: eveningId, evenings: [evening] }, true);
validate('EveningsList', { activeEveningId: eveningId, evenings: [{ ...evening, transcript: 'secret' }] }, false);
validate('CreateEvening', { name: 'Nový večer', expectedEveningId: eveningId }, true);
validate('CreateEvening', { name: 'x'.repeat(81), expectedEveningId: eveningId }, false);
validate('CreateEvening', { name: 'Nový večer', expectedEveningId: eveningId, secret: 'x' }, false);
validate('ActivateEvening', { expectedEveningId: eveningId }, true);
validate('ActivateEvening', { expectedEveningId: 'stale' }, false);
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
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'phone', voice: 'cedar', speed: 1.15 }, true);
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'phone', voice: 'other' }, false);
validate('SpeechRequest', { turnId: 'turn-1', outputTarget: 'phone', speed: 1.3 }, false);
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
validate('ImageAccepted', { operationId: bootId, kind: 'image', status: 'pending' }, true);
validate('ImageAccepted', { operationId: 'not-a-uuid', kind: 'image', status: 'pending' }, false);
validate('ImageAccepted', { operationId: bootId, kind: 'image', status: 'completed' }, false);
const costumeContext = { description: 'ě'.repeat(240), possibleCharacter: 'x'.repeat(80),
  uncertainty: 'high', source: 'photo', expiresAt: '2026-10-09T12:02:00Z' };
validate('CostumeContext', costumeContext, true);
validate('CostumeContext', { ...costumeContext, source: 'manual', possibleCharacter: '' }, true);
validate('CostumeContext', { ...costumeContext, source: 'manual', description: '' }, true);
validate('CostumeContext', { ...costumeContext, description: '' }, false);
validate('CostumeContext', { ...costumeContext, source: 'fake', description: '' }, false);
validate('CostumeContext', { ...costumeContext, description: 'x'.repeat(241) }, false);
validate('CostumeContext', { ...costumeContext, description: 'x\u0085y' }, false);
validate('CostumeContext', { ...costumeContext, possibleCharacter: 'x'.repeat(81) }, false);
validate('CostumeContext', { ...costumeContext, uncertainty: 'certain' }, false);
validate('CostumeContext', { ...costumeContext, compartment: 1 }, false);
const imageOperation = { ...operation, operationId: bootId, kind: 'image' };
validate('CostumeOperation', imageOperation, true);
validate('Operation', { ...imageOperation, status: 'completed', result: costumeContext }, true);
validate('Operation', { ...imageOperation, kind: 'costume', status: 'completed', result: { ...costumeContext, source: 'manual' } }, true);
validate('CostumeOperation', { ...imageOperation, kind: 'costume', status: 'completed', result: { ...costumeContext, source: 'manual', description: '' } }, true);
validate('CostumeOperation', { ...imageOperation, status: 'completed', result: { ...costumeContext, source: 'manual', description: '' } }, false);
validate('CostumeOperation', { ...imageOperation, status: 'cancelled', result: null }, true);
validate('CostumeOperation', { ...imageOperation, correlation: { ...operation.correlation, bootId: 'wrong' } }, false);
validate('CostumeOperation', { ...imageOperation, status: 'completed', result: null }, false);
validate('CostumeOperation', { ...imageOperation, status: 'failed', error: { code: 'provider_unavailable' } }, true);
validate('CostumeOperation', { ...imageOperation, status: 'failed' }, false);
validate('TurnOperation', { ...operation, status: 'completed', result: null }, false);
const result = { reply: 'Navrhuji další otázku.', decision: 'ask', reason: '', proposal: null };
const realtime = { clientTurnId: operation.correlation.clientTurnId, bootId: operation.correlation.bootId,
  generation: 0, readyMask: 63, deviceRevision: 0, catalogVersion: 'ollivander-private-party-v1',
  opening: true, text: '', wav: null, voice: 'marin', speed: 1 };
validate('RealtimeTurn', realtime, true);
validate('RealtimeTurn', { ...realtime, text: 'Petr' }, false);
validate('RealtimeTurn', { ...realtime, opening: false, text: 'Petr' }, true);
validate('RealtimeTurn', { ...realtime, opening: false, wav: 'UklGRg==' }, true);
validate('RealtimeTurn', { ...realtime, opening: false, text: 'Petr', wav: 'UklGRg==' }, false);
validate('RealtimeTurn', { ...realtime, voice: 'onyx' }, false);
validate('RealtimeTurn', { ...realtime, speed: 1.21 }, false);
validate('RealtimeLiveStart', realtime, true);
validate('RealtimeLiveStart', { ...realtime, opening: false, text: 'Petr' }, false);
validate('RealtimeLiveStart', { ...realtime, accessToken: 'secret' }, false);
validate('RealtimePlayback', { operationId: bootId }, true);
validate('RealtimePlayback', { operationId: 'other-operation' }, false);
validate('RealtimeLiveEvent', { type: 'session', streamId: bootId, audioFormat: 'pcm16-mono-24000' }, true);
validate('RealtimeLiveEvent', { type: 'listening', inputId: clientTurnId }, true);
validate('RealtimeLiveEvent', { type: 'speech_stopped', inputId: clientTurnId }, true);
validate('RealtimeLiveEvent', { type: 'heartbeat' }, true);
validate('RealtimeLiveEvent', { type: 'completed', operationId: 'op-1', correlation: operation.correlation,
  result, costMicroUsd: 12345 }, true);
validate('RealtimeLiveEvent', { type: 'failed', code: 'cost_limit' }, true);
validate('RealtimeLiveEvent', { type: 'failed', code: 'provider secret message' }, false);
validate('RealtimeEvent', { type: 'started', operationId: 'op-1', correlation: operation.correlation,
  audioFormat: 'pcm16-mono-24000' }, true);
validate('RealtimeEvent', { type: 'audio', delta: Buffer.alloc(32768).toString('base64') }, true);
validate('RealtimeEvent', { type: 'audio', delta: Buffer.alloc(49152).toString('base64') }, false);
validate('RealtimeEvent', { type: 'completed', operationId: 'op-1', correlation: operation.correlation, result }, true);
validate('RealtimeEvent', { type: 'failed', code: 'ai_unavailable' }, true);
validate('RealtimeEvent', { type: 'failed', code: 'provider secret message' }, false);
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

assert.deepEqual(document.paths['/v1/admin/logs'].get.security, [{ ServerAdmin: [] }, { LogReader: [] }]);
const logEntry = { id: 1, timestamp: '2026-10-01T12:00:00Z', level: 'warning', eventCode: 'realtime_failed',
  traceId: bootId, route: null, method: null, status: null, durationMs: null,
  operationId: null, conversationId: null, code: 'cost_limit' };
const logPage = { storeId: bootId, oldestCursor: 1, latestCursor: 1, nextCursor: 1,
  hasMore: false, truncated: false, droppedWrites: 0, entries: [logEntry] };
validate('ServerLogPage', logPage, true);
validate('ServerLogEntry', { ...logEntry, text: 'private' }, false);
validate('ServerLogEntry', { ...logEntry, code: 'private content' }, false);
validate('ServerLogPage', { ...logPage, entries: Array(201).fill(logEntry) }, false);

validate('GptLiveStart', { ...realtime, speed: 1 }, true);
validate('GptLiveStart', { ...realtime, speed: 1.1 }, false);
validate('GptLiveEvent', { type: 'pause' }, true);
validate('GptLiveEvent', { type: 'pause', extra: true }, false);
validate('GptLiveEvent', { type: 'playback_checkpoint', playbackId: bootId, pcmBytes: 48000, decisionRevision: 2 }, true);
validate('GptLiveEvent', { type: 'playback_checkpoint', playbackId: bootId, pcmBytes: 3, decisionRevision: 2 }, false);
validate('LivePlaybackCheckpoint', { playbackId: bootId, pcmBytes: 48000, drained: true }, true);
validate('LivePlaybackCheckpoint', { playbackId: bootId, pcmBytes: 48000, drained: false }, false);
assert.deepEqual(document.paths['/v1/admin/memory'].get.security, [{ ServerAdmin: [] }]);
assert.deepEqual(document.paths['/v1/admin/memory/guest'].put.security, [{ ServerAdmin: [] }]);
