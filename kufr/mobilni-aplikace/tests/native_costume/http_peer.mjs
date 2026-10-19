import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';

const states = new Map();
const inventory = { schemaVersion: 1, primaryEngine: 'gpt-live', engines: ['gpt-live', 'realtime'].map(engine => ({
  engine, model: engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', ready: true,
  voices: ['cedar', 'marin', 'coral'], speed: { supported: engine === 'realtime', min: 0.8, max: 1.2, default: 1 },
  inputModes: ['audio'], cameraSources: ['phone', 'suitcase'],
})) };
const json = (response, status, value) => response.writeHead(status, { 'Content-Type': 'application/json' }).end(JSON.stringify(value));
const fixture = Buffer.from([255, 216, 255, 218, 0, 2, 255, 217]);
const server = createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const bytes = Buffer.concat(chunks);
  const body = request.headers['content-type'] === 'application/json' && bytes.length ? JSON.parse(bytes.toString()) : {};
  const authorization = request.headers.authorization ?? '';
  if (request.url === '/v1/device-access/token') {
    const device = authorization.slice('Bearer synthetic-'.length);
    if (!states.has(device)) states.set(device, { device, events: [], barrier: 0, operations: new Map() });
    json(response, 200, { deviceId: device, accessToken: `access-${device}` }); return;
  }
  const device = authorization.slice('Bearer access-'.length);
  const state = states.get(device);
  if (!state) { json(response, 401, {}); return; }
  if (request.url === '/receipt') { json(response, 200, { events: state.events, barrier: state.barrier, visualRevision: state.visualRevision ?? null }); return; }
  if (request.url === '/v1/diagnostics') { json(response, 200, { catalogVersion: 'ollivander-private-party-v1', engines: inventory }); return; }
  if (request.url === '/v1/conversations') {
    state.conversation = crypto.randomUUID(); state.boot = body.bootId; state.events.push('create');
    json(response, 200, { conversationId: state.conversation }); return;
  }
  if (request.url === `/v1/conversations/${state.conversation}` && request.method === 'DELETE') {
    state.events.push('delete'); response.writeHead(204).end(); return;
  }
  if (request.url === `/v1/conversations/${state.conversation}/image`) {
    assert.equal(request.headers['boot-id'], state.boot);
    const revision = Number(request.headers['image-revision']);
    assert.ok(Number.isSafeInteger(revision) && revision >= 1);
    if (request.method === 'DELETE') {
      state.events.push(`revoke-${revision}`); state.barrier = Math.max(state.barrier, revision);
      state.visualRevision = undefined; response.writeHead(204).end(); return;
    }
    assert.equal(request.method, 'POST');
    assert.equal(request.headers['image-consent'], 'true'); assert.equal(request.headers['image-source'], 'phone');
    assert.match(request.headers['image-id'], /^[a-f0-9-]{36}$/); assert.ok(Number.isFinite(Date.parse(request.headers['captured-at'])));
    assert.deepEqual(bytes, fixture); state.events.push(`image-${revision}`);
    if (device.includes('lost')) { response.destroy(); return; }
    if (device.includes('failed')) { json(response, 503, { code: 'image_unavailable' }); return; }
    if (device.includes('race')) await new Promise(resolve => setTimeout(resolve, 200));
    if (revision <= state.barrier) { json(response, 409, { code: 'stale_image_revision' }); return; }
    const id = crypto.randomUUID();
    const operation = { operationId: id, kind: 'image', status: device.includes('blocked') && revision === 1 ? 'pending' : 'completed',
      correlation: { deviceId: device, bootId: state.boot, conversationId: state.conversation,
        clientTurnId: request.headers['client-turn-id'], generation: 0, deviceRevision: 0 },
      result: { description: 'Modrý plášť', possibleCharacter: '', uncertainty: 'high', source: 'fake',
        expiresAt: new Date(Date.now() + 30 * 60_000).toISOString() }, revision };
    state.operations.set(id, operation); json(response, 202, { operationId: id, kind: 'image', status: 'pending' }); return;
  }
  if (request.url.startsWith('/v1/operations/')) {
    const operation = state.operations.get(request.url.split('/').at(-1));
    assert.ok(operation); state.events.push(`poll-${operation.revision}`);
    if (operation.status === 'completed' && operation.revision > state.barrier) state.visualRevision = operation.revision;
    json(response, 200, operation); return;
  }
  if (request.url === `/v1/conversations/${state.conversation}/costume-context`) {
    state.events.push('manual');
    json(response, 200, { operationId: crypto.randomUUID(), kind: 'costume', status: 'completed',
      correlation: { deviceId: device, bootId: state.boot, conversationId: state.conversation,
        clientTurnId: request.headers['client-turn-id'], generation: 0, deviceRevision: 0 },
      result: { description: body.description, uncertainty: 'high', source: 'manual', expiresAt: new Date(Date.now() + 30 * 60_000).toISOString() } }); return;
  }
  throw new Error(`Unexpected retained media route ${request.url}`);
});
server.listen(0, '127.0.0.1', () => writeFileSync(process.argv[2], `http://127.0.0.1:${server.address().port}`));
