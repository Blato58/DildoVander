import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import Ajv from 'ajv/dist/2020.js';
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
  '/v1/admin/memory/context',
  '/v1/admin/memory/scopes/{scope}/{subject}',
  '/v1/admin/conversations/{id}/media',
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
  '/v1/conversations/{id}/gpt-live/{streamId}/audio-stream',
  '/v1/conversations/{id}/gpt-live/{streamId}/played',
  '/v1/conversations/{id}/gpt-live/{streamId}/playback',
  '/v1/conversations/{id}/gpt-live/{streamId}/availability',
  '/v1/conversations/{id}/opening',
  '/v1/conversations/{id}/realtime-live',
  '/v1/conversations/{id}/realtime-live/{streamId}/audio',
  '/v1/conversations/{id}/realtime-live/{streamId}/audio-stream',
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
  '/v1/keepsakes/outcomes/{operationId}',
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
const engines = { schemaVersion: 1, primaryEngine: 'gpt-live', engines: [
  { engine: 'gpt-live', model: 'gpt-live-1', ready: true, unavailableReason: null,
    voices: ['cedar','marin','coral'], speed: {supported:false,min:1,max:1,default:1}, inputModes:['audio'], cameraSources:['phone','suitcase'] },
  { engine: 'realtime', model: 'gpt-realtime-2.1', ready: true, unavailableReason: null,
    voices: ['cedar','marin','coral'], speed: {supported:true,min:0.8,max:1.2,default:1}, inputModes:['audio'], cameraSources:['phone','suitcase'] },
] };
const serverModes = { revision: '00000000-0000-4000-8000-000000000001', engines,
  activeConversations: 0, lifetime: 'until_restart', personalityId: 'party-original', personalityLifetime: 'persistent',
  characterEnabled:false,characterTone:'Adult',characterFinaleProfile:'Blocked',characterVersion:'dv01-v1',
  personalities: [{ id: 'party-original', name: 'Původní party kufr', description: 'Původní personalita', promptSha256: 'a'.repeat(64) }] };
validate('ServerModes', serverModes, true);
validate('ServerModes', { ...serverModes, personalityLifetime: 'until_restart' }, false);
validate('ServerModes', { ...serverModes, dialogMode: 'responses', voiceMode: 'openai' }, false);
validate('PersonalityInfo', { ...serverModes.personalities[0], promptSha256: 'bad' }, false);
validate('ServerModeChange', { expectedRevision: serverModes.revision, personalityId: 'ollivander-trunk' }, true);
validate('ServerModeChange', { expectedRevision: serverModes.revision, dialogMode: 'responses', voiceMode: 'openai' }, false);
validate('ServerModeChange', { expectedRevision: 'stale' }, false);
validate('ServerModes', { ...serverModes, credential: 'secret' }, false);
validate('ConversationEngineCapabilities', engines, true);
validate('ConversationEngineCapabilities', { ...engines, schemaVersion: 2 }, false);
validate('ConversationEngineCapabilities', { ...engines, engines: [...engines.engines, engines.engines[0]] }, false);
validate('ConversationEngineCapabilities', { ...engines, engines: [engines.engines[0],engines.engines[0]] }, false);
validate('GptLiveCapability', {...engines.engines[0],ready:false,unavailableReason:'provider_credential_missing'}, true);
validate('GptLiveCapability', {...engines.engines[0],model:'retired-model'}, false);
validate('GptLiveCapability', {...engines.engines[0],ready:false}, false);
validate('GptLiveCapability', {...engines.engines[0],inputModes:['text','audio']}, false);
validate('RealtimeCapability', {...engines.engines[1],ready:'true'}, false);
const memoryProvider = {provider:'openai_vector_store',configured:true,degraded:false,configurationStatus:'configured',
  lastVerifiedAt:null,pending:0,failed:0,deleting:0,storedBytes:0,capacityBytes:1000000,rejectedWrites:0};
