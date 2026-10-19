import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/features/director/DirectorClient.ts', import.meta.url), 'utf8');
const compiled = stripTypeScriptTypes(source.replace("import { nativeDebugBridge } from '../debug/NativeDebugBridge';", 'const nativeDebugBridge = () => globalThis.directorNative;'), { mode: 'transform' });
globalThis.directorNative = { showRequest: async options => ({ status: 200, body: options }), configure: async () => {} };
const { DirectorClient, DirectorRequestError } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
globalThis.window = { setTimeout, clearTimeout };

test('Director rejects destination credential forwarding and stores web access only in memory', async () => {
  const client = new DirectorClient(false);
  for (const address of ['http://example.com', 'https://user:pass@example.com', 'https://example.com/path', 'https://example.com/?key=x']) {
    await assert.rejects(client.configure(address, 'synthetic-admin', 'kufr-001'));
  }
  await client.configure('https://example.com', 'synthetic-admin', 'kufr-001');
  let sent;
  globalThis.fetch = async (url, options) => { sent = { url, ...options }; return { status: 200, text: async () => '{"revision":4}' }; };
  assert.deepEqual(await client.request('/commands', { commandId: 'one', expectedStateRevision: 3 }), { revision: 4 });
  assert.equal(sent.url, 'https://example.com/v1/show/devices/kufr-001/commands');
  assert.equal(sent.headers.Authorization, 'Bearer synthetic-admin');
  assert.equal(sent.redirect, 'error');
  assert.equal(sent.credentials, 'omit');
  assert.equal(sent.method, 'POST');
  client.forget();
  await assert.rejects(client.request(), /operátorský přístup/);
});

test('Director conflict is terminal for that request and never retried', async () => {
  const client = new DirectorClient(false);
  await client.configure('http://127.0.0.1:8174', 'synthetic-admin', 'kufr-001');
  let calls = 0;
  globalThis.fetch = async () => { calls++; return { status: 409, text: async () => '{"code":"stale_revision"}' }; };
  await assert.rejects(client.request('/commands', { commandId: 'one' }), error => error instanceof DirectorRequestError && error.status === 409);
  assert.equal(calls, 1);
});

test('native requests use the private profile bridge without exporting credentials or server URL', async () => {
  const client = new DirectorClient(true);
  await client.configure('', '', 'kufr-001');
  assert.deepEqual(await client.request('/commands', { kind: 'quiet', value: 'on' }), {
    deviceId: 'kufr-001', suffix: '/commands', method: 'POST', body: '{"kind":"quiet","value":"on"}',
  });
});
