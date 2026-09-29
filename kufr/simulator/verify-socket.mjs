import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import WebSocket from 'ws';
import { validateControlMessage } from '../contracts/control-validator.mjs';
import { createSimulatorServer } from './server.mjs';

const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!bridgeExecutable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const temporaryDirectory = await mkdtemp(join(tmpdir(), 'kufr-wss-test-'));
const certificatePath = join(temporaryDirectory, 'certificate.pem');
const privateKeyPath = join(temporaryDirectory, 'private-key.pem');
let simulator;
let client;

function createClient(url, certificate, token) {
  const socket = new WebSocket(url, {
    ca: certificate,
    rejectUnauthorized: true,
    headers: { Authorization: `Bearer ${token}` },
    perMessageDeflate: false,
  });
  const received = [];
  const waiters = [];
  socket.on('message', (bytes) => {
    const message = validateControlMessage(bytes);
    const waiter = waiters.find((candidate) => candidate.type === message.type);
    if (waiter) {
      waiters.splice(waiters.indexOf(waiter), 1);
      waiter.resolve(message);
    } else {
      received.push(message);
    }
  });

  return {
    socket,
    next(type) {
      const existing = received.find((message) => message.type === type);
      if (existing) {
        received.splice(received.indexOf(existing), 1);
        return Promise.resolve(existing);
      }
      return new Promise((resolve, reject) => {
        const waiter = { type, resolve, reject };
        waiters.push(waiter);
        setTimeout(() => {
          if (waiters.includes(waiter)) {
            waiters.splice(waiters.indexOf(waiter), 1);
            reject(new Error(`timed out waiting for ${type}`));
          }
        }, 7000).unref();
      });
    },
    latest(type) {
      const matches = received.filter((message) => message.type === type);
      for (const message of matches) {
        received.splice(received.indexOf(message), 1);
      }
      return matches.at(-1);
    },
    send(message) {
      socket.send(JSON.stringify(message));
    },
  };
}

try {
  execFileSync(process.env.OPENSSL ?? 'openssl', [
    'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
    '-keyout', privateKeyPath, '-out', certificatePath,
    '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1',
  ], { stdio: 'ignore' });

  const certificate = await readFile(certificatePath);
  const privateKey = await readFile(privateKeyPath);
  const token = randomBytes(32).toString('hex');
  const timeline = [];
  simulator = await createSimulatorServer({
    certificate,
    privateKey,
    token,
    bridgeExecutable,
    onTimeline: (event) => timeline.push(event),
  });
  const url = `wss://127.0.0.1:${simulator.port}/v1/control`;

  await new Promise((resolve, reject) => {
    const unauthorized = new WebSocket(url, { ca: certificate, rejectUnauthorized: true });
    unauthorized.on('unexpected-response', (_request, response) => {
      assert.equal(response.statusCode, 401);
      response.resume();
      resolve();
    });
    unauthorized.on('error', reject);
  });

  client = createClient(url, certificate, token);
  await new Promise((resolve, reject) => {
    client.socket.once('open', resolve);
    client.socket.once('error', reject);
  });

  const helloId = randomUUID();
  client.send({ protocolVersion: 1, type: 'hello', messageId: helloId, deviceId: 'kufr-001' });
  const welcome = await client.next('welcome');
  assert.equal(welcome.requestMessageId, helloId);
  assert.equal(welcome.snapshot.controlMode, 'standalone');

  client.send({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  const acquired = await client.next('state.snapshot');
  assert.equal(acquired.controlMode, 'manual');
  assert.ok(acquired.controlLeaseId);

  const command = {
    protocolVersion: 1,
    type: 'selection.start',
    commandId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    sequence: 1,
    expectedRevision: acquired.revision,
    issuedAtDeviceMs: acquired.deviceUptimeMs,
    maxAgeMs: 2000,
    payload: { compartment: 4, selectionSource: 'manual' },
  };
  client.send(command);
  const firstAck = await client.next('command.ack');
  assert.equal(firstAck.status, 'accepted');
  client.send(command);
  const repeatedAck = await client.next('command.ack');
  assert.equal(repeatedAck.status, 'accepted');
  assert.equal(repeatedAck.commandId, command.commandId);
  assert.ok(repeatedAck.eventSequence > firstAck.eventSequence);
  const result = await client.next('selection.result');
  assert.equal(result.status, 'completed');
  assert.equal(result.compartment, 4);
  assert.equal(timeline.filter((event) => event.event === 'release_latch').length, 1);

  client.send({
    protocolVersion: 1,
    type: 'state.get',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  let state;
  do {
    state = await client.next('state.snapshot');
  } while (state.lastResult?.status !== 'completed');
  assert.equal(state.readyMask, 55);

  const secondCommand = {
    ...command,
    commandId: randomUUID(),
    sequence: 2,
    expectedRevision: state.revision,
    issuedAtDeviceMs: state.deviceUptimeMs,
    payload: { compartment: 5, selectionSource: 'manual' },
  };
  client.send(secondCommand);
  const secondAck = await client.next('command.ack');
  assert.equal(secondAck.status, 'accepted');
  for (let index = 0; index < 20; index += 1) {
    client.send({
      protocolVersion: 1,
      type: 'state.get',
      messageId: randomUUID(),
      deviceId: 'kufr-001',
      bootId: simulator.bootId,
    });
  }
  client.send({
    protocolVersion: 1,
    type: 'safety.reset',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    inspectionConfirmed: true,
  });
  client.send({
    protocolVersion: 1,
    type: 'safety.stop',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  const stoppedResult = await client.next('selection.result');
  assert.equal(stoppedResult.commandId, secondCommand.commandId);
  assert.equal(stoppedResult.status, 'outcome_unknown');
  assert.equal(timeline.filter((event) => event.event === 'release_latch').length, 1);
  assert.ok(timeline.some((event) => event.event === 'actuators_off'));
  await new Promise((resolve) => setTimeout(resolve, 250));
  client.send({
    protocolVersion: 1,
    type: 'state.get',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  await new Promise((resolve) => setTimeout(resolve, 250));
  const stateAfterStop = client.latest('state.snapshot');
  assert.ok(stateAfterStop);
  assert.equal(stateAfterStop.phase, 'stopped');
  assert.equal(stateAfterStop.lastResult.status, 'outcome_unknown');

  client.socket.send('{"protocolVersion":1,"type":"state.get","type":"state.get"}');
  await new Promise((resolve) => client.socket.once('close', resolve));
  console.log('WSS: TLS/auth, hello, lease, ACK retry, one release, priority Stop, snapshot and strict input passed');
} finally {
  client?.socket.terminate();
  await simulator?.close();
  await rm(temporaryDirectory, { recursive: true, force: true });
}