const diagnostics = { engines,conversationContractVersion:2,catalogVersion:'ollivander-private-party-v1',audioFormat:'pcm16-mono-24000',
  liveActorMode:'AutonomousActor',personalityId:'party-original',liveDecisionModel:'gpt-6.1-sol',liveTerminalQuietMs:1500,
  characterEnabled:false,characterTone:'Adult',characterFinaleProfile:'Blocked',characterVersion:'dv01-v1',
  characterVisualRoast:'costume_observations_only',characterNamedFinale:'blocked',characterAudio:'unsegmented_live_stream',characterEventReady:false,
  memoryEnabled:true,cloudIndexingEnabled:true,longTermPersonalEnabled:true,memoryProvider};
validate('BackendDiagnostics', diagnostics, true);
validate('BackendDiagnostics', {...diagnostics,liveActorMode:'LegacyBackendScripted'}, false);
validate('BackendDiagnostics', {...diagnostics,credential:'secret'}, false);
validate('BackendDiagnostics', {...diagnostics,privateSource:'guest detail'}, false);
const retiredRoutes = ['opening','turns','transcriptions','speech','realtime-turns'];
for (const route of retiredRoutes) {
  const endpoint = document.paths['/v1/conversations/{id}/'+route].post;
  assert.equal(endpoint.deprecated,true);
  assert.deepEqual(Object.keys(endpoint.responses),['410']);
  assert.equal(endpoint.requestBody,undefined);
  assert.equal(endpoint.responses['410'].content['application/problem+json'].schema.$ref,'#/components/schemas/EngineUpgradeProblem');
}
const imageEndpoint = document.paths['/v1/conversations/{id}/image'].post;
const correctionEndpoint = document.paths['/v1/conversations/{id}/costume-context'].put;
assert.equal(imageEndpoint.requestBody.required, true);
assert.deepEqual(Object.keys(imageEndpoint.requestBody.content), ['image/jpeg']);
assert.deepEqual(imageEndpoint.requestBody.content['image/jpeg'].schema,
  { type: 'string', format: 'binary', maxLength: 262144 });
