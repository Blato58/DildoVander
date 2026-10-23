import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import test from 'node:test';
import assert from 'node:assert/strict';
const identifiers = await readFile(new URL('../../application/identifiers.mjs', import.meta.url), 'utf8');
const identifiersUrl = `data:text/javascript;base64,${Buffer.from(identifiers).toString('base64')}`;
const source = (await readFile(new URL('./client.ts', import.meta.url), 'utf8')).replace('../../application/identifiers.mjs', identifiersUrl);
const clientModule = await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`);
const { ArchiveClient, projectGallery, guestPath, parseClaim, safeIssuedLink, downloadName, parseArchiveReadiness } = clientModule;
const id = '11111111-1111-4111-8111-111111111111';
const defer = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };

test('public reads omit credentials and forbid cache and redirects', async () => {
  let init;
  const client = new ArchiveClient(async (_path, options) => { init = options; return Response.json({ schemaVersion: 1 }); });
  await client.json('/guest/v1/evening', {}, true);
  assert.equal(init.credentials, 'omit'); assert.equal(init.cache, 'no-store'); assert.equal(init.redirect, 'error');
  await assert.rejects(client.json('/guest/v1/evening', { method: 'POST' }, true));
});
test('noncooperative transport and JSON bodies cannot restore a departed route', async () => {
  const transport = defer(); const client = new ArchiveClient(() => transport.promise);
  const read = client.json('/guest/v1/evening'); client.invalidate(); transport.resolve(Response.json({ private: 'secret' }));
  await assert.rejects(read, { name: 'AbortError' });
  const body = defer(); const other = new ArchiveClient(async () => ({ ok: true, status: 200, json: () => body.promise }));
  const reading = other.json('/guest/v1/evening'); await new Promise(resolve => setImmediate(resolve));
  other.invalidate(); body.resolve({ private: 'secret' }); await assert.rejects(reading, { name: 'AbortError' });
});
test('noncooperative blob reading is cancelled before handing out private bytes', async () => {
  const body = defer(); const client = new ArchiveClient(async () => ({ ok: true, status: 200, blob: () => body.promise }));
  const reading = client.blob(`/guest/v1/keepsakes/${id}/assets/${id}`, ['image/png']); await new Promise(resolve => setImmediate(resolve));
  client.invalidate(); body.resolve(new Blob(['private'], { type: 'image/png' })); await assert.rejects(reading, { name: 'AbortError' });
});
test('mutations retain idempotency after uncertain server response without retaining token in header', async () => {
  const requests = []; let fails = true;
  const oldLocks = navigator.locks;
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_name, action) => action() } });
  try {
    const client = new ArchiveClient(async (path, init) => {
      requests.push({ path, init });
      if (path.endsWith('bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'csrf' });
      if (fails) return new Response('', { status: 503 });
      return Response.json({ locator: id });
    });
    await assert.rejects(client.mutate('/guest/v1/claim', { data: { token: 'a'.repeat(40) } })); fails = false;
    await client.mutate('/guest/v1/claim', { data: { token: 'a'.repeat(40) } });
    const claims = requests.filter(request => request.path.endsWith('/claim'));
    assert.equal(claims[0].init.headers['Idempotency-Key'], claims[1].init.headers['Idempotency-Key']);
    assert.equal(claims[1].init.headers['X-DV10-CSRF'], 'csrf');
    assert.equal(claims[1].init.credentials, 'same-origin');
    assert.ok(!JSON.stringify(claims[1].init.headers).includes('a'.repeat(40)));
  } finally { Object.defineProperty(navigator, 'locks', { configurable: true, value: oldLocks }); }
});
test('successful mutation replay identity stays until the caller accepts the parsed response', async () => {
  const pending = defer(); const requests = []; let accept = true; let exposed;
  const oldLocks = navigator.locks;
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_name, action) => action() } });
  try {
    const client = new ArchiveClient(async (path, init) => {
      requests.push({ path, init });
      if (path.endsWith('bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'csrf' });
      return requests.filter(request => request.path.endsWith('/claim')).length === 1
        ? pending.promise : Response.json({ private: 'acknowledged result' });
    });
    const options = { data: { token: 'same-request' }, accept: () => { if (!accept) throw new DOMException('Context suspended', 'AbortError'); } };
    const first = client.mutate('/guest/v1/claim', options).then(value => { exposed = value; return undefined; }, error => error);
    await new Promise(resolve => setImmediate(resolve));
    const firstRequest = requests.find(request => request.path.endsWith('/claim'));
    accept = false;
    pending.resolve(Response.json({ private: 'late private response' }));
    const error = await first;
    assert.equal(error.name, 'AbortError');
    assert.equal(exposed, undefined);

    accept = true;
    const replayed = await client.mutate('/guest/v1/claim', options);
    assert.deepEqual(replayed, { private: 'acknowledged result' });
    const retry = requests.filter(request => request.path.endsWith('/claim'))[1];
    assert.equal(retry.init.headers['Idempotency-Key'], firstRequest.init.headers['Idempotency-Key']);
    assert.equal(retry.init.body, firstRequest.init.body);

    await client.mutate('/guest/v1/claim', options);
    const afterAcknowledgement = requests.filter(request => request.path.endsWith('/claim'))[2];
    assert.notEqual(afterAcknowledgement.init.headers['Idempotency-Key'], retry.init.headers['Idempotency-Key']);
  } finally { Object.defineProperty(navigator, 'locks', { configurable: true, value: oldLocks }); }
});
test('a pending bootstrap cannot dispatch a mutation after logical suspension', async () => {
  const bootstrap = defer(); const requests = []; let active = true;
  const oldLocks = navigator.locks;
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_name, action) => action() } });
  try {
    const client = new ArchiveClient(async path => {
      requests.push(path);
      return path.endsWith('bootstrap') ? bootstrap.promise : Response.json({ locator: id });
    });
    const options = { data: { token: 'a'.repeat(40) }, accept: () => {
      if (!active) throw new DOMException('Action suspended', 'AbortError');
    } };
    const mutation = client.mutate('/guest/v1/claim', options);
    await new Promise(resolve => setImmediate(resolve));
    active = false;
    bootstrap.resolve(Response.json({ schemaVersion: 1, csrfToken: 'csrf' }));
    await assert.rejects(mutation, { name: 'AbortError' });
    assert.deepEqual(requests, ['/guest/v1/bootstrap']);
    active = true;
    await client.mutate('/guest/v1/claim', options);
    assert.deepEqual(requests, ['/guest/v1/bootstrap', '/guest/v1/claim']);
  } finally { Object.defineProperty(navigator, 'locks', { configurable: true, value: oldLocks }); }
});

test('a queued bootstrap cannot send a mutation after route departure', async () => {
  const queued = defer(); let callback; let calls = 0;
  const oldLocks = navigator.locks;
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: (_name, action) => { callback = action; return queued.promise; } } });
  try {
    const client = new ArchiveClient(async () => { calls++; return Response.json({}); });
    const mutation = client.mutate('/guest/v1/claim', { data: { token: 'a'.repeat(40) } }); client.invalidate();
    await assert.rejects(callback(), { name: 'AbortError' }); queued.resolve();
    await assert.rejects(mutation, { name: 'AbortError' }); assert.equal(calls, 0);
  } finally { Object.defineProperty(navigator, 'locks', { configurable: true, value: oldLocks }); }
});
test('public projection excludes portraits, invalid identities and unknown media', () => {
  const item = { locator: id, editionId: id, officialName: '<script>bad</script>', story: { text: 'story' }, assets: [
    { id, kind: 'portrait_image', mediaType: 'image/png' }, { id, kind: 'export_zip', mediaType: 'text/html' }, { id, kind: 'base_image', mediaType: 'image/png' }
  ] };
  const page = projectGallery({ schemaVersion: 1, evening: { id }, items: [item, { ...item, locator: '../secret' }] });
  assert.equal(page.items.length, 1); assert.equal(page.items[0].assets.length, 1); assert.equal(page.items[0].assets[0].kind, 'base_image');
  assert.throws(() => guestPath('../secret'));
  assert.equal(guestPath(id, '', id), `/guest/v1/keepsakes/${id}?view=share&editionId=${id}`);
});
test('capability tokens stay in valid fragments and issued links stay canonical', () => {
  assert.equal(parseClaim('#c=' + 'a'.repeat(40)).kind, 'c'); assert.equal(parseClaim('?c=' + 'a'.repeat(40)), null);
  const share = 'https://example.test/keepsake/#s=' + 'a'.repeat(40);
  assert.equal(safeIssuedLink(share, 'share', 'https://example.test'), share);
  assert.throws(() => safeIssuedLink(share.replace('example.test', 'evil.test'), 'share', 'https://example.test'));
  assert.throws(() => safeIssuedLink(share, 'recovery', 'https://example.test'));
});
test('download extensions reflect validated bytes, including a JPEG returned for artwork', () => {
  assert.equal(downloadName('wand-art.png', 'image/jpeg'), 'wand-art.jpg');
  assert.equal(downloadName('bundle.zip', 'application/zip'), 'bundle.zip');
  assert.throws(() => downloadName('wand-art.png', 'text/html'));
});
test('readiness accepts only the no-retry server projection and remains optional for older views', () => {
  const ready = { state: 'partial', reason: 'reconciliation_required', canRetry: false, shouldPoll: true, hasStory: true, hasArtwork: false };
  assert.deepEqual(parseArchiveReadiness(ready), ready);
  assert.equal(parseArchiveReadiness(null), null);
  assert.equal(parseArchiveReadiness(undefined), null);
  assert.equal(parseArchiveReadiness({ ...ready, canRetry: true }), null);
  assert.equal(parseArchiveReadiness({ ...ready, reason: 'retry_now' }), null);
});
