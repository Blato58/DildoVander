import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { test } from 'node:test';

import { loadDeviceModule } from './load-test-module.mjs';
import { validateControlMessage } from '../../contracts/control-validator.mjs';

let now = 0;
let nextTimer = 1;
const timers = new Map();
const sockets = [];

Object.defineProperty(globalThis, 'performance', {
  value: { now: () => now },
  configurable: true,
});
Object.defineProperty(globalThis, 'crypto', {
  value: { randomUUID },
  configurable: true,
});
globalThis.document = { hidden: false };
globalThis.window = {
  location: { host: '127.0.0.1:5174' },
  setTimeout(callback, delay) {
    const id = nextTimer++;
    timers.set(id, { at: now + delay, callback });
    return id;
  },
  clearTimeout(id) {
    timers.delete(id);
  },
};

class FakeSocket {
  static OPEN = 1;
  static CLOSED = 3;

  constructor() {
    this.readyState = 0;
    this.listeners = new Map();
    this.sent = [];
    sockets.push(this);
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  emit(type, event = {}) {
    for (const listener of this.listeners.get(type) ?? []) {
      listener(event);
    }
  }

  open() {
    this.readyState = FakeSocket.OPEN;
    this.emit('open');
  }

  message(message) {
    this.emit('message', { data: JSON.stringify(message) });
  }

  send(encoded) {
    this.sent.push(JSON.parse(encoded));
  }

  close() {
    if (this.readyState !== FakeSocket.CLOSED) {
      this.readyState = FakeSocket.CLOSED;
      this.emit('close');
    }
  }
}

globalThis.WebSocket = FakeSocket;

function advance(milliseconds) {
  const target = now + milliseconds;
  while (true) {
    const due = [...timers.entries()]
      .filter(([, timer]) => timer.at <= target)
      .sort((left, right) => left[1].at - right[1].at)[0];
    if (!due) {
      break;
    }
    now = due[1].at;
    timers.delete(due[0]);
    due[1].callback();
  }
  now = target;
}

const { SimulatorDeviceConnection, ProtocolDeviceConnection } = await loadDeviceModule('SimulatorDeviceConnection');

const bootId = randomUUID();
const deviceId = 'kufr-001';

function snapshot(overrides = {}) {
  return {
    protocolVersion: 1,
    type: 'state.snapshot',
    deviceId,
    bootId,
    deviceUptimeMs: now,
    eventSequence: 1,
    revision: 1,
    controlMode: 'standalone',
    controlLeaseId: null,
    phase: 'ready',
    activeCommandId: null,
    readyMask: 63,
    faults: [],
    lastResult: null,
    ...overrides,
  };
}

function startConnection() {
  const connection = new SimulatorDeviceConnection(() => {});
  const socket = sockets.at(-1);
  socket.open();
  socket.message({
    protocolVersion: 1,
    type: 'welcome',
    snapshot: snapshot(),
  });
  return { connection, socket };
}

test('Lab carrier requires live native capability and clears capability evidence on disconnect', () => {
  const socket = new FakeSocket();
  const connection = new ProtocolDeviceConnection(() => {}, { source: 'native-device', deviceId, createSocket: () => socket });
  const voice = { available: true, mode: 'realtime', voice: 'marin', phase: 'idle', error: null };
  socket.open();
  socket.message({ protocolVersion: 1, type: 'welcome', snapshot: snapshot({ voice, capabilities: [] }) });
  connection.snapshot().capabilities.push('voiceTestContext');
  assert.equal(connection.startVoice(false, 'A'.repeat(43)), false);
  socket.message(snapshot({ eventSequence: 2, voice, capabilities: ['voiceTestContext'] }));
  assert.equal(connection.startVoice(false, 'A'.repeat(43)), true);
  assert.equal(socket.sent.at(-1).testContext, 'A'.repeat(43));
  connection.suspend();
  assert.deepEqual(connection.snapshot().capabilities, []);
  const simulation = startConnection();
  simulation.socket.message(snapshot({ eventSequence: 2, voice, capabilities: ['voiceTestContext'] }));
  assert.equal(simulation.connection.startVoice(false, 'A'.repeat(43)), false);
  simulation.connection.suspend();
});

test('real-device transport never sends retired text or cancel frames even when an old snapshot advertises text', () => {
  const socket = new FakeSocket();
  const connection = new ProtocolDeviceConnection(() => {}, { source: 'native-device', deviceId, createSocket: () => socket });
  socket.open();
  socket.message({ protocolVersion: 1, type: 'welcome', snapshot: snapshot({ controlMode: 'manual', controlLeaseId: randomUUID(),
    capabilities: ['conversationText'], conversation: { conversationId: randomUUID(), generation: 1, activeTurnId: null } }) });
  assert.equal(connection.snapshot().conversation.canSubmit, false);
  assert.equal(connection.submitText('retired text'), false); assert.equal(connection.cancelConversation(), false);
  assert.equal(socket.sent.some(message => message.type === 'conversation.text' || message.type === 'conversation.cancel'), false);
  connection.suspend();
});

test('dance confirmation blocks voice/manual controls and reconnect never replays music', () => {
  const { connection, socket } = startConnection();
  const dance = { available: true, phase: 'idle', track: null, positionMs: 0, durationMs: 0, bpm: 0, error: null };
  const voice = { available: true, mode: 'realtime', voice: 'marin', phase: 'idle', error: null };
  socket.message(snapshot({ eventSequence: 2, dance, voice }));
  assert.equal(connection.startDance(), true);
  assert.equal(connection.acquire(), false);
  assert.equal(connection.startVoice(), false);
  const request = socket.sent.at(-1);
  socket.message({ protocolVersion: 1, type: 'dance.ack', bootId, requestMessageId: request.messageId,
    accepted: true, dance: { ...dance, phase: 'starting' } });
  assert.equal(connection.snapshot().phase, 'running');
  assert.equal(connection.snapshot().voice.canStart, false);
  socket.message(snapshot({ eventSequence: 3, phase: 'dancing', dance: { ...dance, phase: 'playing',
    track: 'song.wav', positionMs: 1000, durationMs: 10000, bpm: 120 }, voice }));
  assert.equal(connection.snapshot().dance.track, 'song.wav');
  assert.equal(connection.startDance(), false);
  assert.equal(connection.stopDance(), true);
  const starts = socket.sent.filter(message => message.type === 'dance.start').length;
  socket.close();
  assert.equal(connection.snapshot().dance.canStart, false);
  assert.equal(starts, 1);
});

function acquire(connection, socket, leaseId = randomUUID(), eventSequence = 2) {
  assert.equal(connection.acquire(), true);
  socket.message(snapshot({
    eventSequence,
    controlMode: 'manual',
    controlLeaseId: leaseId,
  }));
  assert.equal(connection.snapshot().controlMode, 'manual');
  return leaseId;
}

test('debug telemetry reflects accepted samples, reboot and disconnect without exposing a lease', () => {
  const { connection, socket } = startConnection();
  const initial = connection.snapshot().diagnostics;
  assert.equal(initial.bootId, bootId);
  assert.equal(initial.eventSequence, 1);
  assert.equal(initial.deviceUptimeMs, now);
  assert.equal(initial.devicePhase, 'ready');
  assert(!('controlLeaseId' in initial));
  const changedBootId = randomUUID();
  socket.message(snapshot({ bootId: changedBootId, deviceUptimeMs: 100, eventSequence: 0, phase: 'recoveryRequired' }));
  assert.equal(connection.snapshot().diagnostics.bootId, changedBootId);
  assert.equal(connection.snapshot().diagnostics.deviceUptimeMs, 100);
  assert.equal(connection.snapshot().diagnostics.devicePhase, 'recoveryRequired');
  socket.close();
  assert.equal(connection.snapshot().diagnostics, undefined);
  connection.suspend();
});

function preflight(connection, socket, compartment, kind = 'select') {
  assert.equal(kind === 'select'
    ? connection.select(compartment)
    : connection.confirmReload(compartment), true);
  const heartbeat = socket.sent.at(-1);
  assert.equal(heartbeat.type, 'heartbeat');
  socket.message({
    protocolVersion: 1,
    type: 'heartbeat.ack',
    requestMessageId: heartbeat.messageId,
    bootId,
    controlLeaseId: heartbeat.controlLeaseId,
    revision: 1,
    deviceUptimeMs: now,
  });
  return socket.sent.at(-1);
}

test('new lease starts a fresh sequence and a command retries unchanged', () => {
  const { connection, socket } = startConnection();
  acquire(connection, socket);
  const first = preflight(connection, socket, 1);
  assert.equal(first.type, 'selection.start');
  assert.equal(first.sequence, 1);
  advance(500);
  assert.deepEqual(socket.sent.at(-1), first);
  socket.message({
    protocolVersion: 1,
    type: 'command.ack',
    commandId: first.commandId,
    bootId,
    status: 'accepted',
  });
  socket.message(snapshot({
    eventSequence: 3,
    controlMode: 'manual',
    controlLeaseId: first.controlLeaseId,
    readyMask: 62,
    revision: 2,
    lastResult: { commandId: first.commandId, status: 'completed' },
  }));
  assert.equal(connection.snapshot().readyMask, 62);
  connection.release();
  socket.message(snapshot({ eventSequence: 4, revision: 2, readyMask: 62 }));
  const nextLease = randomUUID();
  acquire(connection, socket, nextLease, 5);
  socket.message(snapshot({
    eventSequence: 5,
    revision: 2,
    readyMask: 62,
    controlMode: 'manual',
    controlLeaseId: nextLease,
  }));
  assert.equal(connection.select(2), true);
  const heartbeat = socket.sent.at(-1);
  socket.message({
    protocolVersion: 1,
    type: 'heartbeat.ack',
    requestMessageId: heartbeat.messageId,
    bootId,
    controlLeaseId: nextLease,
    revision: 2,
    deviceUptimeMs: now,
  });
  assert.equal(socket.sent.at(-1).sequence, 1);
  connection.suspend();
});

test('lost result fetches state, then closes if still unresolved', () => {
  const { connection, socket } = startConnection();
  acquire(connection, socket);
  const command = preflight(connection, socket, 1);
  socket.message({
    protocolVersion: 1,
    type: 'command.ack',
    commandId: command.commandId,
    bootId,
    status: 'accepted',
  });
  // Keep receiving heartbeats so only the missing result timer fires.
  advance(2900);
  connection.heartbeat();
  const heartbeat = socket.sent.at(-1);
  socket.message({
    protocolVersion: 1,
    type: 'heartbeat.ack',
    requestMessageId: heartbeat.messageId,
    bootId,
    controlLeaseId: command.controlLeaseId,
    revision: 1,
    deviceUptimeMs: now,
  });
  advance(2900);
  connection.heartbeat();
  const secondHeartbeat = socket.sent.at(-1);
  socket.message({
    protocolVersion: 1,
    type: 'heartbeat.ack',
    requestMessageId: secondHeartbeat.messageId,
    bootId,
    controlLeaseId: command.controlLeaseId,
    revision: 1,
    deviceUptimeMs: now,
  });
  advance(200);
  assert.equal(socket.sent.at(-1).type, 'state.get');
  advance(3000);
  assert.equal(connection.snapshot().phase, 'disconnected');
  connection.suspend();
});

test('uncertain reload reconciles from mask; uncertain selection blocks control', () => {
  const { connection, socket } = startConnection();
  const leaseId = acquire(connection, socket);
  socket.message(snapshot({
    eventSequence: 3,
    controlMode: 'manual',
    controlLeaseId: leaseId,
    readyMask: 62,
  }));
  const reload = preflight(connection, socket, 1, 'reload');
  assert.equal(reload.type, 'inventory.confirmReload');
  socket.close();
  advance(1500);
  const reconnected = sockets.at(-1);
  reconnected.open();
  reconnected.message({
    protocolVersion: 1,
    type: 'welcome',
    snapshot: snapshot({ readyMask: 63, revision: 2 }),
  });
  assert.equal(connection.snapshot().phase, 'ready');
  connection.suspend();

  const second = startConnection();
  acquire(second.connection, second.socket);
  preflight(second.connection, second.socket, 1);
  second.socket.close();
  advance(1500);
  const secondReconnect = sockets.at(-1);
  secondReconnect.open();
  secondReconnect.message({
    protocolVersion: 1,
    type: 'welcome',
    snapshot: snapshot(),
  });
  assert.equal(second.connection.snapshot().phase, 'stopped');
  assert.equal(second.connection.acquire(), false);
  second.connection.suspend();
});

test('changed heartbeat revision pauses commands until a new snapshot', () => {
  const { connection, socket } = startConnection();
  const leaseId = acquire(connection, socket);
  connection.heartbeat();
  const heartbeat = socket.sent.at(-1);
  socket.message({
    protocolVersion: 1,
    type: 'heartbeat.ack',
    requestMessageId: heartbeat.messageId,
    bootId,
    controlLeaseId: leaseId,
    revision: 2,
    deviceUptimeMs: now,
  });
  assert.equal(socket.sent.at(-1).type, 'state.get');
  assert.equal(connection.select(1), false);
  socket.message(snapshot({
    eventSequence: 3,
    revision: 2,
    readyMask: 62,
    controlMode: 'manual',
    controlLeaseId: leaseId,
  }));
  assert.equal(connection.snapshot().busy, false);
  assert.equal(connection.snapshot().readyMask, 62);
  connection.suspend();
});

function startNativeOffline() {
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId, createSocket: () => new FakeSocket(),
  });
  const socket = sockets.at(-1);
  const lease = randomUUID();
  socket.open();
  socket.message({ protocolVersion: 1, type: 'welcome', snapshot: snapshot({ controlMode: 'manual', controlLeaseId: lease }) });
  return { connection, socket, lease };
}
function offlineStatus(overrides = {}) {
  return { state: 'ready', armed: false, quiet: false, generation: 17, readyMask: 63,
    outboxCount: 0, catalogVersion: 'ollivander-private-party-v1', bankVersion: 'synthetic-fixture',
    audioBytes: 1, audioCapacityBytes: 2, streamWorkingBytes: 2, missing: [], ambientEnabled: false,
    bankVoice: 'fixture', voiceCompatible: true, ...overrides };
}
function offlineAck(socket, request, overrides = {}) {
  socket.message({ protocolVersion: 1, type: 'offline.ack', bootId, requestMessageId: request.messageId,
    accepted: true, errorCode: null, offline: offlineStatus(), ...overrides });
}

