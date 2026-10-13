import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { request } from 'node:http';
import { createServer } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const credentialA = randomBytes(32).toString('base64');
const credentialB = randomBytes(32).toString('base64');
const adminCredential = randomBytes(32).toString('base64');
const logCredential = randomBytes(32).toString('base64');
const sha256 = value => createHash('sha256').update(value).digest('hex');
const port = await new Promise((resolve, reject) => {
  const server = createServer();
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => {
    const selectedPort = server.address().port;
    server.close(() => resolve(selectedPort));
  });
});

const baseUrl = `http://127.0.0.1:${port}`;
const databaseDirectory = await mkdtemp(join(tmpdir(), 'kufr-backend-smoke-'));
const serverEnvironment = {
    ...process.env,
    OPENAI_API_KEY: '', // offline smoke never uses an ambient provider credential
    Backend__Memory__Enabled: 'false',
    Backend__Memory__CloudIndexingEnabled: 'false',
    Backend__Memory__LongTermPersonalEnabled: 'false',
    Backend__Memory__PersonalStoreId: '',
    Backend__Memory__MotifStoreId: '',
    Devices__0__DeviceId: 'device-a',
    Devices__0__CredentialSha256: sha256(credentialA),
    Devices__1__DeviceId: 'device-b',
    Devices__1__CredentialSha256: sha256(credentialB),
    Backend__DatabasePath: join(databaseDirectory, 'journal.sqlite'),
    Backend__Dialog__Mode: 'fake',
    Backend__Voice__Mode: 'fake',
    Backend__Admin__CredentialSha256: sha256(adminCredential),
    Backend__Logs__CredentialSha256: sha256(logCredential),
    ASPNETCORE_ENVIRONMENT: 'Production'
};

let serverOutput = '';
let server;
function startServer() {
  server = spawn('dotnet', [process.env.KUFR_BACKEND_DLL || 'bin/Debug/net10.0/backend.dll', '--urls', baseUrl], {
    cwd: new URL('..', import.meta.url),
    env: serverEnvironment,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });
  for (const stream of [server.stdout, server.stderr]) {
    stream.on('data', chunk => { serverOutput += chunk.toString(); });
  }
}

async function stopServer() {
  if (server?.exitCode === null) {
    const exited = new Promise(resolve => server.once('exit', resolve));
    server.kill();
    await exited;
  }
}

async function waitForServer() {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (server.exitCode !== null) throw new Error(`Backend exited: ${serverOutput}`);
    try {
      await fetch(`${baseUrl}/`);
      return;
    } catch {
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  }
  throw new Error(`Backend did not start: ${serverOutput}`);
}

async function call(path, { credential, token, key, body } = {}) {
  const headers = {};
  if (credential || token) headers.Authorization = `Bearer ${credential ?? token}`;
  if (key) headers['Idempotency-Key'] = key;
  if (body) headers['Content-Type'] = 'application/json';
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'POST',
    headers,
    body: body && JSON.stringify(body)
  });
  const result = await response.json();
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return { status: response.status, result };
}

