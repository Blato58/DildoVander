// Synthetic integration against the real .NET listener. No provider credentials.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { createServer } from 'node:net';
import { request as httpRequest } from 'node:http';
import { fileURLToPath } from 'node:url';

const port = await new Promise(resolvePort => {
  const socket = createServer(); socket.listen(0, '127.0.0.1', () => {
    const port = socket.address().port; socket.close(() => resolvePort(port));
  });
});
const root = await mkdtemp(join(tmpdir(), 'dv10-http-'));
const backendRoot = fileURLToPath(new URL('..', import.meta.url));
const backend = resolve(process.env.KUFR_BACKEND_DLL || join(backendRoot, 'bin/Debug/net10.0/backend.dll'));
const tests = resolve(process.env.KUFR_TESTS_DLL || join(backendRoot, '../backend-tests/bin/Debug/net10.0/BackendTests.dll'));
const env = { ...process.env, OPENAI_API_KEY: '', ASPNETCORE_ENVIRONMENT: 'Production',
  Backend__DatabasePath: join(root, 'journal.sqlite'), Backend__Dialog__Mode: 'fake', Backend__Voice__Mode: 'fake',
  Backend__Memory__Enabled: 'false', Backend__Memory__CloudIndexingEnabled: 'false',
  Backend__Memory__LongTermPersonalEnabled: 'false', Keepsake__Enabled: 'true',
  Keepsake__GenerationEnabled: 'true', Keepsake__PortraitEnabled: 'true', Keepsake__ProviderMode: 'fake',
  Keepsake__DataRoot: join(root, 'archive'), Keepsake__DataProtectionRoot: join(root, 'keys'),
  Keepsake__Origin: 'https://guest.test',
  Devices__0__DeviceId: 'fixture-device',
  Devices__0__CredentialSha256: createHash('sha256').update('synthetic-device-only').digest('hex'),
  Backend__Admin__CredentialSha256: createHash('sha256').update('synthetic-admin-only').digest('hex') };
