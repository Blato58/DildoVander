import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { test } from 'node:test';
import { loadDeviceModule } from './load-test-module.mjs';
import { validateControlMessage } from '../../contracts/control-validator.mjs';

const { NativeControlSocket } = await loadDeviceModule('NativeControlSocket');
const { ProtocolDeviceConnection } = await loadDeviceModule('SimulatorDeviceConnection');
Object.defineProperty(globalThis, 'crypto', { value: { randomUUID }, configurable: true });
globalThis.document = { hidden: false };
let now = 0;
const timers = new Map();
let timerId = 0;
Object.defineProperty(globalThis, 'performance', { value: { now: () => now }, configurable: true });
globalThis.window = {
  setTimeout(callback, milliseconds) { timers.set(++timerId, { callback, milliseconds }); return timerId; },
  clearTimeout(id) { timers.delete(id); },
};
const settle = async () => { await new Promise((resolve) => setImmediate(resolve)); };

function bridge() {
  const callbacks = new Set();
  const native = {
    connected: [], sent: [], closed: [], removed: 0,
    async addListener(name, callback) {
      assert.equal(name, 'transport');
      callbacks.add(callback);
      return { remove: async () => { callbacks.delete(callback); native.removed++; } };
    },
    async connect(options) { native.connected.push(options); },
    async send(options) { native.sent.push(options); },
    async close(options) { native.closed.push(options); },
    emit(state, frame, connectionId = native.connected.at(-1)?.connectionId, failureCode) {
      for (const callback of callbacks) { callback({ connectionId, state, frame, failureCode }); }
    },
  };
  return native;
}

test('native socket scopes frames and close to one connection and never passes credentials', async () => {
  const native = bridge();
  const socket = new NativeControlSocket(native);
  const frames = [];
  socket.addEventListener('message', ({ data }) => frames.push(data));
  await settle();
  const id = native.connected[0].connectionId;
  native.emit('open', undefined, randomUUID());
  assert.equal(socket.readyState, 0);
  native.emit('open');
  native.emit('frame', 'hello');
  socket.send('command');
  await settle();
  assert.deepEqual(frames, ['hello']);
  assert.deepEqual(native.sent, [{ connectionId: id, frame: 'command' }]);
  assert.deepEqual(Object.keys(native.connected[0]), ['connectionId']);
  socket.close();
  native.emit('open', undefined, id);
  native.emit('frame', 'late', id);
  await settle();
  assert.equal(socket.readyState, 3);
  assert.deepEqual(frames, ['hello']);
  assert.equal(native.removed, 1);
  assert.equal(native.closed.at(-1).connectionId, id);
});

test('closing while listener registration waits cancels without starting a native connection', async () => {
  const native = bridge();
  let finishListener;
  native.addListener = () => new Promise((resolve) => { finishListener = resolve; });
  const socket = new NativeControlSocket(native);
  socket.close();
  finishListener({ remove: async () => { native.removed++; } });
  await settle();
  assert.equal(native.connected.length, 0);
  assert.equal(native.removed, 1);
});

test('native send failure closes once and stale callbacks cannot reopen it', async () => {
  const native = bridge();
  native.send = async () => { throw new Error('send failed'); };
  const socket = new NativeControlSocket(native);
  let closes = 0;
  socket.addEventListener('close', () => closes++);
  await settle();
  native.emit('open');
  socket.send('command');
  await settle();
  native.emit('closed');
  native.emit('open');
  assert.equal(closes, 1);
  assert.equal(socket.readyState, 3);
});

const fixture = JSON.parse(await readFile(new URL('../../contracts/fixtures/valid/state-snapshot.json', import.meta.url)));

test('native manual path uses strict shared schema and matching identity before control', async () => {
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
    parseFrame: (frame) => validateControlMessage(new TextEncoder().encode(frame)),
  });
  await settle();
  native.emit('open');
  await settle();
  const hello = JSON.parse(native.sent.at(-1).frame);
  assert.equal(hello.type, 'hello');
  native.emit('frame', JSON.stringify({
    protocolVersion: 1, type: 'welcome', requestMessageId: hello.messageId,
    snapshot: { ...fixture, controlMode: 'standalone', controlLeaseId: null },
  }));
  assert.equal(connection.snapshot().phase, 'ready');
  assert.equal(connection.snapshot().source, 'native-device');
  assert.equal(connection.acquire(), true);
  const leaseId = randomUUID();
  native.emit('frame', JSON.stringify({ ...fixture, controlMode: 'manual', controlLeaseId: leaseId, eventSequence: fixture.eventSequence + 1 }));
  assert.equal(connection.snapshot().controlMode, 'manual');
  assert.equal(connection.select(1), true);
  await settle();
  const heartbeat = JSON.parse(native.sent.at(-1).frame);
  now += 10;
  native.emit('frame', JSON.stringify({
    protocolVersion: 1, type: 'heartbeat.ack', requestMessageId: heartbeat.messageId,
    bootId: fixture.bootId, controlLeaseId: leaseId, revision: fixture.revision, deviceUptimeMs: 100,
  }));
  await settle();
  const command = validateControlMessage(new TextEncoder().encode(native.sent.at(-1).frame));
  assert.equal(command.type, 'selection.start');
  assert.equal(command.payload.compartment, 1);
  assert.equal(command.controlLeaseId, leaseId);
  connection.suspend();
  assert.equal(connection.snapshot().phase, 'disconnected');
});

