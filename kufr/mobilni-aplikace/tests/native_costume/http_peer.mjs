import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';
import assert from 'node:assert/strict';

const states = new Map();
const tokens = new Map();
const fixture = Buffer.from([255, 216, 255, 218, 0, 2, 255, 217]);
const json = (response, status, value) => {
  response.writeHead(status, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify(value));
};
const wav = Buffer.alloc(46);
wav.write('RIFF'); wav.writeUInt32LE(38, 4); wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(32000, 24); wav.writeUInt32LE(64000, 28);
wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(2, 40);
const server = createServer(async (request, response) => {
  try {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const bytes = Buffer.concat(chunks);
    const body = request.headers['content-type'] === 'application/json' ? JSON.parse(bytes) : {};
    const suppliedToken = request.headers.authorization?.replace('Bearer ', '');
    const device = request.url === '/v1/device-access/token' ? body.deviceId ?? suppliedToken?.replace(/^synthetic-/, '')
      : tokens.get(suppliedToken) ?? suppliedToken?.replace(/^access-/, '');
    if (request.url === '/v1/device-access/token') {
      if (!states.has(device)) states.set(device, { events: [], operations: new Map(), creations: new Map(), createdIds: [], deletedIds: [],
        photoConsents: [], manualIds: [], openingIds: [], pendingImage: false, deleteMutations: new Map(),
        deleteKeys: [], corrections: new Map(), correctionKeys: [], tokenRequests: 0, tokenExpired: false });
      const state = states.get(device);
      state.tokenRequests++;
      if (device.includes('refresh-failed') && state.tokenRequests === 2) {
        state.events.push('renew-failed');
        json(response, 503, { code: 'access_unavailable' }); return;
      }
      state.activeToken = state.tokenRequests === 1 ? `access-${device}` : `access-${device}-renewed-${state.tokenRequests}`;
      state.tokenExpired = false;
      tokens.set(state.activeToken, device);
      if (state.tokenRequests > 1) state.events.push('renew');
      json(response, 200, { deviceId: device, accessToken: state.activeToken }); return;
    }
    const state = states.get(device);
    if (!state) { json(response, 403, {}); return; }
    if (request.url === '/receipt') {
      json(response, 200, { events: state.events, photoConsent: state.photoConsents[0],
        photoConsents: state.photoConsents, createdIds: state.createdIds, deletedIds: state.deletedIds,
        manualIds: state.manualIds, openingIds: state.openingIds, createKeys: [...state.creations.keys()],
        deleteKeys: state.deleteKeys, correctionKeys: state.correctionKeys, tokenRequests: state.tokenRequests }); return;
    }
    const deletingConversation = request.method === 'DELETE' && request.url.startsWith('/v1/conversations/');
    if (!deletingConversation && (state.tokenExpired || suppliedToken !== state.activeToken)) {
      json(response, 401, {}); return;
    }
    if (request.url === '/v1/diagnostics') {
      json(response, 200, { voiceMode: device.includes('chain') ? 'fake' : 'openai',
        dialogMode: device.includes('chain') ? 'fake' : 'responses',
        catalogVersion: 'ollivander-private-party-v1', audioFormat: 'pcm16-mono-32000-wav' }); return;
    }
    if (request.url === '/v1/conversations') {
      assert.equal(body.deviceId, device);
      const key = request.headers['idempotency-key'];
      assert.match(key, /^[0-9a-f-]{36}$/);
      if (state.creations.has(key)) {
        state.events.push('create-replay');
        json(response, 200, { conversationId: state.creations.get(key) }); return;
      }
      assert(!state.pendingImage, 'A fresh conversation started before image cleanup barrier');
      assert(state.createdIds.every(id => state.deleteMutations.get(id)?.confirmed), 'An orphan blocks the next Start');
      state.boot = body.bootId; state.conversation = crypto.randomUUID();
      state.creations.set(key, state.conversation);
      state.createdIds.push(state.conversation); state.photoConsents.push(body.consent.image);
      state.events.push('create');
      const createdId = state.conversation;
      if (device.includes('cancel-fresh') && state.createdIds.length === 2) {
        setTimeout(() => json(response, 200, { conversationId: createdId }), 600); return;
      }
      json(response, 200, { conversationId: createdId }); return;
    }
    const conversation = request.url.split('/')[3];
    if (deletingConversation) {
      assert(state.createdIds.includes(conversation));
      const key = request.headers['idempotency-key'];
      const previous = state.deleteMutations.get(conversation);
      if (previous && previous.key !== key) { json(response, 404, { code: 'conversation_not_found' }); return; }
      state.deleteKeys.push({ conversation, key, token: suppliedToken });
      if (state.tokenExpired || suppliedToken !== state.activeToken) {
        state.events.push('expired-token');
        json(response, 401, {}); return;
      }
      const mutation = previous ?? { key, attempts: 0, confirmed: false };
      state.deleteMutations.set(conversation, mutation);
      mutation.attempts++;
      state.events.push(previous ? 'delete-replay' : 'delete');
      // Match the backend: remove the ID immediately, even before its barrier
      // can acknowledge 204. Only the original mutation key can be replayed.
      if (!previous) state.deletedIds.push(conversation);
      if (device.includes('cleanup-timeout')) { json(response, 503, { code: 'image_cancel_pending' }); return; }
      const failedBarrier = device.includes('delete-retry') && conversation === state.createdIds[0]
        || device.includes('fresh-orphan') && conversation === state.createdIds[1];
      if (failedBarrier && mutation.attempts <= (device.includes('renewed-pending') ? 4 : 2)) {
        if (device.includes('expired') && mutation.attempts === 2) state.tokenExpired = true;
        json(response, 503, { code: 'image_cancel_pending' }); return;
      }
      const complete = () => {
        state.pendingImage = false; mutation.confirmed = true;
        response.writeHead(204); response.end();
      };
      if (device.includes('cancel-cleanup') && conversation === state.createdIds[0]) { setTimeout(complete, 600); return; }
      complete(); return;
    }
    if (request.url.endsWith('/image') || request.url.endsWith('/costume-context')) {
      const photo = request.url.endsWith('/image');
      assert.equal(request.method, photo ? 'POST' : 'PUT');
      assert.equal(request.headers['content-type'], photo ? 'image/jpeg' : 'application/json');
      for (const header of ['idempotency-key', 'client-turn-id', 'boot-id'])
        assert.match(request.headers[header], /^[0-9a-f-]{36}$/);
      assert.equal(request.headers['boot-id'], state.boot);
      assert.equal(request.headers['generation'], '0');
      assert.equal(request.headers['device-revision'], '0');
      assert.equal(request.headers['catalog-version'], 'ollivander-private-party-v1');
      assert.equal(conversation, state.conversation);
      if (!photo) {
        const key = request.headers['idempotency-key'];
        const previous = state.corrections.get(key);
        state.correctionKeys.push(key);
        if (previous) {
          assert.deepEqual(bytes, previous.body);
          assert.equal(request.headers['client-turn-id'], previous.turn);
          assert.equal(request.headers['boot-id'], previous.boot);
          assert.equal(conversation, previous.conversation);
          state.events.push('manual-replay');
          if (device.includes('manual-repeat-failure') || device.includes('fresh-orphan')) {
            json(response, 200, { operationId: 'malformed' }); return;
          }
          json(response, 200, previous.operation); return;
        }
      }
      state.events.push(photo ? 'image' : 'manual');
      if (photo) { assert(state.photoConsents[0]); assert.deepEqual(bytes, fixture); state.pendingImage = true; }
      else { state.manualIds.push(conversation); assert.equal(body.description, 'Host v modrém plášti'); }
      if (photo && device.includes('lost')) { response.destroy(); return; }
      if (photo && device.includes('malformed')) { json(response, 202, { operationId: 'malformed', kind: 'image', status: 'pending' }); return; }
      if (photo && device.includes('failed')) { json(response, 503, { code: 'ai_unavailable' }); return; }
      const id = crypto.randomUUID();
      const operation = { operationId: id, kind: photo ? 'image' : 'costume', status: 'completed',
        correlation: { deviceId: device, bootId: state.boot, conversationId: conversation,
          clientTurnId: request.headers['client-turn-id'], generation: 0, deviceRevision: 0 },
        result: { description: photo ? 'Modrý plášť' : body.description, possibleCharacter: '', uncertainty: 'high',
          source: photo ? 'fake' : 'manual', expiresAt: new Date(Date.now() + 120000).toISOString() } };
      state.operations.set(id, operation);
      if (!photo) {
        state.corrections.set(request.headers['idempotency-key'], { operation, body: bytes,
          turn: request.headers['client-turn-id'], boot: request.headers['boot-id'], conversation });
        if (device.includes('manual-lost')) { response.destroy(); return; }
        if (device.includes('manual-malformed') || device.includes('manual-repeat-failure') || device.includes('fresh-orphan')) {
          json(response, 200, { ...operation, correlation: { ...operation.correlation, generation: 1 } }); return;
        }
      }
      json(response, photo ? 202 : 200, photo ? { operationId: id, kind: 'image', status: 'pending' } : operation);
      return;
    }
    if (request.url.endsWith('/opening') || request.url.endsWith('/speech')) {
      assert.equal(conversation, state.conversation);
      assert(!state.deletedIds.includes(conversation));
      assert(!state.pendingImage, 'Opening started while image provider still busy');
      const opening = request.url.endsWith('/opening');
      if (opening) { state.openingIds.push(conversation); state.turn = body.clientTurnId; state.events.push('opening'); }
      const id = crypto.randomUUID();
      state.operations.set(id, { operationId: id, kind: opening ? 'turn' : 'speech', status: 'completed',
        correlation: { deviceId: device, bootId: state.boot, conversationId: conversation,
          clientTurnId: state.turn, generation: 0, deviceRevision: 0 },
        result: opening ? { reply: 'synthetic', decision: 'no_selection', proposal: null }
          : { outputTarget: 'phone', audioId: crypto.randomUUID() } });
      json(response, 202, { operationId: id }); return;
    }
    if (request.url.startsWith('/v1/audio/')) {
      response.writeHead(200, { 'Content-Type': 'audio/wav' }); response.end(wav); return;
    }
    if (request.url.startsWith('/v1/operations/')) {
      const operation = state.operations.get(request.url.split('/').at(-1));
      assert(operation);
      state.events.push(operation.kind === 'image' ? 'poll' : `poll-${operation.kind}`);
      if (device === 'debug-chain-cancel' && operation.kind === 'image') return;
      if (operation.kind === 'image') state.pendingImage = false;
      json(response, 200, operation); return;
    }
    state.events.push('unexpected'); json(response, 404, {});
  } catch (error) { console.error(error); json(response, 500, { code: 'peer_validation_failed' }); }
});
server.listen(0, '127.0.0.1', () => writeFileSync(process.argv[2], `http://127.0.0.1:${server.address().port}`));
