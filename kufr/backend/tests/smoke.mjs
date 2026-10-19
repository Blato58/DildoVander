import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { createServer } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

// This is an offline HTTP contract test. An ambient provider key is removed;
// real provider conversations and iPhone/suitcase hardware are separate gates.
const credentialA = randomBytes(32).toString('base64');
const credentialB = randomBytes(32).toString('base64');
const adminCredential = randomBytes(32).toString('base64');
const logCredential = randomBytes(32).toString('base64');
const sha256 = value => createHash('sha256').update(value).digest('hex');
const port = await new Promise((resolve, reject) => {
  const server = createServer();
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => { const port = server.address().port; server.close(() => resolve(port)); });
});
const baseUrl = `http://127.0.0.1:${port}`;
const directory = await mkdtemp(join(tmpdir(), 'kufr-conversation-smoke-'));
const environment = {
  ...process.env, OPENAI_API_KEY: '', ASPNETCORE_ENVIRONMENT: 'Production',
  Backend__DatabasePath: join(directory, 'journal.sqlite'),
  Backend__Memory__Enabled: 'true', Backend__Memory__LongTermPersonalEnabled: 'true',
  Backend__Memory__CloudIndexingEnabled: 'false', Backend__Memory__PersonalStoreId: '', Backend__Memory__MotifStoreId: '',
  Backend__Memory__AppNamespace: 'offline-http-smoke',
  Devices__0__DeviceId: 'device-a', Devices__0__CredentialSha256: sha256(credentialA),
  Devices__1__DeviceId: 'device-b', Devices__1__CredentialSha256: sha256(credentialB),
  Backend__Admin__CredentialSha256: sha256(adminCredential), Backend__Logs__CredentialSha256: sha256(logCredential)
};
let server;
let output = '';
function start() {
  server = spawn('dotnet', [process.env.KUFR_BACKEND_DLL || 'bin/Debug/net10.0/backend.dll', '--urls', baseUrl], {
    cwd: new URL('..', import.meta.url), env: environment, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  for (const stream of [server.stdout, server.stderr]) { stream.on('data', chunk => { output += chunk.toString(); }); }
}
async function stop() {
  if (server?.exitCode === null) { const ended = new Promise(resolve => server.once('exit', resolve)); server.kill(); await ended; }
}
async function ready() {
  for (let index = 0; index < 120; index++) {
    if (server.exitCode !== null) { throw new Error(`Backend exited: ${output}`); }
    try { if ((await fetch(`${baseUrl}/healthz`)).ok) { return; } } catch { }
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error(`Backend did not start: ${output}`);
}
function parseJsonResponse(text, { method, path, status, contentType }) {
  try { return JSON.parse(text); }
  catch {
    // Never echo visitor content, credentials or query parameters into CI logs.
    const diagnostics = output.slice(-20_000).split(/\r?\n/).map(line => line.trim());
    const exceptionTypes = diagnostics.filter(line => /^(?:System|Microsoft|Kufr)\.[\w.]+Exception:/.test(line))
      .map(line => line.slice(0, line.indexOf(':')));
    const frames = diagnostics.filter(line => line.startsWith('at Kufr.Backend.')).slice(-5);
    throw new Error(`Expected JSON from ${method} ${path}: HTTP ${status}, `
      + `content-type=${contentType ?? 'none'}, bytes=${Buffer.byteLength(text)}`
      + (exceptionTypes.length ? `; server exceptions=${[...new Set(exceptionTypes)].join(',')}` : '')
      + (frames.length ? `\n${frames.join('\n')}` : ''));
  }
}
async function call(path, { method = 'GET', token, key, body, raw, headers = {} } = {}) {
  const response = await fetch(baseUrl + path, {
    method, headers: { ...headers, ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(key ? { 'Idempotency-Key': key } : {}), ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: raw ?? (body ? JSON.stringify(body) : undefined)
  });
  if (path.startsWith('/v1/')) { assert.equal(response.headers.get('cache-control'), 'no-store'); }
  return { status: response.status, result: response.status === 204 ? null : parseJsonResponse(await response.text(), {
    method, path: new URL(response.url).pathname, status: response.status, contentType: response.headers.get('content-type')
  }) };
}
const post = (path, options) => call(path, { method: 'POST', ...options });
const remove = (path, token, key = randomUUID(), body) => call(path, { method: 'DELETE', token, key, body });
const contextPath = (id, device = 'device-a') => `/v1/admin/memory/context?deviceId=${device}&conversationId=${id}&personalityId=ollivander-trunk`;
const context = id => call(contextPath(id), { token: adminCredential });
const tokenFor = async credential => {
  const issued = await post('/v1/device-access/token', { token: credential });
  assert.equal(issued.status, 200); assert.equal(issued.result.expiresInSeconds, 900); return issued.result.accessToken;
};
const request = (body, clientTurnId = randomUUID()) => ({ clientTurnId, bootId: body.bootId, generation: body.generation,
  readyMask: 63, deviceRevision: 7, catalogVersion: body.catalogVersion, opening: true, text: '', wav: null, voice: 'marin', speed: 1 });
const costumeJpeg = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooooA//2Q==', 'base64');

start();
try {
  await ready();
  assert.deepEqual((await call('/healthz')).result, { status: 'ok' });
  assert.equal((await post('/v1/device-access/token')).status, 401);
  assert.equal((await post('/v1/device-access/token', { token: 'wrong' })).status, 401);
  let tokenA = await tokenFor(credentialA);
  let tokenB = await tokenFor(credentialB);
  assert.equal((await call('/v1/admin/modes', { token: tokenA })).status, 401);
  const modes = (await call('/v1/admin/modes', { token: adminCredential })).result;
  assert.equal(modes.engines.primaryEngine, 'gpt-live');
  assert.deepEqual(modes.engines.engines.map(item => [item.engine, item.model]),
    [['gpt-live', 'gpt-live-1'], ['realtime', 'gpt-realtime-2.1']]);
  assert.ok(modes.engines.engines.every(item => !item.ready && item.unavailableReason === 'provider_credential_missing'));
  assert.ok(!('dialogMode' in modes) && !('voiceMode' in modes), 'retired implementations are absent from capabilities');
  const diagnostics = (await call('/v1/diagnostics', { token: tokenA })).result;
  assert.equal(diagnostics.conversationContractVersion, 2);
  assert.equal(diagnostics.liveActorMode, 'AutonomousActor');
  assert.deepEqual(diagnostics.engines, modes.engines);
  assert.equal((await call('/v1/admin/modes', { method: 'PUT', token: adminCredential,
    body: { expectedRevision: modes.revision, dialogMode: 'fake' } })).status, 410);

  const body = { deviceId: 'device-a', bootId: randomUUID(), generation: 0,
    catalogVersion: 'ollivander-private-party-v1', locale: 'cs-CZ', consent: { audio: true, image: true } };
  const createKey = randomUUID();
  assert.equal((await post('/v1/conversations', { key: createKey, body })).status, 401);
  assert.equal((await post('/v1/conversations', { token: tokenB, key: createKey, body })).status, 403);
  const created = await post('/v1/conversations', { token: tokenA, key: createKey, body });
  assert.equal(created.status, 201); const id = created.result.conversationId;
  assert.equal((await post('/v1/conversations', { token: tokenA, key: createKey, body })).result.conversationId, id);
  assert.equal((await post('/v1/conversations', { token: tokenA, key: createKey, body: { ...body, generation: 1 } })).status, 409);

  // Opaque catalog versions may be admitted for compatibility, but neither
  // unified engine may reserve or complete a selection against that catalog.
  const opaqueBody = { ...body, deviceId: 'device-b', bootId: randomUUID(), catalogVersion: 'fixture-opaque-catalog' };
  const opaque = await post('/v1/conversations', { token: tokenB, key: randomUUID(), body: opaqueBody });
  assert.equal(opaque.status, 201);
  for (const engine of ['gpt-live', 'realtime-live']) {
    const opaquePath = `/v1/conversations/${opaque.result.conversationId}/${engine}`;
    const opaqueKey = randomUUID();
    const invalid = request(opaqueBody);
    for (let retry = 0; retry < 2; retry++) {
      const rejected = await post(opaquePath, { token: tokenB, key: opaqueKey, body: invalid });
      assert.equal(rejected.status, 400);
      assert.equal(rejected.result.code, 'invalid_request');
      assert.ok(!rejected.result.operationId, 'unknown catalog never publishes a completed operation');
    }
    const unreserved = await post(opaquePath, { token: tokenB, key: opaqueKey,
      body: { ...invalid, catalogVersion: body.catalogVersion } });
    assert.equal(unreserved.status, 409);
    assert.equal(unreserved.result.code, 'conversation_version_mismatch', 'schema rejection did not reserve a poisoned idempotency key');
  }
  assert.equal((await remove(`/v1/conversations/${opaque.result.conversationId}`, tokenB)).status, 204);

  for (const engine of ['gpt-live', 'realtime-live']) {
    const unavailable = await post(`/v1/conversations/${id}/${engine}`, { token: tokenA, key: randomUUID(), body: request(body) });
    assert.equal(unavailable.status, 409); assert.equal(unavailable.result.code, 'realtime_unavailable');
    assert.ok(!unavailable.result.operationId, 'unavailable engines cannot fabricate paid operations');
    assert.equal((await post(`/v1/conversations/${id}/${engine}`, { token: tokenB, key: randomUUID(), body: request(body) })).status, 403);
    const absentStream = await call(`/v1/conversations/${id}/${engine}/${randomUUID()}/audio-stream`, { token: tokenA });
    assert.equal(absentStream.status, 404);
  }
  for (const route of ['opening', 'turns', 'transcriptions', 'speech', 'realtime-turns']) {
    const retired = await post(`/v1/conversations/${id}/${route}`, { token: tokenA, key: randomUUID(), body: request(body) });
    assert.equal(retired.status, 410); assert.equal(retired.result.code, 'conversation_engine_upgrade_required');
  }
  const photo = await post(`/v1/conversations/${id}/image`, { token: tokenA, key: randomUUID(), raw: costumeJpeg,
    headers: { 'Content-Type': 'image/jpeg', 'Client-Turn-Id': randomUUID(), 'Boot-Id': body.bootId, Generation: '0', 'Device-Revision': '7',
      'Catalog-Version': body.catalogVersion, 'Image-Id': randomUUID(), 'Image-Revision': '1',
      'Image-Source': 'phone', 'Captured-At': new Date().toISOString(), 'Image-Consent': 'true' } });
  assert.equal(photo.status, 503); assert.equal(photo.result.code, 'vision_provider_unavailable');
  assert.ok(!photo.result.operationId, 'camera upload cannot pretend successful analysis without its provider');

  assert.equal((await context(id)).result.records.length, 0);
  assert.equal((await call(contextPath(id), { token: tokenA })).status, 401);
  const guestId = randomUUID();
  const binding = { deviceId: 'device-a', conversationId: id, guestId, identitySource: 'offline-http-explicit-operator',
    identityVerified: true, longTermAllowed: true, cloudExportAllowed: false, expectedVersion: 0 };
  assert.equal((await call('/v1/admin/memory/guest', { method: 'PUT', token: tokenA, body: binding })).status, 401);
  assert.equal((await call('/v1/admin/memory/guest', { method: 'PUT', token: adminCredential, body: binding })).status, 200);
  const evening = (await call('/v1/evening-memory', { token: tokenA })).result.eveningId;
  const memoryId = randomUUID();
  const memory = { deviceId: 'device-a', conversationId: id, eveningId: evening, personalityId: 'ollivander-trunk',
    scope: 'Personal', kind: 'UserReportedFact', text: 'HTTP guest A repairs violet clocks.', sourceReference: 'operator:fixture', expectedRevision: 0 };
  assert.equal((await call(`/v1/admin/memory/records/${memoryId}`, { method: 'PUT', token: adminCredential, body: memory })).status, 200);
  assert.equal((await context(id)).result.records[0].text, memory.text);
  assert.equal((await remove(`/v1/conversations/${id}`, tokenB)).status, 404);
  assert.equal((await post(`/v1/conversations/${id}/gpt-live`, { token: tokenA, key: randomUUID(), body: request(body) })).status,
    404, 'authoritative memory edits retire active provider context before another response');
  const secondBody = { ...body, bootId: randomUUID() };
  const second = (await post('/v1/conversations', { token: tokenA, key: randomUUID(), body: secondBody })).result.conversationId;
  assert.equal((await context(second)).result.records.length, 0, 'anonymous next guest cannot inherit personal records');
  const returnBinding = { ...binding, conversationId: second };
  assert.equal((await call('/v1/admin/memory/guest', { method: 'PUT', token: adminCredential, body: returnBinding })).status, 200);
  assert.equal((await context(second)).result.records[0].memoryId, memoryId, 'explicit identity binding enables permitted return recall');
  await remove(`/v1/conversations/${second}`, tokenA);

  await stop(); start(); await ready();
  assert.equal((await call('/v1/diagnostics', { token: tokenA })).status, 401, 'restart expires process-local device grants');
  tokenA = await tokenFor(credentialA); tokenB = await tokenFor(credentialB);
  assert.equal((await context(second)).result.records[0].text, memory.text, 'canonical private archive survives restart');
  const deleted = await remove(`/v1/admin/memory/records/${memoryId}`, adminCredential, randomUUID(), { deviceId: 'device-a', expectedRevision: 1 });
  assert.equal(deleted.status, 200); assert.equal(deleted.result.localDeletion, 'complete');
  assert.equal((await context(second)).result.records.length, 0);
  const resurrected = await call(`/v1/admin/memory/records/${memoryId}`, { method: 'PUT', token: adminCredential, body: memory });
  assert.equal(resurrected.status, 409, 'late stale write cannot recreate manually deleted identity');
  assert.equal((await call('/v1/admin/logs', { token: tokenA })).status, 401);
  const logs = await call('/v1/admin/logs', { token: logCredential });
  assert.equal(logs.status, 200);
  for (const secret of [credentialA, credentialB, adminCredential, logCredential, tokenA, tokenB, memory.text]) {
    assert.ok(!output.includes(secret) && !JSON.stringify(logs.result).includes(secret), 'technical diagnostics exclude private words and credentials');
  }
  console.log('Offline backend HTTP: two engines/readiness, auth, idempotency, retired routes, truthful vision failure, private guest binding, archive restart and deletion fences passed.');
} finally {
  await stop();
  assert.equal(dirname(resolve(directory)), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('kufr-conversation-smoke-'));
  await rm(directory, { recursive: true, force: true });
}