test('malformed, duplicate, oversized and wrong-device snapshots close native control', async () => {
  for (const bad of [
    '{"protocolVersion":1,"protocolVersion":1,"type":"state.snapshot"}',
    JSON.stringify({ ...fixture, readyMask: 1.5 }),
    JSON.stringify({ ...fixture, deviceId: 'another-kufr' }),
    JSON.stringify({ ...fixture, additionalField: '<img onerror=alert(1)>' }),
    ' '.repeat(8193),
  ]) {
    const native = bridge();
    const connection = new ProtocolDeviceConnection(() => {}, {
      source: 'native-device', deviceId: fixture.deviceId,
      createSocket: () => new NativeControlSocket(native),
      parseFrame: (frame) => validateControlMessage(new TextEncoder().encode(frame)),
    });
    await settle();
    native.emit('open');
    native.emit('frame', bad);
    assert.equal(connection.snapshot().phase, 'disconnected');
    assert.equal(connection.acquire(), false);
    assert.match(connection.snapshot().status, /zastaveno/);
    connection.suspend();
  }
});

test('native reconnect stops after three retries and an explicit connect starts a fresh attempt', async () => {
  timers.clear();
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
  });
  await settle();
  for (const baseDelay of [500, 1000, 2000]) {
    native.emit('closed');
    assert.equal(connection.snapshot().controlMode, 'observer');
    const [id, timer] = [...timers.entries()].at(-1);
    assert.ok(timer.milliseconds >= baseDelay * 0.8 && timer.milliseconds <= baseDelay * 1.2);
    timers.delete(id);
    timer.callback();
    await settle();
  }
  native.emit('closed');
  assert.equal(timers.size, 0);
  assert.match(connection.snapshot().status, /zastaveno/);
  connection.connect();
  await settle();
  native.emit('closed');
  assert.equal(timers.size, 1);
  connection.suspend();
  assert.equal(timers.size, 0);
});

test('native permanent failures stop retries and only allowlisted reasons reach the UI', async () => {
  for (const reason of ['certificate_mismatch', 'authorization_rejected', 'protocol_error', 'tls_failed']) {
    timers.clear();
    const native = bridge();
    const connection = new ProtocolDeviceConnection(() => {}, {
      source: 'native-device', deviceId: fixture.deviceId,
      createSocket: () => new NativeControlSocket(native),
    });
    await settle();
    native.emit('closed', undefined, undefined, reason);
    assert.equal(timers.size, 0);
    assert.match(connection.snapshot().status, /zastaveno/);
    assert.doesNotMatch(connection.snapshot().status, /simulátor/);
    connection.suspend();
  }
  timers.clear();
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
  });
  await settle();
  native.emit('closed', undefined, undefined, 'network_unavailable');
  assert.match(connection.snapshot().status, /síť/);
  assert.equal(timers.size, 1);
  const [id, timer] = [...timers.entries()].at(-1);
  timers.delete(id); timer.callback();
  await settle();
  native.emit('closed', undefined, undefined, 'secret-token-or-url');
  assert.doesNotMatch(connection.snapshot().status, /secret-token-or-url/);
  assert.equal(timers.size, 1);
  connection.suspend();
});

test('brief welcome connections cannot reset the retry limit but five seconds of snapshots can', async () => {
  timers.clear();
  now = 0;
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
    parseFrame: frame => validateControlMessage(new TextEncoder().encode(frame)),
  });
  const welcome = async () => {
    await settle(); native.emit('open'); await settle();
    const hello = JSON.parse(native.sent.at(-1).frame);
    native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'welcome',
      requestMessageId: hello.messageId,
      snapshot: { ...fixture, controlMode: 'standalone', controlLeaseId: null, activeCommandId: null },
    }));
  };
  for (let attempt = 0; attempt < 4; attempt++) {
    await welcome();
    now += 100;
    native.emit('closed');
    if (attempt < 3) {
      const [id, timer] = [...timers.entries()].at(-1);
      timers.delete(id); timer.callback();
    }
  }
  assert.equal(timers.size, 0);
  connection.connect();
  await welcome();
  native.emit('closed');
  const [retryId, retryTimer] = [...timers.entries()].at(-1);
  timers.delete(retryId); retryTimer.callback();
  await welcome();
  for (let seconds = 1; seconds <= 5; seconds++) {
    now += 1000;
    native.emit('frame', JSON.stringify({ ...fixture, controlMode: 'standalone', controlLeaseId: null,
      activeCommandId: null, eventSequence: fixture.eventSequence + seconds, deviceUptimeMs: fixture.deviceUptimeMs + seconds * 1000 }));
  }
  native.emit('closed');
  const retry = [...timers.values()].at(-1);
  assert.ok(retry.milliseconds >= 400 && retry.milliseconds <= 600);
  connection.suspend();
});

