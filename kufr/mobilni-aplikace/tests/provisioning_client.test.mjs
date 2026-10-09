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

test('background refresh stays quiet and a tap waits for its BLE slot', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let finishPoll;
  let mutations = 0;
  const updates = [];
  const client = new ProvisioningClient({
    connect: async () => status,
    status: () => new Promise(resolve => { finishPoll = resolve; }),
    disconnect: async () => {},
  }, (...event) => updates.push(event));
  client.show(true);
  await client.connect('KUFR_123456');
  const beforePoll = updates.length;
  context.mock.timers.tick(1000);
  assert.equal(updates.length, beforePoll, 'polling must not disable controls');
  const mutation = client.perform(async () => { mutations++; return status; });
  assert.equal(mutations, 0, 'native requests remain serial');
  assert.equal(updates.at(-1)[2], true, 'first tap reserves the slot and disables more taps');
  finishPoll(status);
  await mutation;
  assert.equal(mutations, 1, 'tap is not discarded');
  assert.equal(updates.at(-1)[2], false);
  await client.disconnect();
});

test('a tap waiting on an old poll cannot mutate a reconnected suitcase', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let finishPoll;
  let mutations = 0;
  const client = new ProvisioningClient({
    connect: async () => status,
    status: () => new Promise(resolve => { finishPoll = resolve; }),
    disconnect: async () => {},
  }, () => {});
  client.show(true);
  await client.connect('KUFR_123456');
  context.mock.timers.tick(1000);
  const mutation = client.perform(async () => { mutations++; return status; });
  await client.disconnect();
  await client.connect('KUFR_654321');
  finishPoll(status);
  await mutation;
  assert.equal(mutations, 0);
  await client.disconnect();
});

test('selecting another suitcase during a refresh waits rather than losing the tap', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let finishPoll;
  const connections = [];
  const client = new ProvisioningClient({
    connect: async ({ name }) => { connections.push(name); return status; },
    status: () => new Promise(resolve => { finishPoll = resolve; }),
    disconnect: async () => {},
  }, () => {});
  client.show(true);
  await client.connect('KUFR_123456');
  context.mock.timers.tick(1000);
  const connecting = client.connect('KUFR_654321');
  assert.equal(connections.length, 1);
  finishPoll(status);
  await connecting;
  assert.deepEqual(connections, ['KUFR_123456', 'KUFR_654321']);
  await client.disconnect();
});

test('the first queued tap keeps controls busy until completion and disconnect clears its reservation', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let finishPoll;
  let finishAction;
  const updates = [];
  let actions = 0;
  const client = new ProvisioningClient({
    connect: async () => status,
    status: () => new Promise(resolve => { finishPoll = resolve; }),
    disconnect: async () => {},
  }, (...event) => updates.push(event));
  client.show(true);
  await client.connect('KUFR_123456');
  context.mock.timers.tick(1000);
  const beforeTap = updates.length;
  const first = client.perform(async () => { actions++; return new Promise(resolve => { finishAction = resolve; }); });
  await client.perform(async () => { actions++; return status; });
  finishPoll(status);
  await flush();
  assert.equal(actions, 1);
  assert.ok(updates.slice(beforeTap).every(event => event[2]), 'no re-enable between poll and queued action');
  finishAction(status);
  await first;
  assert.equal(updates.at(-1)[2], false);
  context.mock.timers.tick(1000);
  const stale = client.connect('KUFR_654321');
  await client.disconnect();
  await client.connect('KUFR_112233');
  finishPoll(status);
  await stale;
  await client.disconnect();
});

test('one-button pairing serializes polling and refreshes saved controls', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let polls = 0;
  let pairs = 0;
  let refreshed = 0;
  let complete;
  const updates = [];
  const client = new ProvisioningClient({ connect: async () => status, status: async () => { polls++; return status; },
    pairLocal: () => { pairs++; return new Promise(resolve => { complete = resolve; }); }, disconnect: async () => {} },
  (...event) => updates.push(event));
  client.show(true);
  await client.connect('KUFR_123456');
  const pairing = client.pairLocal(async () => { refreshed++; });
  await client.pairLocal(async () => { refreshed++; });
  context.mock.timers.tick(5000); await flush();
  assert.equal(polls, 0);
  assert.equal(pairs, 1);
  complete({ deviceId: 'kufr-1', host: 'kufr.local' });
  await pairing;
  assert.equal(refreshed, 1);
  assert.equal(updates.at(-1)[0], status);
  assert.match(updates.at(-1)[1], /Keychain/);
  context.mock.timers.tick(1000); await flush();
  assert.equal(polls, 1);
});

test('late BLE pairing after disconnect cannot refresh a different device', async () => {
  let complete;
  let refreshed = 0;
  const client = new ProvisioningClient({ connect: async () => status,
    pairLocal: () => new Promise(resolve => { complete = resolve; }), disconnect: async () => {} }, () => {});
  await client.connect('KUFR_123456');
  const pairing = client.pairLocal(async () => { refreshed++; });
  await client.disconnect();
  complete({ deviceId: 'old-kufr', host: 'old.local' });
  await pairing;
  assert.equal(refreshed, 0);
});

test('lost pairing response is never replayed or exposed as SDK text', async () => {
  let pairs = 0;
  const updates = [];
  const client = new ProvisioningClient({ connect: async () => status, pairLocal: async () => {
    pairs++; throw { code: 'timeout', message: 'token=private' };
  } }, (...event) => updates.push(event));
  await client.connect('KUFR_123456');
  await client.pairLocal(async () => { throw new Error('must not refresh'); });
  assert.equal(pairs, 1);
  assert.match(updates.at(-1)[1], /nejistý/);
  assert.doesNotMatch(updates.at(-1)[1], /private|token=/);
});

test('pairing tap during a quiet poll reserves one action and waits for its BLE slot', async (context) => {
  context.mock.timers.enable({ apis: ['setTimeout'] });
  let finishPoll;
  let pairs = 0;
  let refreshed = 0;
  const client = new ProvisioningClient({ connect: async () => status,
    status: () => new Promise(resolve => { finishPoll = resolve; }),
    pairLocal: async () => { pairs++; return { deviceId: 'kufr-1', host: 'kufr.local' }; },
    disconnect: async () => {} }, () => {});
  client.show(true);
  await client.connect('KUFR_123456');
  context.mock.timers.tick(1000);
  const pairing = client.pairLocal(async () => { refreshed++; });
  await client.pairLocal(async () => { refreshed++; });
  assert.equal(pairs, 0);
  finishPoll(status);
  await pairing;
  assert.equal(pairs, 1);
  assert.equal(refreshed, 1);
  await client.disconnect();
});
