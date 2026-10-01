import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { test } from 'node:test';

import { loadDeviceModule } from './load-test-module.mjs';

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

const { SimulatorDeviceConnection } = await loadDeviceModule('SimulatorDeviceConnection');

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