test('native offline Show requires preflight and exact lease/generation/revision without acquiring authority', async () => {
  const { connection, socket, lease } = startNativeOffline();
  await assert.rejects(connection.requestOffline('offline_arm'), /preflight/);
  const preflight = connection.requestOffline('offline_preflight');
  const request = socket.sent.at(-1);
  assert.equal(request.type, 'offline_preflight');
  assert.equal(validateControlMessage(new TextEncoder().encode(JSON.stringify(request))).type, 'offline_preflight');
  offlineAck(socket, request, { bootId: randomUUID() });
  assert.equal(socket.sent.filter(frame => frame.type === 'offline_arm').length, 0);
  // An outbox-shaped reply may not satisfy an unrelated preflight.
  offlineAck(socket, request, { type: 'offline.outbox', records: [] });
  offlineAck(socket, request);
  assert.equal((await preflight).generation, 17);
  const arm = connection.requestOffline('offline_arm');
  const armRequest = socket.sent.at(-1);
  assert.equal(armRequest.controlLeaseId, lease);
  assert.equal(armRequest.generation, 17);
  assert.equal(armRequest.expectedRevision, 1);
  assert.equal(validateControlMessage(new TextEncoder().encode(JSON.stringify(armRequest))).type, 'offline_arm');
  assert.equal(socket.sent.some(frame => frame.type === 'control.acquire'), false);
  offlineAck(socket, armRequest, { offline: offlineStatus({ armed: true }) });
  await arm;
  for (const compartment of [undefined, 0, 7, 1.5]) await assert.rejects(connection.requestOffline('offline_start', compartment), /1–6/);
  const started = connection.requestOffline('offline_start', 4, undefined, { shortVariant: true });
  assert.equal(socket.sent.at(-1).compartment, 4);
  assert.equal(socket.sent.at(-1).shortVariant, true);
  assert.equal(validateControlMessage(new TextEncoder().encode(JSON.stringify(socket.sent.at(-1)))).type, 'offline_start');
  offlineAck(socket, socket.sent.at(-1), { offline: offlineStatus({ armed: true }) });
  await started;
  connection.suspend();
});

