import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/features/director/DirectorPanel.ts', import.meta.url), 'utf8');
const linksSource = await readFile(new URL('../src/features/director/DuelLinks.ts', import.meta.url), 'utf8');
const linksModule = `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(linksSource, { mode: 'transform' })).toString('base64')}`;
const { DuelGrantMemory } = await import(linksModule);
const compiled = stripTypeScriptTypes(source
  .replace("'../../../../design-system/ui.js'", JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace("import { DirectorClient, DirectorRequestError, type ShowState } from './DirectorClient';",
    'class DirectorRequestError extends Error {} class DirectorClient {}')
  .replace("'./DuelLinks'", JSON.stringify(linksModule)), { mode: 'transform' });
const { DirectorPanel, duelNarrationLabel, remoteStopStatus } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
globalThis.document = { hidden: false };

function deferred() {
  let resolve, reject;
  const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
}
const state = revision => ({ deviceId: 'fixture-device', eveningId: 'one-evening', revision, mood: { dominant: 'amused' } });
function panel() {
  const value = Object.create(DirectorPanel.prototype);
  Object.assign(value, {
    visible: true, authorized: true, pending: false, generation: 0, readSequence: 0, controlEpoch: 0, refreshedAt: Date.now(),
    client: { deviceId: 'fixture-device' }, state: state(0), duelGrants: new DuelGrantMemory(),
    query: () => ({ hidden: false, replaceChildren() {} }), update() {}, feedback() {},
  });
  return value;
}

test('duel audio labels require the exact device playback proof and never infer it from completion', () => {
  assert.match(duelNarrationLabel({ narrationMode: 'web_only', narrationStatus: 'playback_confirmed' }), /Hlas kufru se nespouští/);
  assert.match(duelNarrationLabel({ narrationMode: 'physical_device', narrationStatus: 'final_wait' }), /není potvrzené/);
  assert.match(duelNarrationLabel({ narrationMode: 'physical_device', narrationStatus: 'playback_finished' }), /není potvrzené/);
  assert.match(duelNarrationLabel({ narrationMode: 'physical_device', narrationStatus: 'playback_confirmed' }), /Zařízení potvrdilo/);
  assert.match(duelNarrationLabel({ narrationMode: 'unbound' }), /nedokládá zvuk/);
});

test('backend Stop sends one priority POST while an ordinary command hangs and discards its late response', async () => {
  const value = panel();
  const ordinary = deferred(), stop = deferred();
  const calls = [];
  value.client.request = (suffix, body) => {
    calls.push({ suffix, body });
    return body.kind === 'stop' ? stop.promise : ordinary.promise;
  };
  const ordinaryWork = value.command('mood-evening', 'amused');
  assert.equal(value.pending, true);
  const stopWork = value.stopThroughBackend();
  await value.stopThroughBackend();
  assert.equal(calls.length, 2);
  const request = calls[1];
  assert.equal(request.suffix, '/commands');
  assert.equal(request.body.kind, 'stop');
  assert.equal(request.body.expectedStateRevision, 0);
  assert.match(request.body.commandId, /^[0-9a-f-]{36}$/);
  assert.equal(request.body.sessionId, undefined);
  assert.equal(request.body.ttlSeconds, 30);
  const stopped = { ...state(4), deviceStopRevision: 7, updatedAt: new Date().toISOString() };
  stop.resolve({ status: 'accepted', state: stopped }); await stopWork;
  ordinary.resolve({ status: 'accepted', code: 'direction_accepted', state: state(1) }); await ordinaryWork;
  assert.equal(value.state.revision, 4);
  assert.equal(value.remoteStop.status, 'accepted');
  assert.equal(value.remoteStop.expectedDeviceRevision, 7);
  assert.match(value.operation.status, /Zneplatněno povelem Stop/);
  assert.equal(calls.filter(call => call.body.kind === 'stop').length, 1);
});

test('lost Stop response stays unknown without replay and prior snapshots cannot replace accepted Stop', async () => {
  const value = panel();
  let sends = 0;
  value.client.request = async () => { sends++; throw new Error('connection lost'); };
  await value.stopThroughBackend();
  assert.equal(value.remoteStop.status, 'unknown');
  assert.equal(value.remoteStop.pending, false);
  assert.equal(value.refreshedAt, 0);
  assert.equal(sends, 1);
  assert.match(remoteStopStatus(value.state, value.remoteStop, 0), /neznámý/);

  const read = deferred(), stop = deferred();
  value.client.request = (suffix, body) => body ? stop.promise : read.promise;
  const stopping = value.stopThroughBackend();
  const reading = value.refresh();
  stop.resolve({ status: 'accepted', state: { ...state(9), deviceStopRevision: 2, updatedAt: new Date().toISOString() } });
  await stopping;
  read.resolve(state(8)); await reading;
  assert.equal(value.state.revision, 9);
});

test('Stop delivery labels require fresh matching actual device report, never backend acceptance alone', () => {
  const now = Date.now();
  const operation = { id: 'one', pending: false, status: 'accepted', expectedDeviceRevision: 3,
    acceptedAt: new Date(now - 1000).toISOString() };
  const snapshot = { ...state(5), deviceStopRevision: 3,
    deviceSettings: { stopRevision: 3, observedAt: new Date(now).toISOString() } };
  assert.match(remoteStopStatus(snapshot, operation, now, now), /Zařízení potvrdilo/);
  assert.match(remoteStopStatus(snapshot, operation, now - 13000, now), /Čekám/);
  assert.match(remoteStopStatus({ ...snapshot, deviceSettings: { stopRevision: 2, observedAt: new Date(now).toISOString() } }, operation, now, now), /Čekám/);
  assert.match(remoteStopStatus({ ...snapshot, deviceSettings: { stopRevision: 3, observedAt: new Date(now - 20000).toISOString() } }, operation, now, now), /Čekám/);
  assert.match(remoteStopStatus({ ...snapshot, deviceSettings: { stopRevision: 3, observedAt: new Date(now - 2000).toISOString() } }, operation, now, now), /Čekám/);
  assert.match(remoteStopStatus({ ...snapshot, deviceSettings: null }, operation, now, now), /Čekám/);
  assert.match(remoteStopStatus(snapshot, { ...operation, status: 'unknown' }, now, now), /neznámý/);
});

test('leaving during backend Stop retains an unknown result and ignores the old confirmation', async () => {
  const value = panel();
  const response = deferred(); value.client.request = () => response.promise;
  const stopping = value.stopThroughBackend();
  ++value.generation; value.abandonRemoteStop();
  assert.equal(value.remoteStop.pending, false);
  assert.equal(value.remoteStop.status, 'unknown');
  response.resolve({ status: 'accepted', state: { ...state(8), deviceStopRevision: 2, updatedAt: new Date().toISOString() } });
  await stopping;
  assert.equal(value.state.revision, 0);
  assert.match(remoteStopStatus(value.state, value.remoteStop, Date.now()), /neznámý/);
});

test('backend and local Stop buttons retain separate command owners', () => {
  const value = panel();
  const elements = new Map();
  value.query = selector => {
    if (!elements.has(selector)) elements.set(selector, { listeners: {}, addEventListener(name, action) { this.listeners[name] = action; } });
    return elements.get(selector);
  };
  value.element = { querySelectorAll: () => [] };
  let remote = 0, local = 0;
  value.stopThroughBackend = async () => { remote++; };
  value.local = { stop: () => { local++; } };
  value.bind(false);
  elements.get('[data-remote-stop]').listeners.click();
  assert.equal(remote, 1); assert.equal(local, 0);
  elements.get('[data-director-stop]').listeners.click();
  assert.equal(remote, 1); assert.equal(local, 1);
});

test('a late older poll cannot replace the more recent authoritative read', async () => {
  const value = panel();
  const first = deferred(), second = deferred();
  const queue = [first, second]; value.request = () => queue.shift().promise;
  const readFirst = value.refresh(), readSecond = value.refresh();
  second.resolve(state(2)); await readSecond;
  first.resolve(state(1)); await readFirst;
  assert.equal(value.state.revision, 2);
});

test('hiding Director clears the operator grant copies and removes rendered links', () => {
  const value = panel();
  value.element = { hidden: false };
  value.duelGrants.bind('one-evening', 'duel-one');
  value.duelGrants.remember('left', { token: 'a'.repeat(43), expiresAt: new Date(Date.now() + 60000).toISOString() });
  let rendered = false;
  value.renderDuelLinks = () => { rendered = true; };
  value.show(false);
  assert.equal(value.duelGrants.get('left'), undefined);
  assert.equal(value.element.hidden, true);
  assert.equal(rendered, true);
});

test('duel replacement, cancellation and evening changes discard old grant copies', async () => {
  const value = panel();
  value.renderDuel = () => {};
  const duel = { id: 'duel-one', eveningId: 'one-evening', deviceId: 'fixture-device', state: 'Ready' };
  value.setDuel(duel);
  const grant = { token: 'a'.repeat(43), expiresAt: new Date(Date.now() + 60000).toISOString() };
  value.duelGrants.remember('left', grant);
  value.setDuel({ ...duel, state: 'Cancelled' });
  assert.equal(value.duelGrants.get('left'), undefined);
  value.setDuel(duel); value.duelGrants.remember('left', grant);
  value.setDuel({ ...duel, id: 'duel-two' });
  assert.equal(value.duelGrants.get('left'), undefined);
  value.duelGrants.remember('left', grant);
  value.request = async () => ({ ...state(4), eveningId: 'next-evening' });
  await value.refresh();
  assert.equal(value.duelGrants.get('left'), undefined);
  assert.equal(value.duel, undefined);
});

test('an accepted operation invalidates a poll already in flight without replay', async () => {
  const value = panel();
  const response = deferred(); value.request = () => response.promise;
  const read = value.refresh();
  let writes = 0;
  await value.perform('Nálada večera', async () => { writes++; value.state = state(3); });
  response.resolve(state(1)); await read;
  assert.equal(value.state.revision, 3);
  assert.equal(writes, 1);
  assert.equal(value.pending, false);
});
