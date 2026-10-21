import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const document = JSON.parse(await readFile(new URL('./show-openapi-v1.json', import.meta.url), 'utf8'));
const deployment = JSON.parse(await readFile(new URL('../web/deploy/backend-route-contract.json', import.meta.url), 'utf8'));
const ajv = new Ajv2020({ strict: true, allErrors: true });
addFormats(ajv);
for (const [name, schema] of Object.entries(document.components.schemas)) {
  ajv.addSchema(JSON.parse(JSON.stringify(schema).replaceAll('#/components/schemas/', 'urn:show:')), `urn:show:${name}`);
}
for (const name of Object.keys(document.components.schemas)) assert.ok(ajv.getSchema(`urn:show:${name}`));
for (const [method, rawPath, source] of deployment.routes.filter(row => row[2].startsWith('Features/Show/'))) {
  const path = rawPath.replace(/:(?:guid)(?=})/g, '');
  const operation = document.paths[path]?.[method.toLowerCase()];
  assert.ok(operation, `missing ${method} ${path}`);
  assert.equal(operation['x-source'], source);
  const authority = source === 'Features/Show/ShowDeviceCuesEndpoints.cs' ? 'DeviceToken' : 'ServerAdmin';
  assert.ok(operation.security.some(scheme => Object.hasOwn(scheme, authority)));
}
assert.deepEqual(document.paths['/guest/v1/chronicle'].get.security, []);
assert.deepEqual(document.paths['/guest/v1/duels/{id}/choices'].post.security, [{ DuelSideGrant: [] }]);
const validate = (name, value, expected) => {
  const check = ajv.getSchema(`urn:show:${name}`);
  assert.equal(check(value), expected, `${name}: ${JSON.stringify(check.errors)}`);
};
const previewId = '11111111-1111-4111-8111-111111111111';
validate('AmbientPreviewRequest', { operationId: previewId }, true);
validate('AmbientPreviewRequest', { operationId: '00000000-0000-0000-0000-000000000000' }, false);
validate('AmbientPreviewRequest', { operationId: previewId, text: 'arbitrary provider input' }, false);
const preview = { status: 'ready', operationId: previewId, lineId: 'ambient-one', text: 'Authored cue.', synthetic: false, wavBase64: 'UklGRg==' };
validate('AmbientPreviewResponse', preview, true);
validate('AmbientPreviewResponse', { ...preview, wavBase64: null }, false);
validate('AmbientPreviewResponse', { ...preview, wavBase64: 'a'.repeat(682729) }, false);
validate('AmbientPreviewResponse', { ...preview, status: 'pending', wavBase64: null }, true);
for (const [path, method] of [['/v1/show/devices/{deviceId}/ambient-previews', 'post'], ['/v1/show/devices/{deviceId}/ambient-previews/{operationId}', 'get']]) {
  const operation = document.paths[path][method];
  assert.equal(operation['x-account-permission'], 'operate');
  assert.ok(operation.security.some(scheme => Object.hasOwn(scheme, 'AccountSession')
    && (method === 'get' || Object.hasOwn(scheme, 'AccountCsrf'))));
}
const command = { commandId: 'cmd-1', expectedStateRevision: 7, kind: 'override',
  sessionId: 'visit-1', sessionGeneration: 'generation-1', value: '2', ttlSeconds: 30 };
validate('ShowCommand', command, true);
validate('ShowCommand', { ...command, force: true }, false);
validate('ShowCommand', { ...command, ttlSeconds: 0 }, false);
validate('ShowCommand', { ...command, expectedStateRevision: -1 }, false);
validate('ShowCommand', { ...command, kind: 'gpio' }, false);
const duel = { eveningId: 'evening-1', leftIdentityId: 'identity-a', rightIdentityId: 'identity-b',
  mode: 'autoplay', consentConfirmed: true, operationKey: 'request-1' };
validate('DuelCreate', duel, true);
validate('DuelCreate', { ...duel, consentConfirmed: false }, false);
validate('DuelChoice', { side: 'left', round: 1, revision: 2, action: 'shield', operationKey: 'move-1' }, true);
validate('DuelChoice', { side: 'left', round: 4, revision: 2, action: 'shield', operationKey: 'move-1' }, false);
const outcome = { operationId: '11111111-1111-4111-8111-111111111111', deviceId: 'device-a',
  bootId: '22222222-2222-4222-8222-222222222222', generation: 1,
  localEveningId: '33333333-3333-4333-8333-333333333333', eveningId: null,
  sourceMode: 'offline_scripted', compartment: 2, catalogVersion: 'ollivander-private-party-v1',
  startedAtDeviceMs: 1000, completedAtDeviceMs: 12000,
  timeConfidence: 'boot_monotonic', outcome: 'completed_by_timing' };
validate('OfflineOutcomeRequest', { schemaVersion: 1, outcome }, true);
for (const operationEpoch of ['0', '9007199254740993', '18446744073709551615']) {
  validate('OfflineOutcomeRequest', { schemaVersion: 1, outcome: { ...outcome, operationEpoch } }, true);
  validate('ImportedOfflineOutcome', { schemaVersion: 1, operationId: outcome.operationId,
    eveningId: outcome.localEveningId, locator: outcome.operationId, duplicate: true, operationEpoch }, true);
}
for (const operationEpoch of [null, 1, '', '01', '+1', '1.0', '18446744073709551616']) {
  validate('OfflineOutcomeRequest', { schemaVersion: 1, outcome: { ...outcome, operationEpoch } }, false);
}
validate('OfflineOutcomeRequest', { schemaVersion: 1, outcome: { ...outcome, outcome: 'pending' } }, false);
validate('OfflineOutcomeRequest', { schemaVersion: 1, outcome: { ...outcome, eveningId: 'active' } }, false);
console.log('Show HTTP contract: source routes, scopes, bounded commands, duel moves and offline outcomes passed.');
const ack = { bootId: '11111111-1111-4111-8111-111111111111', generation: 1,
  deviceRevision: 7, outcome: 'completed', evidence: 'playback_finished' };
validate('DeviceCueAck', ack, true);
validate('DeviceCueAck', { ...ack, force: true }, false);
validate('DeviceCueAck', { ...ack, evidence: 'model_guessed' }, false);