test('offline commands reject simulated targets and timeout never retries or preserves Arm authority', async () => {
  const simulated = startConnection();
  await assert.rejects(simulated.connection.requestOffline('offline_preflight'), /autentizovaná/);
  assert.equal(simulated.socket.sent.some(frame => frame.type.startsWith('offline_')), false);
  simulated.connection.suspend();
  const { connection, socket, lease } = startNativeOffline();
  const preflight = connection.requestOffline('offline_preflight');
  offlineAck(socket, socket.sent.at(-1)); await preflight;
  const arm = connection.requestOffline('offline_arm');
  const rejected = assert.rejects(arm, /není známý/);
  advance(2000);
  socket.message(snapshot({ controlMode: 'manual', controlLeaseId: lease, eventSequence: 2 }));
  advance(2001); await rejected;
  assert.equal(socket.sent.filter(frame => frame.type === 'offline_arm').length, 1);
  await assert.rejects(connection.requestOffline('offline_arm'), /preflight/);
  connection.suspend();
});

test('offline outbox acknowledgement is explicit and Stop invalidates pending results', async () => {
  const { connection, socket } = startNativeOffline();
  await assert.rejects(connection.requestOffline('offline_sync_ack', undefined, 'not-an-id'), /přesné ID/);
  const outbox = connection.requestOffline('offline_outbox');
  const request = socket.sent.at(-1);
  offlineAck(socket, request, { type: 'offline.outbox', records: [] });
  assert.deepEqual((await outbox).records, []);
  const pending = connection.requestOffline('quiet', undefined, undefined, { enabled: true });
  const rejected = assert.rejects(pending, /Stop/);
  connection.stop(); await rejected;
  offlineAck(socket, socket.sent.findLast(frame => frame.type === 'quiet'));
  assert.equal(socket.sent.filter(frame => frame.type === 'quiet').length, 1);
  assert.equal(socket.sent.at(-1).type, 'safety.stop');
  connection.suspend();
});