let processHandle;
let serverLog = '';
async function run(cmd, args, options = {}) {
  return await new Promise((resolveRun, reject) => {
    const child = spawn(cmd, args, { env, windowsHide: true, ...options }); let out = '', err = '';
    child.stdout.on('data', b => out += b); child.stderr.on('data', b => err += b);
    child.once('exit', code => code === 0 ? resolveRun(out) : reject(new Error(`fixture exit ${code}: ${err}`)));
  });
}
function launch() {
  serverLog = '';
  processHandle = spawn('dotnet', [backend, '--urls', `http://127.0.0.1:${port}`], { env, windowsHide: true, cwd: backendRoot });
  processHandle.stdout.on('data', b => serverLog += b); processHandle.stderr.on('data', b => serverLog += b);
}
async function stop() {
  if (processHandle?.exitCode === null) {
    const end = new Promise(resolveEnd => processHandle.once('exit', resolveEnd)); processHandle.kill(); await end;
  }
}
async function ready() {
  for (let n = 0; n < 150; n++) {
    if (processHandle.exitCode !== null) { throw new Error(`listener exited: ${serverLog}`); }
    try { await fetch(`http://127.0.0.1:${port}/`); return; } catch {}
    await new Promise(r => setTimeout(r, 50));
  }
  throw new Error('listener startup timed out');
}
class Browser {
  cookie = ''; csrf = '';
  async request(method, path, body, { key = randomUUID(), origin = 'https://guest.test', csrf = this.csrf,
    headers = {}, json = true, expected } = {}) {
    const payload = body === undefined ? undefined : json ? JSON.stringify(body) : body;
    const response = await new Promise((resolveResponse, reject) => {
      const request = httpRequest(`http://127.0.0.1:${port}${path}`, {
      method, headers: { Host: 'guest.test', 'X-Forwarded-Proto': 'https', Origin: origin,
        ...(this.cookie ? { Cookie: this.cookie } : {}), 'X-DV10-CSRF': csrf, 'Idempotency-Key': key,
        ...(body === undefined ? {} : { 'Content-Type': json ? 'application/json' : 'image/png', 'Content-Length': Buffer.byteLength(payload) }), ...headers },
      }, incoming => {
        const chunks = []; incoming.on('data', chunk => chunks.push(chunk));
        incoming.on('end', () => {
          const headers = new Headers();
          for (const [key, value] of Object.entries(incoming.headers)) {
            if (Array.isArray(value)) { for (const item of value) headers.append(key, item); }
            else if (value !== undefined) headers.set(key, value);
          }
          resolveResponse(new Response(incoming.statusCode === 204 ? null : Buffer.concat(chunks), { status: incoming.statusCode, headers }));
        });
      });
      request.on('error', reject);
      request.end(payload);
    });
    if (expected !== undefined) { assert.equal(response.status, expected, `${method} ${path} returned ${response.status}`); }
    if (path.startsWith('/guest/')) {
      assert.equal(response.headers.get('cache-control'), 'no-store');
      assert.equal(response.headers.get('referrer-policy'), 'no-referrer');
    }
    return response;
  }
  async bootstrap() {
    const response = await this.request('POST', '/guest/v1/bootstrap', { schemaVersion: 1 }, { expected: 200 });
    const cookie = response.headers.get('set-cookie');
    assert.match(cookie, /^__Host-dv10=/); assert.match(cookie, /httponly/i); assert.match(cookie, /secure/i);
    assert.match(cookie, /samesite=lax/i); assert.doesNotMatch(cookie, /domain=/i);
    this.cookie = cookie.split(';')[0]; this.csrf = (await response.json()).csrfToken;
  }
}
const prefix = id => `/guest/v1/keepsakes/${id}`;
try {
  const seeded = JSON.parse((await run('dotnet', [tests, '--keepsake-fixture'])).trim().split(/\r?\n/).at(-1));
  const [first, second] = seeded.items; const photo = Buffer.from(seeded.png, 'base64');
  launch(); await ready();
  const a = new Browser(), b = new Browser(), viewer = new Browser();
  await a.bootstrap(); await b.bootstrap(); await viewer.bootstrap();
  await a.request('GET', '/guest/v1/claim', undefined, { expected: 404 });
  await a.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: first.token }, { origin: 'https://evil.test', expected: 403 });
  await a.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: first.token }, { csrf: 'wrong', expected: 403 });
  const claimKey = randomUUID();
  const claim = () => a.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: first.token }, { key: claimKey, expected: 200 });
  assert.equal((await (await claim()).json()).locator, first.locator);
  assert.equal((await (await claim()).json()).locator, first.locator);
  await b.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: first.token }, { expected: 404 });
  await b.request('GET', prefix(first.locator), undefined, { expected: 404 });
  await b.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: second.token }, { expected: 200 });
  await a.request('GET', prefix(second.locator), undefined, { expected: 404 });
  const oldCookie = a.cookie, oldCsrf = a.csrf;
  await a.bootstrap(); assert.equal(a.cookie, oldCookie); assert.equal(a.csrf, oldCsrf);
  let view;
  for (let n = 0; n < 100; n++) {
    view = await (await a.request('GET', prefix(first.locator), undefined, { expected: 200 })).json();
    if (view.editions.length) { break; }
    await new Promise(r => setTimeout(r, 100));
  }
  assert.equal(view.retentionMode, 'manual_only'); assert.equal(view.deleteAfterUtc, null);
  assert.equal(view.editions.length, 1, JSON.stringify(view.jobs));
  assert.equal(view.editions[0].synthetic, true); assert.equal(view.uploads.length, 0);
  assert.doesNotMatch(JSON.stringify(view), /"prompt":|"deviceId":|"outcomeId":/);
  const editionId = view.editions[0].id;
  const share = await (await a.request('POST', `${prefix(first.locator)}/share-links`, { schemaVersion: 1, editionId }, { expected: 200 })).json();
  assert.equal(new URL(share.url).origin, 'https://guest.test');
  assert.equal(new URL(share.url).search, ''); const shareToken = new URL(share.url).hash.slice(3);
  await viewer.request('POST', '/guest/v1/share-claim', { schemaVersion: 1, token: shareToken }, { expected: 200 });
  const shared = await (await viewer.request('GET', `${prefix(first.locator)}?view=share&editionId=${editionId}`, undefined, { expected: 200 })).json();
  assert.equal(shared.readOnly, true); assert.equal(shared.editions[0].id, editionId);
  assert.equal(shared.portraits.length, 0); assert.equal(shared.uploads.length, 0);
  await a.request('POST', `${prefix(first.locator)}/exports?view=share`, { schemaVersion: 1, editionId, format: 'zip' }, { expected: 404 });
  await viewer.request('POST', `${prefix(first.locator)}/exports`, { schemaVersion: 1, editionId, format: 'zip' }, { expected: 404 });
  await a.request('POST', `${prefix(first.locator)}/portrait-uploads`, photo, { json: false, headers: { 'X-DV10-Consent': '1' }, expected: 409 });
  await a.request('POST', `${prefix(first.locator)}/portrait-consents`,
    { schemaVersion: 1, policyVersion: 'dv10-portrait-v1' }, { expected: 400 });
  const consent = await (await a.request('POST', `${prefix(first.locator)}/portrait-consents`,
    { schemaVersion: 1, accepted: true, policyVersion: 'dv10-portrait-v1' }, { expected: 200 })).json();
  const uploadKey = randomUUID();
  const upload = await (await a.request('POST', `${prefix(first.locator)}/portrait-uploads`, photo,
    { json: false, key: uploadKey, headers: { 'X-DV10-Consent': String(consent.consentVersion) }, expected: 201 })).json();
  const retryUpload = await (await a.request('POST', `${prefix(first.locator)}/portrait-uploads`, photo,
    { json: false, key: uploadKey, headers: { 'X-DV10-Consent': String(consent.consentVersion) }, expected: 201 })).json();
  assert.equal(upload.upload.id, retryUpload.upload.id);
  const portrait = await (await a.request('POST', `${prefix(first.locator)}/portraits`,
    { schemaVersion: 1, uploadId: upload.upload.id, editionId, variant: 'portrait_only', consentVersion: consent.consentVersion }, { expected: 202 })).json();
  assert.equal(portrait.job.kind, 'portrait');
  for (let n = 0; n < 100; n++) {
    view = await (await a.request('GET', prefix(first.locator), undefined, { expected: 200 })).json();
    if (view.portraits.length) { break; } await new Promise(r => setTimeout(r, 100));
  }
  assert.equal(view.portraits.length, 1, JSON.stringify(view.jobs));
  await viewer.request('GET', `${prefix(first.locator)}/assets/${view.portraits[0].assetId}`, undefined, { expected: 404 });
  for (const format of ['story', 'card', 'zip']) {
    const exported = await (await a.request('POST', `${prefix(first.locator)}/exports`, { schemaVersion: 1, editionId, format }, { expected: 201 })).json();
    const download = await a.request('GET', `${prefix(first.locator)}/assets/${exported.asset.id}`, undefined, { expected: 200 });
    assert.ok((await download.arrayBuffer()).byteLength > 10);
  }
  const recovery = await (await a.request('POST', `${prefix(first.locator)}/recovery-links`, { schemaVersion: 1 }, { expected: 200 })).json();
  await stop(); launch(); await ready();
  let recovered = new Browser(); await recovered.bootstrap();
  await recovered.request('POST', '/guest/v1/recover', { schemaVersion: 1, token: new URL(recovery.url).hash.slice(3) }, { expected: 200 });
  view = await (await recovered.request('GET', prefix(first.locator), undefined, { expected: 200 })).json();
  assert.equal(view.editions[0].id, editionId); assert.equal(view.portraits.length, 1);
  const deviceToken = (await (await b.request('POST', '/v1/device-access/token', undefined,
    { headers: { Authorization: 'Bearer synthetic-device-only' }, expected: 200 })).json()).accessToken;
  await b.request('POST', `/v1/keepsakes/${first.locator}/claim`, undefined,
    { headers: { Authorization: 'Bearer ' + deviceToken }, expected: 409 });
  await b.request('POST', `/v1/keepsakes/${first.locator}/restore-owner`, { schemaVersion: 1, confirm: 'revoke_prior_access' },
    { headers: { Authorization: 'Bearer ' + deviceToken }, expected: 401 });
  const restoreKey = randomUUID();
  const restoreOwner = () => b.request('POST', `/v1/keepsakes/${first.locator}/restore-owner`,
    { schemaVersion: 1, confirm: 'revoke_prior_access' },
    { key: restoreKey, headers: { Authorization: 'Bearer synthetic-admin-only' }, expected: 200 });
  const restored = await (await restoreOwner()).json();
  assert.equal((await (await restoreOwner()).json()).url, restored.url, 'operator restoration reply is idempotent');
  await recovered.request('GET', prefix(first.locator), undefined, { expected: 404 });
  await viewer.request('GET', `${prefix(first.locator)}?view=share&editionId=${editionId}`, undefined, { expected: 404 });
  recovered = new Browser(); await recovered.bootstrap();
  await recovered.request('POST', '/guest/v1/claim', { schemaVersion: 1, token: new URL(restored.url).hash.slice(3) }, { expected: 200 });
  view = await (await recovered.request('GET', prefix(first.locator), undefined, { expected: 200 })).json();
  assert.equal(view.editions[0].id, editionId); assert.equal(view.editions.length, 1);
  const deleted = await (await recovered.request('DELETE', `${prefix(first.locator)}/portrait`,
    { schemaVersion: 1, confirm: 'delete', expectedRevision: view.revision }, { expected: 202 })).json();
  assert.equal(deleted.receipt.providerState, 'unknown_not_provider_confirmed');
  const receipt = await (await recovered.request('GET', `/guest/v1/deletions/${deleted.receipt.id}`, undefined, { expected: 200 })).json();
  assert.equal(receipt.receipt.scope, 'photos_and_portraits');
  await b.request('GET', `/guest/v1/deletions/${deleted.receipt.id}`, undefined, { expected: 404 });
  view = await (await recovered.request('GET', prefix(first.locator), undefined, { expected: 200 })).json();
  assert.equal(view.portraits.length, 0); assert.equal(view.uploads.length, 0);
  const deletionKey = randomUUID();
  const whole = await (await recovered.request('DELETE', prefix(first.locator),
    { schemaVersion: 1, confirm: 'delete', expectedRevision: view.revision }, { key: deletionKey, expected: 202 })).json();
  const wholeReplay = await (await recovered.request('DELETE', prefix(first.locator),
    { schemaVersion: 1, confirm: 'delete', expectedRevision: view.revision }, { key: deletionKey, expected: 202 })).json();
  assert.equal(wholeReplay.receipt.id, whole.receipt.id);
  await recovered.request('GET', prefix(first.locator), undefined, { expected: 404 });
  await viewer.request('GET', `${prefix(first.locator)}?view=share&editionId=${editionId}`, undefined, { expected: 404 });
  await recovered.request('GET', `/guest/v1/deletions/${whole.receipt.id}`, undefined, { expected: 200 });
  const adminHeaders = { Authorization: 'Bearer synthetic-admin-only' };
  await b.request('GET', '/v1/admin/keepsakes', undefined, { expected: 401 });
  await b.request('GET', '/v1/admin/keepsakes', undefined,
    { headers: { Authorization: 'Bearer synthetic-device-only' }, expected: 401 });
  const listed = await (await b.request('GET', '/v1/admin/keepsakes', undefined,
    { headers: adminHeaders, expected: 200 })).json();
  assert.ok(listed.items.some(item => item.id === second.locator));
  assert.doesNotMatch(JSON.stringify(listed), /"story":|"prompt":|"token":/);
  await b.request('POST', '/v1/admin/keepsake-deletions/preview',
    { schemaVersion: 1, targetIds: null, scope: 'all' }, { headers: adminHeaders, expected: 400 });
  const preview = (await (await b.request('POST', '/v1/admin/keepsake-deletions/preview',
    { schemaVersion: 1, targetIds: [second.locator], scope: 'all' },
    { headers: adminHeaders, expected: 201 })).json()).preview;
  assert.deepEqual(preview.targetIds, [second.locator]);
  const confirmPath = `/v1/admin/keepsake-deletions/${preview.id}/confirm`;
  await b.request('POST', confirmPath,
    { schemaVersion: 1, confirm: 'delete', previewDigest: 'wrong' }, { headers: adminHeaders, expected: 409 });
  await b.request('GET', prefix(second.locator), undefined, { expected: 200 });
  const batchKey = randomUUID();
  const confirmBatch = () => b.request('POST', confirmPath,
    { schemaVersion: 1, confirm: 'delete', previewDigest: preview.digest },
    { headers: adminHeaders, key: batchKey, expected: 202 });
  const batch = (await (await confirmBatch()).json()).batch;
  assert.equal((await (await confirmBatch()).json()).batch.id, batch.id);
  await b.request('GET', prefix(second.locator), undefined, { expected: 404 });
  await b.request('GET', `/guest/v1/deletions/${batch.deletionReceiptIds[0]}`, undefined, { expected: 404 });
  await b.request('GET', `/v1/admin/keepsake-deletions/batches/${batch.id}`, undefined,
    { headers: adminHeaders, expected: 200 });
  await b.request('GET', `/v1/admin/keepsake-deletions/receipts/${batch.deletionReceiptIds[0]}`, undefined,
    { headers: adminHeaders, expected: 200 });
  for (const forbidden of [first.token, shareToken, new URL(recovery.url).hash.slice(3), a.csrf]) {
    assert.ok(!serverLog.includes(forbidden), 'secret leaked into technical logs');
  }
  console.log('DV10 HTTP integration passed: isolation, claim replay, CSRF/origin, share scope, consent/upload, fake jobs, exports, restart/recovery, deletion receipts and secret-free logs.');
} finally { await stop(); await rm(root, { recursive: true, force: true }); }
