import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { createServer } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const credentialA = randomBytes(32).toString('base64');
const credentialB = randomBytes(32).toString('base64');
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
    Devices__0__DeviceId: 'device-a',
    Devices__0__CredentialSha256: sha256(credentialA),
    Devices__1__DeviceId: 'device-b',
    Devices__1__CredentialSha256: sha256(credentialB),
    Backend__DatabasePath: join(databaseDirectory, 'journal.sqlite'),
    ASPNETCORE_ENVIRONMENT: 'Production'
};

let serverOutput = '';
let server;
function startServer() {
  server = spawn('dotnet', ['bin/Debug/net10.0/backend.dll', '--urls', baseUrl], {
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

startServer();
try {
  await waitForServer();

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
    consent: { audio: false, image: false }
  };
  const key = randomUUID();
  const tokenA = issuedA.result.accessToken;
  const tokenB = issuedB.result.accessToken;
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
  const accepted = await call(turnPath, { token: tokenA, key: turnKey, body: turn });
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
  const endKey = randomUUID();
  const deviceBConversationPath = `/v1/conversations/${deviceBConversation}`;
  assert.equal(await remove(deviceBConversationPath, tokenA, endKey), 403);
  assert.equal(await remove(deviceBConversationPath, tokenB, endKey), 204);
  assert.equal(await remove(deviceBConversationPath, tokenB, endKey), 204);
  const endedOperation = await read(liveOperationPath, tokenB);
  assert.equal(endedOperation.result.status, 'cancelled');
  assert.equal(endedOperation.result.result, null);
  assert.equal((await call(deviceBTurnPath, {
    token: tokenB,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID() }
  })).status, 404);

  for (let index = 0; index < 15; index++) {
    assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 200);
  }
  assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 429);
  assert.doesNotMatch(serverOutput, new RegExp(`${credentialA}|${credentialB}|${tokenA}|${tokenB}`));

  await stopServer();
  startServer();
  await waitForServer();
  assert.equal((await call('/v1/conversations', { token: tokenA, key, body })).status, 401);
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
    body: { ...body, deviceId: 'device-b' }
  });
  assert.equal(otherDeviceCreate.status, 201);
  assert.notEqual(otherDeviceCreate.result.conversationId, created.result.conversationId);
  const raceConversationPath = `/v1/conversations/${otherDeviceCreate.result.conversationId}`;
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
  assert.equal(lostOperation.result.status, 'failed');
  assert.equal(lostOperation.result.error.code, 'result_expired');
  assert.equal((await call(turnPath, {
    token: newToken,
    key: randomUUID(),
    body: { ...turn, clientTurnId: randomUUID() }
  })).status, 404);
  console.log('Backend HTTP smoke: access, scope, fake turn, limits and restart idempotency passed');
} finally {
  await stopServer();
  await rm(databaseDirectory, { recursive: true, force: true });
}
