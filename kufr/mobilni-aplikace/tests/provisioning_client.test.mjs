import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/features/provisioning/ProvisioningClient.ts', import.meta.url), 'utf8');
const { ProvisioningClient, errorMessage } = await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`);
const status = { v: 1, ok: true, deviceId: 'kufr-1', firmwareVersion: '1.0.0', network: {
  phase: 'connected', activeProfile: 'hotspot', lastSuccessfulProfile: 'hotspot', result: 'connected',
  hotspot: { configured: true, ssid: 'iPhone' }, fallback: { configured: false, ssid: '' },
}, backendConnected: false, ota: { state: 'idle', automaticInstall: false } };
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };

test('late secure connection cannot restore disconnected UI or start polling', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let resolve;
  let polls = 0;
  const updates = [];
  const client = new ProvisioningClient({ connect: () => new Promise((done) => { resolve = done; }), status: async () => { polls++; return status; }, disconnect: async () => {} }, (...event) => updates.push(event));
  client.show(true);
  const connecting = client.connect('KUFR_123456');
  await client.disconnect();
  resolve(status);
  await connecting;
  context.mock.timers.tick(5000);
  await flush();
  assert.equal(polls, 0);
  assert.equal(updates.at(-1)[0], null);
  assert.match(updates.at(-1)[1], /odpojeno/);
});

test('polling is serial, stops while hidden, resumes explicitly and terminates on BLE loss', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let polls = 0;
  let reject = false;
  const updates = [];
  const client = new ProvisioningClient({ connect: async () => status, status: async () => { polls++; if (reject) throw { code: 'disconnected' }; return status; }, disconnect: async () => {} }, (...event) => updates.push(event));
  client.show(true);
  await client.connect('KUFR_123456');
  context.mock.timers.tick(1000); await flush();
  assert.equal(polls, 1);
  client.show(false);
  context.mock.timers.tick(10000); await flush();
  assert.equal(polls, 1);
  client.show(true);
  reject = true;
  context.mock.timers.tick(1000); await flush();
  assert.equal(polls, 2);
  context.mock.timers.tick(10000); await flush();
  assert.equal(polls, 2);
  assert.match(updates.at(-1)[1], /znovu/);
});

test('uncertain mutations are not replayed and stale completions cannot overwrite reconnect', async () => {
  const updates = [];
  const client = new ProvisioningClient({ connect: async () => status, disconnect: async () => {} }, (...event) => updates.push(event));
  await client.connect('KUFR_123456');
  let requests = 0;
  await client.perform(async () => { requests++; throw { code: 'timeout', message: 'secret password should never be displayed' }; });
  assert.equal(requests, 1);
  assert.match(updates.at(-1)[1], /nejistý/);
  assert.doesNotMatch(updates.at(-1)[1], /secret/);
  let resolve;
  const mutation = client.perform(() => new Promise((done) => { resolve = done; }));
  await client.disconnect();
  await client.connect('KUFR_654321');
  resolve({ ...status, deviceId: 'old-device' });
  await mutation;
  assert.equal(updates.at(-1)[0].deviceId, 'kufr-1');
});

test('unexpected native errors never echo SDK messages or secrets', () => {
  assert.doesNotMatch(errorMessage({ code: 'new_sdk_error', message: 'password=abc' }), /password|abc/);
  assert.match(errorMessage({ code: 'authentication_failed' }), /PoP/);
});