test('native voice settings and start use schema-validated device confirmation without a lease', async () => {
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
    parseFrame: frame => validateControlMessage(new TextEncoder().encode(frame)),
  });
  await settle();
  native.emit('open');
  await settle();
  const hello = JSON.parse(native.sent.at(-1).frame);
  const voice = { available: true, mode: 'off', voice: 'marin', phase: 'idle', error: null };
  native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'welcome', requestMessageId: hello.messageId,
    snapshot: { ...fixture, controlMode: 'standalone', controlLeaseId: null, activeCommandId: null, voice },
  }));
  assert.equal(connection.configureVoice('realtime', 'cedar'), true);
  await settle();
  const configure = validateControlMessage(new TextEncoder().encode(native.sent.at(-1).frame));
  assert.equal(configure.type, 'voice.configure');
  assert.equal(connection.snapshot().voice.mode, 'off');
  native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'voice.ack', bootId: fixture.bootId,
    requestMessageId: configure.messageId, accepted: true, voice: { ...voice, mode: 'realtime', voice: 'cedar' },
  }));
  assert.equal(connection.snapshot().voice.canStart, true);
  assert.equal(connection.startVoice(), true);
  await settle();
  const start = validateControlMessage(new TextEncoder().encode(native.sent.at(-1).frame));
  assert.equal(start.type, 'voice.start');
  assert.equal(start.expectedRevision, fixture.revision);
  assert.equal('controlLeaseId' in start, false);
  native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'voice.ack', bootId: fixture.bootId,
    requestMessageId: start.messageId, accepted: true, voice: { ...voice, mode: 'realtime', voice: 'cedar', phase: 'listening' },
  }));
  assert.equal(connection.snapshot().voice.canStop, true);
  assert.equal(connection.stopVoice(), true);
  await settle();
  assert.equal(validateControlMessage(new TextEncoder().encode(native.sent.at(-1).frame)).type, 'voice.stop');
  connection.suspend();
});


test('native camera consent crosses the strict schema only for an explicit start', async () => {
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
    parseFrame: frame => validateControlMessage(new TextEncoder().encode(frame)),
  });
  await settle(); native.emit('open'); await settle();
  const hello = JSON.parse(native.sent.at(-1).frame);
  const voice = { available: true, cameraAvailable: true, photoWarning: null,
    mode: 'realtime', voice: 'marin', phase: 'idle', error: null };
  native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'welcome', requestMessageId: hello.messageId,
    snapshot: { ...fixture, controlMode: 'standalone', controlLeaseId: null, activeCommandId: null, voice },
  }));
  assert.equal(connection.snapshot().voice.cameraAvailable, true);
  assert.equal(connection.startVoice(true), true); await settle();
  const start = validateControlMessage(new TextEncoder().encode(native.sent.at(-1).frame));
  assert.equal(start.imageConsent, true);
  native.emit('frame', JSON.stringify({ protocolVersion: 1, type: 'voice.ack', bootId: fixture.bootId,
    requestMessageId: start.messageId, accepted: true, voice: { ...voice, phase: 'photo_wait' },
  }));
  assert.equal(connection.snapshot().voice.phase, 'photo_wait');
  assert.equal(connection.snapshot().voice.canStop, true);
  const next = { ...fixture, controlMode: 'standalone', controlLeaseId: null, activeCommandId: null,
    eventSequence: fixture.eventSequence + 1, voice: { ...voice, phase: 'listening', photoWarning: 'photo_failed' } };
  native.emit('frame', JSON.stringify(next));
  assert.match(connection.snapshot().voice.status, /bez ní/);
  assert.equal(connection.snapshot().voice.error, null);
  connection.suspend();
});


test('route is captured before listener registration and remains bound after BLE hint changes', async () => {
  const native = bridge();
  let finishListener;
  native.addListener = () => new Promise(resolve => { finishListener = resolve; });
  const route = { host: '192.168.0.62', deviceId: 'kufr-1' };
  const socket = new NativeControlSocket(native, route);
  // Model BLE loss/new route while registration is pending. The old identity
  // hostname must not replace the route captured for this attempt.
  route.host = 'kufr-old.local';
  route.deviceId = 'kufr-2';
  finishListener({ remove: async () => { native.removed++; } });
  await settle();
  assert.equal(native.connected[0].host, '192.168.0.62');
  assert.equal(native.connected[0].deviceId, 'kufr-1');
  assert.deepEqual(Object.keys(native.connected[0]).sort(), ['connectionId', 'deviceId', 'host']);
  socket.close();
});