assert.equal(imageEndpoint.responses['202'].content['application/json'].schema.$ref, '#/components/schemas/ImageAccepted');
assert.equal(correctionEndpoint.responses['200'].content['application/json'].schema.$ref, '#/components/schemas/CostumeOperation');
for (const endpoint of [imageEndpoint, correctionEndpoint]) {
  const epochHeader = endpoint.parameters.find(parameter => parameter.name === 'Operation-Epoch');
  assert.equal(epochHeader.required, false);
  assert.equal(epochHeader.schema.$ref, '#/components/schemas/OperationEpoch');
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
      && path !== '/v1/device-access/token' && !path.startsWith('/v1/admin/')
      && !retiredRoutes.some(route => path === '/v1/conversations/{id}/'+route)
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
for (const epoch of ['0', '9007199254740993', '18446744073709551615']) {
  validate('OperationEpoch', epoch, true);
  validate('CreateConversation', { ...conversation, operationEpoch: epoch }, true);
}
for (const epoch of [null, 1, '', '01', '+1', '-1', '1.0', '1e1', '1\n', '18446744073709551616']) {
  validate('OperationEpoch', epoch, false);
  validate('CreateConversation', { ...conversation, operationEpoch: epoch }, false);
}

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
validate('RealtimeLiveStart', realtime, true);
for (const operationEpoch of ['0', '9007199254740993', '18446744073709551615']) {
  validate('RealtimeLiveStart', { ...realtime, operationEpoch }, true);
  validate('GptLiveStart', { ...realtime, operationEpoch }, true);
  validate('TurnCorrelation', { ...operation.correlation, operationEpoch }, true);
  validate('LiveAvailability', { bootId, generation: 1, readyMask: 63, deviceRevision: 7, operationEpoch }, true);
}
const physicalOutcome = { schemaVersion: 1, outcomeId: bootId, proposalId: clientTurnId,
  physicalOperationId: clientTurnId, bootId, generation: 1, deviceRevision: 7,
  presentationEvidence: 'completed_by_timing' };
validate('KeepsakeOutcomeRequest', physicalOutcome, true);
const scopedOutcome = { ...physicalOutcome, operationEpoch: '18446744073709551615',
  physicalFingerprint: '9007199254740993' };
validate('KeepsakeOutcomeRequest', scopedOutcome, true);
validate('KeepsakeOutcomeRequest', { ...scopedOutcome, physicalFingerprint: '0' }, false);
validate('KeepsakeOutcomeRequest', { ...physicalOutcome, operationEpoch: '1' }, false);
validate('KeepsakeOutcomeRequest', { ...scopedOutcome, operationEpoch: null }, false);
validate('KeepsakeOutcomeAcknowledgement', { schemaVersion: 1, locator: bootId, outcomeId: bootId,
  sessionEpoch: 1, physicalOperationId: clientTurnId, operationEpoch: scopedOutcome.operationEpoch,
  physicalFingerprint: scopedOutcome.physicalFingerprint }, true);
validate('RealtimeLiveStart', { ...realtime, opening: false, text: 'Petr' }, false);
validate('RealtimeLiveStart', { ...realtime, accessToken: 'secret' }, false);
validate('RealtimePlayback', { operationId: bootId }, true);
validate('RealtimePlayback', { operationId: 'other-operation' }, false);
validate('RealtimePlayback', { operationId: bootId, responseGeneration: 0 }, true);
validate('RealtimePlayback', { operationId: bootId, responseGeneration: 2 }, true);
validate('RealtimePlayback', { operationId: bootId, responseGeneration: -1 }, false);
validate('RealtimePlayback', { operationId: bootId, responseGeneration: 0.5 }, false);
for (const schema of ['RealtimeLiveEvent', 'GptLiveEvent']) {
  validate(schema, { type: 'audio', delta: 'AAAA', responseGeneration: 0 }, true);
  validate(schema, { type: 'audio', delta: 'AAAA' }, false);
  validate(schema, { type: 'audio', delta: 'AAAA', responseGeneration: -1 }, false);
  validate(schema, { type: 'audio_cancel', responseGeneration: 1 }, true);
  validate(schema, { type: 'audio_cancel', responseGeneration: 2, visualRevision: 3 }, true);
  validate(schema, { type: 'audio_cancel', responseGeneration: 2, visualRevision: 0 }, false);
  validate(schema, { type: 'audio_cancel', responseGeneration: 2, visualRevision: 1000001 }, false);
  validate(schema, { type: 'audio_cancel', responseGeneration: 0 }, false);
}

validate('RealtimeLiveEvent', { type: 'session', streamId: bootId, audioFormat: 'pcm16-mono-24000' }, true);
validate('RealtimeLiveEvent', { type: 'listening', inputId: clientTurnId, responseGeneration: 0 }, true);
validate('RealtimeLiveEvent', { type: 'listening', inputId: clientTurnId, responseGeneration: -1 }, false);
validate('RealtimeLiveEvent', { type: 'speech_stopped', inputId: clientTurnId }, true);
validate('RealtimeLiveEvent', { type: 'heartbeat' }, true);
validate('RealtimeLiveEvent', { type: 'visitor', text: 'Host', responseGeneration: 2 }, true);
validate('RealtimeLiveEvent', { type: 'speech_stopped', inputId: clientTurnId, responseGeneration: 2 }, true);
validate('RealtimeLiveEvent', { type: 'closed', reason: 'conversation_terminal', responseGeneration: 2 }, true);
validate('GptLiveEvent', { type: 'pause', responseGeneration: 2 }, true);
validate('GptLiveEvent', { type: 'playback_checkpoint', playbackId: bootId, pcmBytes: 2, decisionRevision: 0, responseGeneration: 2 }, true);
validate('GptLiveEvent', { type: 'pause', responseGeneration: -1 }, false);

validate('RealtimeLiveEvent', { type: 'completed', operationId: 'op-1', correlation: operation.correlation,
  responseGeneration: 0, result, costMicroUsd: 12345 }, true);
validate('RealtimeLiveEvent', { type: 'started', operationId: 'op-1', correlation: operation.correlation, responseGeneration: 0 }, true);
validate('RealtimeLiveEvent', { type: 'started', operationId: 'op-1', correlation: operation.correlation }, false);
validate('RealtimeLiveEvent', { type: 'failed', code: 'cost_limit' }, true);
validate('RealtimeLiveEvent', { type: 'failed', code: 'provider secret message' }, false);
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

console.log('Backend OpenAPI conversation v2: retained engines, upgrades, security and request/result boundaries passed');

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

for (const engine of ['gpt-live','realtime-live']) {
  const stream = document.paths['/v1/conversations/{id}/'+engine+'/{streamId}/audio-stream'].get;
  assert(stream.responses['101']);
  assert.equal(stream.requestBody,undefined);
  assert.equal(stream['x-input-schema'].$ref,'#/components/schemas/AudioStreamFrame');
}
const frame = {inputId:bootId,sequence:0,audio:Buffer.alloc(9600).toString('base64')};
validate('AudioStreamFrame',frame,true);
validate('AudioStreamFrame',{...frame,sequence:-1},false);
validate('AudioStreamFrame',{...frame,audio:Buffer.alloc(9602).toString('base64')},false);
validate('AudioStreamFrame',{...frame,accessToken:'secret'},false);
validate('AudioStreamAck',{type:'ack',inputId:bootId,sequence:0},true);
validate('AudioStreamAck',{type:'ack',inputId:bootId,sequence:0,private:'secret'},false);
const revoke = document.paths['/v1/conversations/{id}/image'].delete;
assert(revoke.parameters.some(parameter => parameter.name === 'Image-Revision' && parameter.required));
for (const name of ['Image-Id','Image-Source','Captured-At','Image-Revision','Image-Consent']) {
  assert(imageEndpoint.parameters.some(parameter => parameter.name === name),name);
}
const imageSource = ajv.compile(imageEndpoint.parameters.find(parameter => parameter.name === 'Image-Source').schema);
assert(imageSource('phone')); assert(imageSource('suitcase')); assert(!imageSource('unknown'));
assert.deepEqual(document.paths['/v1/admin/conversations/{id}/media'].delete.security,[{ServerAdmin:[]}]);
assert.deepEqual(document.paths['/v1/admin/memory/context'].get.security,[{ServerAdmin:[]}]);
assert.deepEqual(document.paths['/v1/admin/memory/scopes/{scope}/{subject}'].delete.security,[{ServerAdmin:[]}]);
const identity = {deviceId:'kufr-001',conversationId:bootId,guestId:'returning-guest',identityVerified:true,
  longTermAllowed:true,cloudExportAllowed:false,version:1};
validate('GuestMemoryPermission',identity,true);
validate('GuestMemoryPermission',{...identity,identityVerified:false},false);
validate('GuestMemoryPermission',{...identity,longTermAllowed:false,cloudExportAllowed:true},false);
const source = {memoryId:bootId,scope:'Personal',deviceId:'kufr-001',eveningId,conversationId:bootId,
  guestId:'returning-guest',personalityId:null,kind:'UserReportedFact',text:'synthetic source fact',sourceReference:'input-1',
  createdAt:'2026-10-19T10:00:00Z',expiresAt:'9999-12-31T23:59:59Z',revision:1,isActive:true,supersedes:null,
  useAllowed:true,exportAllowed:false,permissionVersion:1,appNamespace:'development'};
const privateContext = {identity,epoch:3,deletionEpoch:1,records:[source],totalRecords:1,hasMore:false,memoryStatus:memoryProvider};
validate('PrivateMemoryContext',privateContext,true);
validate('PrivateMemoryContext',{...privateContext,records:Array(51).fill(source)},false);
validate('PrivateMemoryRecord',{...source,providerRaw:'private'},false);
validate('MemoryProviderStatus',{...memoryProvider,records:[source]},false);
validate('MemoryScopeDelete',{deviceId:'kufr-001',expectedEpoch:3},true);
validate('MemoryScopeDelete',{deviceId:'kufr-001',expectedEpoch:-1},false);
validate('MemoryScopeDelete',{deviceId:'kufr-001',expectedEpoch:3,text:'private'},false);
validate('MemoryDeletionResult',{localDeletion:'complete',remoteDeletion:'pending'},true);
validate('EngineUpgradeProblem',{type:'about:blank',title:'Gone',status:410,
  code:'conversation_engine_upgrade_required',traceId:'trace-1'},true);
validate('EngineUpgradeProblem',{type:'about:blank',title:'Accepted',status:202,
  code:'conversation_engine_upgrade_required',traceId:'trace-1'},false);