async function read(path, token) {
  const response = await fetch(`${baseUrl}${path}`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return { status: response.status, result: await response.json() };
}

async function activateEvening(path, token, key, body) {
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'PUT',
    headers: {
      Authorization: `Bearer ${token}`,
      'Idempotency-Key': key,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(body)
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return { status: response.status, result: await response.json() };
}

async function remove(path, token, key, body) {
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'DELETE',
    headers: {
      Authorization: `Bearer ${token}`,
      'Idempotency-Key': key
    },
    body
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return response.status;
}

function makeWav(sampleBytes = 2) {
  const wav = Buffer.alloc(44 + sampleBytes);
  wav.write('RIFF', 0);
  wav.writeUInt32LE(wav.length - 8, 4);
  wav.write('WAVEfmt ', 8);
  wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20);
  wav.writeUInt16LE(1, 22);
  wav.writeUInt32LE(32000, 24);
  wav.writeUInt32LE(64000, 28);
  wav.writeUInt16LE(2, 32);
  wav.writeUInt16LE(16, 34);
  wav.write('data', 36);
  wav.writeUInt32LE(sampleBytes, 40);
  return wav;
}

async function transcribe(path, token, key, fields, wav = makeWav()) {
  const form = new FormData();
  for (const [name, value] of Object.entries(fields)) {
    form.append(name, String(value));
  }
  form.append('wav', new Blob([wav], { type: 'audio/wav' }), 'capture.wav');
  const response = await fetch(`${baseUrl}${path}`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Idempotency-Key': key },
    body: form
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return { status: response.status, result: await response.json() };
}

async function downloadAudio(path, token) {
  const response = await fetch(`${baseUrl}${path}`, {
    headers: { Authorization: `Bearer ${token}` }
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return {
    status: response.status,
    contentType: response.headers.get('content-type'),
    sampleRate: response.headers.get('x-audio-sample-rate-hz'),
    channels: response.headers.get('x-audio-channels'),
    body: response.status === 200 ? Buffer.from(await response.arrayBuffer()) : await response.json()
  };
}


const costumeJpeg = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooooA//2Q==', 'base64');
async function imageRequest(id, token, headers, jpeg = costumeJpeg, correction = null) {
  const response = await fetch(`${baseUrl}/v1/conversations/${id}/${correction === null ? 'image' : 'costume-context'}`, {
    method: correction === null ? 'POST' : 'PUT',
    headers: { ...headers, ...(token ? { Authorization: `Bearer ${token}` } : {}),
      'Content-Type': correction === null ? 'image/jpeg' : 'application/json' },
    body: correction === null ? jpeg : JSON.stringify({ description: correction })
  });
  assert.equal(response.headers.get('cache-control'), 'no-store');
  return { status: response.status, result: await response.json(), connection: response.headers.get('connection') };
}

// Do not eagerly upload after a declared-size rejection: Undici can report
// EPIPE while it is still writing instead of exposing the server's early 413.
async function declaredOversizeRequest(path, headers, byteCount) {
  return await new Promise((resolve, reject) => {
    const oversizedRequest = request(`${baseUrl}${path}`, {
      method: 'POST',
      headers: { ...headers, 'Content-Length': byteCount, Expect: '100-continue' }
    }, response => {
      const chunks = [];
      let received = 0;
      response.on('error', reject);
      response.on('data', chunk => {
        received += chunk.length;
        if (received > 4096) { response.destroy(new Error('Oversized rejection response')); return; }
        chunks.push(chunk);
      });
      response.on('end', () => {
        try {
          assert.equal(response.headers['cache-control'], 'no-store');
          resolve({ status: response.statusCode,
            result: JSON.parse(Buffer.concat(chunks).toString('utf8')),
            connection: response.headers.connection });
        } catch (error) { reject(error); }
        finally { oversizedRequest.destroy(); }
      });
    });
    oversizedRequest.on('error', reject);
    oversizedRequest.on('continue', () => oversizedRequest.destroy(
      new Error('Declared oversized body was not rejected before upload')));
    oversizedRequest.setTimeout(5000, () => oversizedRequest.destroy(new Error('Oversized request timed out')));
    oversizedRequest.flushHeaders();
  });
}

startServer();
try {
  await waitForServer();
  const health = await fetch(`${baseUrl}/healthz`);
  assert.equal(health.status, 200);
  assert.deepEqual(await health.json(), { status: 'ok' });

  assert.equal((await call('/v1/device-access/token')).status, 401);
  assert.equal((await call('/v1/device-access/token', { credential: 'wrong' })).status, 401);
  const issuedA = await call('/v1/device-access/token', { credential: credentialA });
  const issuedB = await call('/v1/device-access/token', { credential: credentialB });
  assert.equal(issuedA.status, 200);
  assert.equal(issuedA.result.deviceId, 'device-a');
  assert.equal(issuedA.result.expiresInSeconds, 900);
  assert.equal(issuedB.status, 200);

  const body = {
    deviceId: 'device-a',
    bootId: randomUUID(),
    generation: 1,
    catalogVersion: 'fixture-1',
    locale: 'cs-CZ',
    consent: { audio: true, image: false }
  };
  const key = randomUUID();
  const tokenA = issuedA.result.accessToken;
  const tokenB = issuedB.result.accessToken;
  for (const token of [undefined, credentialA, tokenA]) {
    assert.equal((await read('/v1/admin/logs', token)).status, 401);
    assert.equal((await read('/v1/admin/logs/status', token)).status, 401);
  }
  assert.equal((await read('/v1/admin/modes', logCredential)).status, 401);
  assert.equal((await call('/v1/device-access/token', { credential: logCredential })).status, 401);
  assert.equal((await read('/v1/admin/logs/status', logCredential)).status, 200);
  for (const query of ['limit=201', 'after=-1', 'level=debug', 'traceId=private-secret', 'limit=1&limit=2', 'token=private-secret']) {
    assert.equal((await read(`/v1/admin/logs?${query}`, logCredential)).status, 400);
  }
  const failed = await fetch(`${baseUrl}/v1/private-secret?text=private-query-secret`, { method: 'POST' });
  const requestId = failed.headers.get('x-request-id');
  assert.match(requestId, /^[0-9a-f-]{36}$/);
  const tracePage = (await read(`/v1/admin/logs?traceId=${requestId}`, logCredential)).result;
  assert.equal(tracePage.entries.length, 1);
  assert.equal(tracePage.entries[0].route, '(unmatched)');
  assert.equal(tracePage.entries[0].status, 404);
  const firstLogPage = (await read('/v1/admin/logs?limit=2', adminCredential)).result;
  assert.equal(firstLogPage.entries.length, 2);
  assert.equal(firstLogPage.hasMore, false);
  assert.ok(firstLogPage.entries[0].id < firstLogPage.entries[1].id);
  const quietPage = (await read(`/v1/admin/logs?after=${firstLogPage.nextCursor}`, logCredential)).result;
  assert.equal(quietPage.entries.length, 0, 'successful log reads do not create log traffic');

  assert.equal((await read('/v1/admin/modes')).status, 401);
  assert.equal((await read('/v1/admin/modes', credentialA)).status, 401);
  assert.equal((await read('/v1/admin/modes', tokenA)).status, 401);
  const modes = (await read('/v1/admin/modes', adminCredential)).result;
  assert.equal(modes.lifetime, 'until_restart');
  assert.equal(modes.dialogMode, 'fake');
  assert.equal(modes.activeConversations, 0);
  assert.equal(modes.personalityId, 'party-original');
  assert.equal(modes.personalityLifetime, 'persistent');
  assert.deepEqual(modes.personalities.map(item => item.id), ['party-original', 'ollivander-trunk']);
  assert.ok(modes.personalities.every(item => /^[a-f0-9]{64}$/.test(item.promptSha256)));
  assert.equal(modes.personalities[1].promptSha256, '99a72891209ef2bf5ad5a0120a7998c5a08bf46317f38812fb1d3816a5c3099d');
  const changeModes = async (body, credential = adminCredential) => {
    const response = await fetch(`${baseUrl}/v1/admin/modes`, {
      method: 'PUT', headers: { Authorization: `Bearer ${credential}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    assert.equal(response.headers.get('cache-control'), 'no-store');
    return { status: response.status, result: await response.json() };
  };
  const modeBody = { expectedRevision: modes.revision, dialogMode: 'fake', voiceMode: 'fake' };
  assert.equal((await changeModes(modeBody, tokenA)).status, 401);
  assert.equal((await changeModes(modeBody)).status, 200);
  assert.equal((await changeModes({ ...modeBody, dialogMode: 'unsupported' })).status, 400);
  assert.equal((await changeModes({ ...modeBody, expectedRevision: randomUUID() })).status, 409);
  assert.equal((await changeModes({ ...modeBody, voiceMode: 'openai' })).result.code, 'provider_unavailable');
  assert.equal((await changeModes({ ...modeBody, personalityId: 'unknown' })).result.code, 'invalid_personality');
  assert.equal((await changeModes({ ...modeBody, personalityId: 'ollivander-trunk' }, tokenA)).status, 401);
  const selectedPersonality = await changeModes({ ...modeBody, personalityId: 'ollivander-trunk' });
  assert.equal(selectedPersonality.status, 200);
  assert.equal(selectedPersonality.result.personalityId, 'ollivander-trunk');
  assert.equal((await changeModes(modeBody)).status, 409, 'personality changes share the mode revision');
  const restoredPersonality = await changeModes({ ...modeBody, expectedRevision: selectedPersonality.result.revision, personalityId: 'party-original' });
  assert.equal(restoredPersonality.status, 200);
  const adminToken = await call('/v1/device-access/token', { credential: adminCredential });
  assert.equal(adminToken.result.deviceId, 'debug-server-admin');
  const virtualDevice = adminToken.result.deviceId;
  const virtualToken = adminToken.result.accessToken;
  const virtualBody = { ...body, deviceId: virtualDevice };
  const virtualSession = await call('/v1/conversations', {
    token: virtualToken, key: randomUUID(), body: virtualBody,
  });
  assert.equal(virtualSession.status, 201);
  const virtualId = virtualSession.result.conversationId;
  assert.equal((await changeModes({ ...modeBody, expectedRevision: restoredPersonality.result.revision,
    personalityId: 'ollivander-trunk' })).result.code, 'conversations_active');
  const virtualTurn = {
    clientTurnId: randomUUID(), bootId: virtualBody.bootId, generation: 1,
    readyMask: 0b101010, deviceRevision: 4, catalogVersion: virtualBody.catalogVersion,
  };
  const virtualOpening = await call(`/v1/conversations/${virtualId}/opening`, {
    token: virtualToken, key: randomUUID(), body: virtualTurn,
  });
  assert.equal(virtualOpening.status, 202);
  let virtualResult = (await read(`/v1/operations/${virtualOpening.result.operationId}`, virtualToken)).result.result;
  assert.equal(virtualResult.decision, 'ask');
  for (const answer of ['Jsem Eva', 'Ráda kreslím', 'Zkusila jsem tanec']) {
    const next = await call(`/v1/conversations/${virtualId}/turns`, {
      token: virtualToken, key: randomUUID(),
      body: { ...virtualTurn, clientTurnId: randomUUID(), text: answer },
    });
    assert.equal(next.status, 202);
    virtualResult = (await read(`/v1/operations/${next.result.operationId}`, virtualToken)).result.result;
    assert.equal(virtualResult.decision, 'ask');
  }
  const virtualProposal = await call(`/v1/conversations/${virtualId}/turns`, {
    token: virtualToken, key: randomUUID(),
    body: { ...virtualTurn, clientTurnId: randomUUID(), text: 'Přátelé říkají, že jsem zvědavá' },
  });
  assert.equal(virtualProposal.status, 202);
  virtualResult = (await read(`/v1/operations/${virtualProposal.result.operationId}`, virtualToken)).result.result;
  assert.equal(virtualResult.decision, 'propose');
  assert.ok([2, 4, 6].includes(virtualResult.proposal.compartment));
  assert.equal((await call(`/v1/conversations/${virtualId}/turns`, {
    token: virtualToken, key: randomUUID(),
    body: { ...virtualTurn, clientTurnId: randomUUID(), text: 'Ještě něco' },
  })).status, 409);
  assert.equal(await remove(`/v1/conversations/${virtualId}`, virtualToken, randomUUID()), 204);
  const emptySession = await call('/v1/conversations', {
    token: virtualToken, key: randomUUID(), body: virtualBody,
  });
  assert.equal(emptySession.status, 201);
  const emptyOpening = await call(`/v1/conversations/${emptySession.result.conversationId}/opening`, {
    token: virtualToken, key: randomUUID(),
    body: { ...virtualTurn, clientTurnId: randomUUID(), readyMask: 0 },
  });
  assert.equal(emptyOpening.status, 202);
  assert.equal((await read(`/v1/operations/${emptyOpening.result.operationId}`, virtualToken)).result.result.decision,
    'no_selection');
  assert.equal(await remove(
    `/v1/conversations/${emptySession.result.conversationId}`, virtualToken, randomUUID()), 204);
  assert.equal((await read('/v1/admin/modes', adminToken.result.accessToken)).status, 401);
  assert.equal((await read('/v1/diagnostics')).status, 401);
  assert.equal((await read('/v1/diagnostics', credentialA)).status, 401);
  assert.deepEqual((await read('/v1/diagnostics', tokenA)).result, {
    voiceMode: 'fake', dialogMode: 'fake', catalogVersion: 'ollivander-private-party-v1',
    audioFormat: 'pcm16-mono-32000-wav',
    liveActorMode: 'LegacyBackendScripted', personalityId: 'party-original',
    liveDecisionModel: 'gpt-6.1-sol', liveTerminalQuietMs: 4000,
    characterEnabled: false, characterTone: 'Adult', characterFinaleProfile: 'Blocked',
    characterVersion: 'dv01-v1', characterVisualRoast: 'permission_pending',
    characterNamedFinale: 'blocked', characterAudio: 'unsegmented_live_stream', characterEventReady: false,
    memoryEnabled: false, cloudIndexingEnabled: false, longTermPersonalEnabled: false,
    memoryProvider: { provider: 'openai_vector_store', configured: false, degraded: false,
      configurationStatus: 'disabled', lastVerifiedAt: null, pending: 0, failed: 0, deleting: 0 },
  });
  assert.equal((await read('/v1/admin/memory', tokenA)).status, 401);
  assert.equal((await read('/v1/admin/memory', logCredential)).status, 401);
  assert.equal((await read('/v1/admin/memory', adminCredential)).result.configurationStatus, 'disabled');
  assert.equal((await call('/v1/admin/memory/verify', { token: adminCredential })).status, 503);
  assert.equal((await activateEvening('/v1/admin/memory/guest', adminCredential, randomUUID(), {})).status, 400);
  assert.equal((await activateEvening(`/v1/admin/memory/records/${randomUUID()}`, adminCredential, randomUUID(), {})).status, 400);
  assert.equal((await call('/v1/conversations', {
    token: tokenA,
    key: randomUUID(),
    body: { ...body, consent: { audio: false } }
  })).status, 400);
  assert.equal((await call('/v1/conversations', {
    token: tokenA,
    key: randomUUID(),
    body: { ...body, unexpected: true }
  })).status, 400);
  assert.equal((await call('/v1/conversations', { credential: credentialA, key, body })).status, 401);
  assert.equal((await call('/v1/conversations', { token: tokenB, key, body })).status, 403);
  const created = await call('/v1/conversations', { token: tokenA, key, body });
  assert.equal(created.status, 201);
  assert.equal(created.result.limits.maxTurns, 10);
  const duplicate = await call('/v1/conversations', { token: tokenA, key, body });
  assert.equal(duplicate.result.conversationId, created.result.conversationId);
  assert.equal((await call('/v1/conversations', {
    token: tokenA,
    key,
    body: { ...body, generation: 2 }
  })).status, 409);
  assert.equal((await call('/v1/conversations', {
    token: tokenA,
    key: randomUUID(),
    body
  })).status, 409);

  const imageBody = { ...virtualBody, consent: { audio: true, image: true } };
  const imageSession = await call('/v1/conversations', { token: virtualToken, key: randomUUID(), body: imageBody });
  assert.equal(imageSession.status, 201);
  const imageId = imageSession.result.conversationId;
  const imageHeaders = { 'Idempotency-Key': randomUUID(), 'Client-Turn-Id': randomUUID(),
    'Boot-Id': imageBody.bootId, Generation: String(imageBody.generation), 'Device-Revision': '0',
    'Catalog-Version': imageBody.catalogVersion };
  assert.equal((await imageRequest(imageId, undefined, imageHeaders)).status, 401);
  assert.equal((await imageRequest(imageId, tokenB, imageHeaders)).status, 403);
  assert.equal((await imageRequest(imageId, virtualToken, {})).status, 400);
  assert.equal((await imageRequest(imageId, virtualToken, { ...imageHeaders, Generation: '999' })).status, 409);
  assert.equal((await imageRequest(created.result.conversationId, tokenA,
    { ...imageHeaders, 'Boot-Id': body.bootId, Generation: String(body.generation), 'Catalog-Version': body.catalogVersion })).status, 403);
  assert.equal((await imageRequest(imageId, virtualToken, imageHeaders, Buffer.from('invalid'))).status, 400);
  for (let attempt = 0; attempt < 3; attempt++) {
    const oversizedImage = await declaredOversizeRequest(`/v1/conversations/${imageId}/image`,
      { ...imageHeaders, Authorization: `Bearer ${virtualToken}`, 'Content-Type': 'image/jpeg' }, 262145);
    assert.equal(oversizedImage.status, 413);
    assert.equal(oversizedImage.connection, 'close', 'rejected unread image must not advertise reusable HTTP/1 socket');
    assert.equal((await imageRequest(imageId, virtualToken, imageHeaders, Buffer.from('invalid'))).status, 400);
  }
  const jpegMetadata = Buffer.from(costumeJpeg); jpegMetadata[3] = 0xe1;
  assert.equal((await imageRequest(imageId, virtualToken, imageHeaders, jpegMetadata)).status, 400);
  const imageAccepted = await imageRequest(imageId, virtualToken, imageHeaders);
  assert.equal(imageAccepted.status, 202);
  assert.equal((await imageRequest(imageId, virtualToken, imageHeaders)).result.operationId, imageAccepted.result.operationId);
  assert.equal((await imageRequest(imageId, virtualToken, { ...imageHeaders, 'Idempotency-Key': randomUUID() })).status, 409);
  const imageOperation = await read(`/v1/operations/${imageAccepted.result.operationId}`, virtualToken);
  assert.equal(imageOperation.result.kind, 'image');
  assert.equal(imageOperation.result.result.source, 'fake');
  const correctionHeaders = { ...imageHeaders, 'Idempotency-Key': randomUUID() };
  const correction = await imageRequest(imageId, virtualToken, correctionHeaders, null, 'Cerveny plast');
  assert.equal(correction.status, 200);
  assert.equal(correction.result.kind, 'costume');
  assert.equal(correction.result.result.description, 'Cerveny plast');
  assert.equal(correction.result.result.source, 'manual');
  assert.equal((await read(`/v1/operations/${imageAccepted.result.operationId}`, virtualToken)).result.result.description, 'Cerveny plast');
  assert.equal((await imageRequest(imageId, virtualToken, correctionHeaders, null, 'Cerveny plast')).result.operationId,
    correction.result.operationId);
  assert.equal((await imageRequest(imageId, virtualToken, correctionHeaders, null, 'Modry plast')).status, 409);
  const clearHeaders = { ...imageHeaders, 'Idempotency-Key': randomUUID() };
  const clear = await imageRequest(imageId, virtualToken, clearHeaders, null, '');
  assert.equal(clear.status, 200);
  assert.equal(clear.result.status, 'completed');
  assert.equal(clear.result.kind, 'costume');
  assert.equal(clear.result.result.source, 'manual');
  assert.equal(clear.result.result.description, '');
  assert.equal((await imageRequest(imageId, virtualToken, clearHeaders, null, '')).result.operationId,
    clear.result.operationId, 'empty correction is an idempotent receipt');
  const clearedImage = await read(`/v1/operations/${imageAccepted.result.operationId}`, virtualToken);
  assert.equal(clearedImage.result.status, 'cancelled');
  assert.equal(clearedImage.result.result, null, 'clear never publishes an empty completed image');
  assert.equal(await remove(`/v1/conversations/${imageId}`, virtualToken, randomUUID()), 204);

  const parallelBody = { ...body, deviceId: 'device-b' };
  const parallel = await Promise.all([
    call('/v1/conversations', { token: tokenB, key: randomUUID(), body: parallelBody }),
    call('/v1/conversations', { token: tokenB, key: randomUUID(), body: parallelBody })
  ]);
  assert.deepEqual(parallel.map(value => value.status).sort(), [201, 409]);

  const turn = {
    clientTurnId: randomUUID(),
    bootId: body.bootId,
    generation: body.generation,
    text: 'Soukromá testovací otázka',
    readyMask: 63,
    deviceRevision: 7,
    catalogVersion: body.catalogVersion
  };
  const turnPath = `/v1/conversations/${created.result.conversationId}/turns`;
  const realtimeBody = { ...virtualBody, catalogVersion: 'ollivander-private-party-v1' };
  const realtimeSession = await call('/v1/conversations', { token: virtualToken, key: randomUUID(), body: realtimeBody });
  assert.equal(realtimeSession.status, 201);
  const realtimePath = `/v1/conversations/${realtimeSession.result.conversationId}/realtime-turns`;
  const realtime = { ...turn, bootId: realtimeBody.bootId, catalogVersion: realtimeBody.catalogVersion,
    text: '', opening: true, wav: null, voice: 'marin', speed: 1 };
  assert.equal((await call(realtimePath, { key: randomUUID(), body: realtime })).status, 401);
  assert.equal((await call(realtimePath, { token: tokenB, key: randomUUID(), body: realtime })).status, 403);
  assert.equal((await call(realtimePath, { token: virtualToken, key: randomUUID(), body: { ...realtime, extra: true } })).status, 400);
  assert.equal((await call(realtimePath, { token: virtualToken, key: randomUUID(), body: realtime })).result.code, 'realtime_unavailable');
  const livePath = `/v1/conversations/${realtimeSession.result.conversationId}/realtime-live`;
  assert.equal((await call(livePath, { key: randomUUID(), body: realtime })).status, 401);
  assert.equal((await call(livePath, { token: tokenB, key: randomUUID(), body: realtime })).status, 403);
  assert.equal((await call(livePath, { token: virtualToken, key: randomUUID(), body: { ...realtime, extra: true } })).status, 400);
  assert.equal((await call(livePath, { token: virtualToken, key: randomUUID(), body: realtime })).result.code, 'realtime_unavailable');
  const missingStream = `${livePath}/${randomUUID()}`;
  assert.equal((await call(`${missingStream}/played`, { body: { operationId: randomUUID() } })).status, 401);
  assert.equal((await call(`${missingStream}/played`, { token: virtualToken, body: { operationId: randomUUID() } })).status, 404);
  assert.equal((await call(`${missingStream}/audio`, { token: virtualToken, body: {} })).status, 404);
  // A valid recording exceeds the ordinary 4096-byte JSON cap and reaches
  // conversation admission, without making a paid provider call in fake mode.
  assert.equal((await call(realtimePath, { token: virtualToken, key: randomUUID(),
    body: { ...realtime, opening: false, wav: makeWav(19200).toString('base64') } })).result.code,
    'opening_required_or_already_started');
  const oversizedRealtimeBytes = Buffer.byteLength(JSON.stringify({ ...realtime,
    opening: false, wav: 'a'.repeat(2600001) }));
  assert.equal((await declaredOversizeRequest(realtimePath,
    { Authorization: `Bearer ${virtualToken}`, 'Idempotency-Key': randomUUID(),
      'Content-Type': 'application/json' }, oversizedRealtimeBytes)).status, 413);
  assert.equal(await remove(`/v1/conversations/${realtimeSession.result.conversationId}`, virtualToken, randomUUID()), 204);
  const turnKey = randomUUID();
  assert.equal((await call(turnPath, {
    token: tokenB,
    key: turnKey,
    body: turn
  })).status, 403);
  assert.equal((await call(turnPath, {
    token: tokenA,
    key: randomUUID(),
    body: { ...turn, bootId: randomUUID() }
  })).status, 409);
  const simultaneous = await Promise.all([
    call(turnPath, { token: tokenA, key: turnKey, body: turn }),
    call(turnPath, { token: tokenA, key: turnKey, body: turn })
  ]);
  assert.deepEqual(simultaneous.map(value => value.status), [202, 202]);
  assert.equal(simultaneous[0].result.operationId, simultaneous[1].result.operationId);
  const accepted = simultaneous[0];
  assert.equal(accepted.status, 202);
  assert.equal((await call(turnPath, {
    token: tokenA,
    key: turnKey,
    body: turn
  })).result.operationId, accepted.result.operationId);
  assert.equal((await call(turnPath, {
    token: tokenA,
    key: turnKey,
    body: { ...turn, text: 'changed' }
  })).status, 409);
  assert.equal((await call(turnPath, {
    token: tokenA,
    key: randomUUID(),
    body: turn
  })).status, 409);
  const operationPath = `/v1/operations/${accepted.result.operationId}`;
  assert.equal((await read(operationPath, tokenB)).status, 403);
  const operation = await read(operationPath, tokenA);
  assert.equal(operation.status, 200);
  assert.equal(operation.result.kind, 'turn');
  assert.equal(operation.result.status, 'completed');
  assert.equal(operation.result.correlation.clientTurnId, turn.clientTurnId);
  assert.equal(operation.result.correlation.deviceRevision, turn.deviceRevision);
  assert.equal(operation.result.result.decision, 'ask');
  assert.equal(operation.result.result.proposal, null);
  assert.doesNotMatch(JSON.stringify(operation.result), /Soukromá testovací otázka/);

  const transcriptionPath = `/v1/conversations/${created.result.conversationId}/transcriptions`;
  const transcriptionFields = {
    clientTurnId: randomUUID(),
    bootId: body.bootId,
    generation: body.generation,
    deviceRevision: 7
  };
  const transcriptionKey = randomUUID();
  assert.equal((await transcribe(transcriptionPath, tokenB, randomUUID(), transcriptionFields)).status, 403);
  const malformedWav = makeWav();
  malformedWav.writeUInt32LE(44100, 24);
  assert.equal((await transcribe(transcriptionPath, tokenA, randomUUID(), transcriptionFields, malformedWav)).status, 400);
  assert.equal((await transcribe(transcriptionPath, tokenA, randomUUID(), transcriptionFields, makeWav(1_920_002))).status, 413);
  const oversizedStatus = await new Promise((resolve, reject) => {
    const oversizedRequest = request(`${baseUrl}${transcriptionPath}`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${tokenA}`,
        'Idempotency-Key': randomUUID(),
        'Content-Type': 'multipart/form-data; boundary=oversized',
        'Content-Length': 2_000_001
      }
    }, response => {
      response.resume();
      response.on('end', () => resolve(response.statusCode));
    });
    oversizedRequest.on('error', reject);
    oversizedRequest.setTimeout(5000, () => oversizedRequest.destroy(new Error('Oversized request timed out')));
    oversizedRequest.flushHeaders();
  });
  assert.equal(oversizedStatus, 413);
  const chunkedBoundary = 'kufr-test-boundary';
  const chunkedResponse = await fetch(`${baseUrl}${transcriptionPath}`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${tokenA}`,
      'Idempotency-Key': randomUUID(),
      'Content-Type': `multipart/form-data; boundary=${chunkedBoundary}`
    },
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(Buffer.from(`--${chunkedBoundary}\r\nContent-Disposition: form-data; name="wav"; filename="large.wav"\r\nContent-Type: audio/wav\r\n\r\n`));
        controller.enqueue(Buffer.alloc(2_000_001));
        controller.enqueue(Buffer.from(`\r\n--${chunkedBoundary}--\r\n`));
        controller.close();
      }
    }),
    duplex: 'half'
  });
  assert.equal(chunkedResponse.status, 413);
  const transcription = await transcribe(transcriptionPath, tokenA, transcriptionKey, transcriptionFields);
  assert.equal(transcription.status, 202);
  assert.equal((await transcribe(transcriptionPath, tokenA, transcriptionKey, transcriptionFields)).result.operationId,
    transcription.result.operationId);
  assert.equal((await transcribe(transcriptionPath, tokenA, transcriptionKey,
    { ...transcriptionFields, deviceRevision: 8 })).status, 409);
  const transcriptionOperationPath = `/v1/operations/${transcription.result.operationId}`;
  const transcriptionOperation = await read(transcriptionOperationPath, tokenA);
  assert.equal(transcriptionOperation.result.kind, 'transcription');
  assert.equal(transcriptionOperation.result.status, 'completed');
  assert.equal(transcriptionOperation.result.result.source, 'deviceMicrophone');
  assert.equal((await read(transcriptionOperationPath, tokenB)).status, 403);
  assert.equal((await transcribe(transcriptionPath, tokenA, randomUUID(),
    { ...transcriptionFields, clientTurnId: randomUUID() }, makeWav(8192))).status, 202);

  const speechPath = `/v1/conversations/${created.result.conversationId}/speech`;
  const speechBody = { turnId: turn.clientTurnId, outputTarget: 'device' };
  const speechKey = randomUUID();
  assert.equal((await call(speechPath, { token: tokenA, key: randomUUID(),
    body: { ...speechBody, outputTarget: 'both' } })).status, 400);
  for (const invalidOptions of [
    { voice: 'alloy' }, { speed: 0.84 }, { speed: 1.26 },
    { speed: null }, { voice: null }, { unknown: true },
  ]) {
    assert.equal((await call(speechPath, { token: tokenA, key: randomUUID(),
      body: { ...speechBody, ...invalidOptions } })).status, 400);
  }
  assert.equal((await call(speechPath, { token: tokenB, key: randomUUID(), body: speechBody })).status, 403);
  assert.equal((await call(speechPath, { token: tokenA, key: randomUUID(),
    body: { ...speechBody, turnId: randomUUID() } })).status, 404);
  const oversizedSpeech = await fetch(`${baseUrl}${speechPath}`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${tokenA}`,
      'Idempotency-Key': randomUUID(),
      'Content-Type': 'application/json'
    },
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(Buffer.alloc(5000, 120));
        controller.close();
      }
    }),
    duplex: 'half'
  });
  assert.equal(oversizedSpeech.status, 413);
  const selectedSpeechBody = { ...speechBody, voice: 'cedar', speed: 1.15 };
  const speech = await call(speechPath, { token: tokenA, key: speechKey, body: selectedSpeechBody });
  assert.equal(speech.status, 202);
  assert.equal((await call(speechPath, { token: tokenA, key: speechKey, body: selectedSpeechBody })).result.operationId,
    speech.result.operationId);
  assert.equal((await call(speechPath, { token: tokenA, key: speechKey,
    body: { ...selectedSpeechBody, speed: 1.2 } })).status, 409);
  assert.equal((await call(speechPath, { token: tokenA, key: randomUUID(), body: speechBody })).status, 409);
  const speechOperationPath = `/v1/operations/${speech.result.operationId}`;
  const speechOperation = await read(speechOperationPath, tokenA);
  assert.equal(speechOperation.result.kind, 'speech');
  assert.equal(speechOperation.result.correlation.clientTurnId, turn.clientTurnId);
  assert.equal(speechOperation.result.result.outputTarget, 'device');
  const audioPath = `/v1/audio/${speechOperation.result.result.audioId}`;
  const downloaded = await downloadAudio(audioPath, tokenA);
  assert.equal(downloaded.status, 200);
  assert.equal(downloaded.contentType, 'audio/wav');
  assert.equal(downloaded.sampleRate, '32000');
  assert.equal(downloaded.channels, '1');
  assert.equal(downloaded.body.subarray(0, 4).toString(), 'RIFF');
  assert.equal((await downloadAudio(audioPath, tokenB)).status, 403);
  assert.equal((await downloadAudio('/v1/audio/missing', tokenA)).status, 404);

  for (let index = 1; index < 9; index++) {
    const next = await call(turnPath, {
      token: tokenA,
      key: randomUUID(),
      body: { ...turn, clientTurnId: randomUUID() }
    });
    assert.equal(next.status, 202);
  }
  const competingTurns = await Promise.all([
    call(turnPath, {
      token: tokenA,
      key: randomUUID(),
      body: { ...turn, clientTurnId: randomUUID() }
    }),
    call(turnPath, {
      token: tokenA,
      key: randomUUID(),
      body: { ...turn, clientTurnId: randomUUID() }
    })
  ]);
  assert.deepEqual(competingTurns.map(value => value.status).sort(), [202, 429]);

  const deviceBConversation = parallel.find(value => value.status === 201).result.conversationId;
  const deviceBTurnPath = `/v1/conversations/${deviceBConversation}/turns`;
  const deviceBTurn = await call(deviceBTurnPath, {
    token: tokenB,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID(), bootId: parallelBody.bootId }
  });
  assert.equal(deviceBTurn.status, 202);
  const deviceBOperationPath = `/v1/operations/${deviceBTurn.result.operationId}`;
  const cancelKey = randomUUID();
  assert.equal(await remove(deviceBOperationPath, tokenB, randomUUID(), 'unexpected'), 400);
  const chunkedDelete = await fetch(`${baseUrl}${deviceBOperationPath}`, {
    method: 'DELETE',
    headers: {
      Authorization: `Bearer ${tokenB}`,
      'Idempotency-Key': randomUUID()
    },
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(new TextEncoder().encode('unexpected'));
        controller.close();
      }
    }),
    duplex: 'half'
  });
  assert.equal(chunkedDelete.status, 400);
  assert.equal(await remove(deviceBOperationPath, tokenA, cancelKey), 403);
  assert.equal(await remove(deviceBOperationPath, tokenB, cancelKey), 204);
  const cancelledOperation = await read(deviceBOperationPath, tokenB);
  assert.equal(cancelledOperation.result.status, 'cancelled');
  assert.equal(cancelledOperation.result.result, null);
  assert.equal(await remove(deviceBOperationPath, tokenB, cancelKey), 204);
  const liveBeforeEnd = await call(deviceBTurnPath, {
    token: tokenB,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID(), bootId: parallelBody.bootId }
  });
  assert.equal(liveBeforeEnd.status, 202);
  const liveOperationPath = `/v1/operations/${liveBeforeEnd.result.operationId}`;
  assert.equal((await read(liveOperationPath, tokenB)).result.status, 'completed');
  const liveTurnId = (await read(liveOperationPath, tokenB)).result.correlation.clientTurnId;
  const speechBeforeEnd = await call(`/v1/conversations/${deviceBConversation}/speech`, {
    token: tokenB,
    key: randomUUID(),
    body: { turnId: liveTurnId, outputTarget: 'phone' }
  });
  assert.equal(speechBeforeEnd.status, 202);
  const speechBeforeEndResult = await read(`/v1/operations/${speechBeforeEnd.result.operationId}`, tokenB);
  const audioBeforeEndPath = `/v1/audio/${speechBeforeEndResult.result.result.audioId}`;
  assert.equal((await downloadAudio(audioBeforeEndPath, tokenB)).status, 200);
  const endKey = randomUUID();
  const deviceBConversationPath = `/v1/conversations/${deviceBConversation}`;
  assert.equal(await remove(deviceBConversationPath, tokenA, endKey), 403);
  assert.equal(await remove(deviceBConversationPath, tokenB, endKey), 204);
  assert.equal(await remove(deviceBConversationPath, tokenB, endKey), 204);
  const endedOperation = await read(liveOperationPath, tokenB);
  assert.equal(endedOperation.result.status, 'cancelled');
  assert.equal(endedOperation.result.result, null);
  assert.equal((await downloadAudio(audioBeforeEndPath, tokenB)).status, 404);
  assert.equal((await call(deviceBTurnPath, {
    token: tokenB,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID() }
  })).status, 404);

  for (let index = 0; index < 15; index++) {
    assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 200);
  }
  assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 429);
  for (const secret of [credentialA, credentialB, tokenA, tokenB]) {
    assert.equal(serverOutput.includes(secret), false);
  }
  assert.equal(serverOutput.includes(turn.text), false);

  const beforeRestartLogs = (await read('/v1/admin/logs', logCredential)).result;
  const encodedLogs = JSON.stringify(beforeRestartLogs);
  for (const secret of [credentialA, credentialB, adminCredential, logCredential, tokenA, tokenB,
      turn.text, 'private-query-secret', 'private-secret']) {
    assert.equal(encodedLogs.includes(secret), false, 'sensitive content absent from server log');
  }
  await stopServer();
  startServer();
  await waitForServer();
  assert.equal((await call('/v1/conversations', { token: tokenA, key, body })).status, 401);
  const afterRestartLogs = (await read(`/v1/admin/logs?after=${beforeRestartLogs.nextCursor}`, logCredential)).result;
  assert.equal(afterRestartLogs.storeId, beforeRestartLogs.storeId);
  assert.ok(afterRestartLogs.entries.some(entry => entry.eventCode === 'server_started'));
  const newToken = (await call('/v1/device-access/token', { credential: credentialA })).result.accessToken;
  const createReplay = await call('/v1/conversations', { token: newToken, key, body });
  assert.equal(createReplay.result.conversationId, created.result.conversationId);
  assert.equal((await call('/v1/conversations', {
    token: newToken,
    key,
    body: { ...body, generation: 2 }
  })).status, 409);
  const newTokenB = (await call('/v1/device-access/token', { credential: credentialB })).result.accessToken;
  assert.equal(await remove(deviceBOperationPath, newTokenB, cancelKey), 204);
  assert.equal(await remove(deviceBConversationPath, newTokenB, endKey), 204);
  const otherDeviceCreate = await call('/v1/conversations', {
    token: newTokenB,
    key,
    body: { ...body, deviceId: 'device-b', consent: { audio: false, image: false } }
  });
  assert.equal(otherDeviceCreate.status, 201);
  assert.notEqual(otherDeviceCreate.result.conversationId, created.result.conversationId);
  const raceConversationPath = `/v1/conversations/${otherDeviceCreate.result.conversationId}`;
  assert.equal((await transcribe(`${raceConversationPath}/transcriptions`, newTokenB,
    randomUUID(), { clientTurnId: randomUUID(), bootId: body.bootId, generation: 1, deviceRevision: 7 })).status, 403);
  const raceTurnPath = `${raceConversationPath}/turns`;
  const [raceTurn, raceEnd] = await Promise.all([
    call(raceTurnPath, {
      token: newTokenB,
      key: randomUUID(),
      body: { ...turn, clientTurnId: randomUUID(), bootId: body.bootId }
    }),
    remove(raceConversationPath, newTokenB, randomUUID())
  ]);
  assert.equal(raceEnd, 204);
  assert.ok([202, 404].includes(raceTurn.status));
  if (raceTurn.status === 202) {
    const racedOperation = await read(`/v1/operations/${raceTurn.result.operationId}`, newTokenB);
    assert.equal(racedOperation.result.status, 'cancelled');
    assert.equal(racedOperation.result.result, null);
  }
  const lostOperation = await read(operationPath, newToken);
  const lostTranscription = await read(transcriptionOperationPath, newToken);
  assert.equal(lostTranscription.result.kind, 'transcription');
  assert.equal(lostTranscription.result.status, 'failed');
  const lostSpeech = await read(speechOperationPath, newToken);
  assert.equal(lostSpeech.result.kind, 'speech');
  assert.equal(lostSpeech.result.status, 'failed');
  assert.equal((await downloadAudio(audioPath, newToken)).status, 404);
  assert.equal(lostOperation.result.status, 'failed');
  assert.equal(lostOperation.result.error.code, 'result_expired');
  assert.equal((await call(turnPath, {
    token: newToken,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID() }
  })).status, 404);
  // Evening content survives restart and is separate from short conversation TTL.
  assert.equal((await read('/v1/evening-memory', 'invalid')).status, 401);
  const previousEvening = (await read('/v1/evening-memory', newToken)).result;
  assert.ok(previousEvening.exchangeCount > 0);
  assert.equal(await remove(`/v1/evening-memory/${previousEvening.eveningId}`, newTokenB, randomUUID()), 204);
  assert.equal((await read('/v1/evening-memory', newToken)).result.exchangeCount, previousEvening.exchangeCount);
  assert.equal(await remove(`/v1/evening-memory/${previousEvening.eveningId.toUpperCase()}`, newToken, randomUUID()), 204);
  const clearedEvening = (await read('/v1/evening-memory', newToken)).result;
  assert.equal(clearedEvening.exchangeCount, 0);
  assert.notEqual(clearedEvening.eveningId, previousEvening.eveningId);
  const listBefore = await read('/v1/evenings', newToken);
  assert.equal(listBefore.status, 200);
  assert.equal(listBefore.result.activeEveningId, clearedEvening.eveningId);
  assert.equal(listBefore.result.evenings.length, 1);
  assert.equal(JSON.stringify(listBefore.result).includes(turn.text), false);
  const eveningName = 'Další večer 🎉';
  const eveningBody = { name: eveningName, expectedEveningId: clearedEvening.eveningId };
  const eveningKey = randomUUID();
  assert.equal((await call('/v1/evenings', { token: newToken, key: randomUUID(),
    body: { ...eveningBody, extra: 'forbidden' } })).status, 400);
  const duplicateName = await fetch(`${baseUrl}/v1/evenings`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${newToken}`, 'Idempotency-Key': randomUUID(),
      'Content-Type': 'application/json' },
    body: `{"name":"First","name":"Second","expectedEveningId":"${clearedEvening.eveningId}"}`
  });
  assert.equal(duplicateName.status, 400);
  assert.equal((await call('/v1/evenings', { token: newToken, key: randomUUID(),
    body: { ...eveningBody, name: 'bad\nname' } })).status, 400);
  assert.equal((await call('/v1/evenings', { token: newToken, key: randomUUID(),
    body: { ...eveningBody, name: '🎉'.repeat(81) } })).status, 400);
  const newEvening = await call('/v1/evenings', { token: newToken, key: eveningKey, body: eveningBody });
  assert.equal(newEvening.status, 201);
  const namedEveningId = newEvening.result.activeEveningId;
  assert.equal(newEvening.result.evenings.length, 2);
  assert.equal(newEvening.result.evenings.find(item => item.eveningId === namedEveningId).name, eveningName);
  assert.equal((await call('/v1/evenings', { token: newToken, key: eveningKey,
    body: { ...eveningBody, name: 'Changed' } })).status, 409);
  assert.equal((await call('/v1/evenings', { token: newToken, key: randomUUID(),
    body: eveningBody })).result.code, 'evening_conflict');
  assert.equal((await activateEvening(`/v1/evenings/${namedEveningId}/active`, newTokenB,
    randomUUID(), { expectedEveningId: (await read('/v1/evenings', newTokenB)).result.activeEveningId })).status, 404);
  const switchKey = randomUUID();
  const switchBody = { expectedEveningId: namedEveningId };
  const switched = await activateEvening(`/v1/evenings/${clearedEvening.eveningId}/active`,
    newToken, switchKey, switchBody);
  assert.equal(switched.status, 200);
  assert.equal(switched.result.activeEveningId, clearedEvening.eveningId);
  assert.deepEqual((await call('/v1/evenings', { token: newToken, key: eveningKey,
    body: eveningBody })).result, newEvening.result);
  assert.equal((await read('/v1/evenings', newToken)).result.activeEveningId, clearedEvening.eveningId);
  assert.deepEqual((await activateEvening(`/v1/evenings/${clearedEvening.eveningId}/active`,
    newToken, switchKey, switchBody)).result, switched.result);
  const greetingSession = await call('/v1/conversations', { token: newToken, key: randomUUID(), body });
  assert.equal(greetingSession.status, 201);
  const openingPath = `/v1/conversations/${greetingSession.result.conversationId}/opening`;
  const { text: ignoredText, ...openingBody } = { ...turn, clientTurnId: randomUUID() };
  const openingKey = randomUUID();
  const greeting = await call(openingPath, { token: newToken, key: openingKey, body: openingBody });
  assert.equal(greeting.status, 202);
  assert.equal((await read(`/v1/operations/${greeting.result.operationId}`, newToken)).result.result.decision, 'ask');
  assert.equal((await call(openingPath, { token: newToken, key: openingKey, body: openingBody })).result.operationId, greeting.result.operationId);
  assert.equal((await call(openingPath, { token: newToken, key: randomUUID(), body: { ...openingBody, clientTurnId: randomUUID() } })).status, 409);
  assert.equal((await call(openingPath, { token: newToken, key: randomUUID(), body: { ...openingBody, text: 'forbidden' } })).status, 400);
  assert.equal(await remove(`/v1/evening-memory/${previousEvening.eveningId}`, newToken, randomUUID()), 204);
  assert.equal((await read('/v1/evening-memory', newToken)).result.exchangeCount, 1);
  console.log('Backend HTTP smoke: access, fake voice, named evenings, scope and restart passed');
} finally {
  await stopServer();
  await rm(databaseDirectory, { recursive: true, force: true });
}