test('last observed local time changes only on accepted snapshots or matched heartbeat replies', () => {
  const dateNow = Date.now;
  let wallClock = 1_700_000_000_000;
  Date.now = () => wallClock;
  const socket = new FakeSocket();
  const connection = new ProtocolDeviceConnection(() => {}, {
    source: 'native-device', deviceId, autoConnect: false, createSocket: () => socket,
  });
  try {
    assert.equal(connection.snapshot().diagnostics, undefined);
    connection.connect(); socket.open();
    assert.equal(connection.snapshot().diagnostics, undefined);
    const lease = randomUUID();
    socket.message({ protocolVersion: 1, type: 'welcome', snapshot: snapshot({ controlMode: 'manual', controlLeaseId: lease }) });
    assert.equal(connection.snapshot().diagnostics.deviceId, deviceId);
    assert.equal(connection.snapshot().diagnostics.lastObservedAtMs, wallClock);
    const observed = wallClock;
    wallClock += 1_000;
    connection.release(); // local intent is not evidence of a fresh device response
    assert.equal(connection.snapshot().diagnostics.lastObservedAtMs, observed);
    socket.message(snapshot({ eventSequence: 0, controlMode: 'manual', controlLeaseId: lease })); // delayed, superseded state
    assert.equal(connection.snapshot().diagnostics.lastObservedAtMs, observed);
    connection.heartbeat();
    const heartbeat = socket.sent.at(-1);
    const reply = { protocolVersion: 1, type: 'heartbeat.ack', requestMessageId: heartbeat.messageId,
      bootId, controlLeaseId: lease, revision: 1, deviceUptimeMs: now };
    socket.message({ ...reply, requestMessageId: randomUUID() });
    assert.equal(connection.snapshot().diagnostics.lastObservedAtMs, observed);
    socket.message(reply);
    assert.equal(connection.snapshot().diagnostics.lastObservedAtMs, wallClock);
  } finally { connection.suspend(); Date.now = dateNow; }
});
