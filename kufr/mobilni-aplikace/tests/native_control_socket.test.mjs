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
    emit(state, frame, connectionId = native.connected.at(-1)?.connectionId) {
      for (const callback of callbacks) { callback({ connectionId, state, frame }); }
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
    connection.suspend();
  }
});

test('native reconnect backs off, remains an observer and explicit disconnect cancels retries', async () => {
  timers.clear();
  const native = bridge();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId: fixture.deviceId,
    createSocket: () => new NativeControlSocket(native),
  });
  await settle();
  for (const baseDelay of [500, 1000, 2000, 4000, 8000, 8000]) {
    native.emit('closed');
    assert.equal(connection.snapshot().controlMode, 'observer');
    const [id, timer] = [...timers.entries()].at(-1);
    assert.ok(timer.milliseconds >= baseDelay * 0.8 && timer.milliseconds <= baseDelay * 1.2);
    timers.delete(id);
    timer.callback();
    await settle();
  }
  native.emit('closed');
  connection.suspend();
  assert.equal(timers.size, 0);
});
