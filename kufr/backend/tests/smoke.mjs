import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { createServer } from 'node:net';

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
const server = spawn('dotnet', ['bin/Debug/net10.0/backend.dll', '--urls', baseUrl], {
  cwd: new URL('..', import.meta.url),
  env: {
    ...process.env,
    Devices__0__DeviceId: 'device-a',
    Devices__0__CredentialSha256: sha256(credentialA),
    Devices__1__DeviceId: 'device-b',
    Devices__1__CredentialSha256: sha256(credentialB),
    ASPNETCORE_ENVIRONMENT: 'Production'
  },
  windowsHide: true,
  stdio: ['ignore', 'pipe', 'pipe']
});

let serverOutput = '';
for (const stream of [server.stdout, server.stderr]) {
  stream.on('data', chunk => { serverOutput += chunk.toString(); });
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

try {
  let ready = false;
  for (let attempt = 0; attempt < 100; attempt++) {
    if (server.exitCode !== null) throw new Error(`Backend exited: ${serverOutput}`);
    try {
      await fetch(`${baseUrl}/`);
      ready = true;
      break;
    } catch {
      await new Promise(resolve => setTimeout(resolve, 50));
    }
  }
  assert.ok(ready, `Backend did not start: ${serverOutput}`);

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

  for (let index = 0; index < 15; index++) {
    assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 200);
  }
  assert.equal((await call('/v1/device-access/token', { credential: credentialA })).status, 429);
  assert.doesNotMatch(serverOutput, new RegExp(`${credentialA}|${credentialB}|${tokenA}|${tokenB}`));
  console.log('Backend HTTP smoke: access, scope, idempotency, concurrency and token cap passed');
} finally {
  server.kill();
}
