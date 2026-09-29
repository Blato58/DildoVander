import assert from 'node:assert/strict';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, sep } from 'node:path';

import WebSocket from 'ws';
import { validateControlMessage } from '../contracts/control-validator.mjs';
import { encodeMediaFrame } from '../contracts/media-frame-v1.mjs';
import { validateMediaControlMessage } from '../contracts/media-validator.mjs';
import { createSimulatorServer } from './server.mjs';

const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!bridgeExecutable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const temporaryDirectory = await mkdtemp(join(tmpdir(), 'kufr-media-wss-test-'));
let simulator;
let controlSocket;
let mediaSocket;

function inbox(socket, validator) {
  const queued = [];
  const waiting = [];
  socket.on('message', (bytes, isBinary) => {
    assert.equal(isBinary, false);
    const message = validator(bytes);
    const waiter = waiting.find((candidate) => candidate.type === message.type);
    if (waiter) {
      waiting.splice(waiting.indexOf(waiter), 1);
      waiter.resolve(message);
    } else {
      queued.push(message);
    }
  });
  return {
    next(type) {
      const existing = queued.find((message) => message.type === type);
      if (existing) {
        queued.splice(queued.indexOf(existing), 1);
        return Promise.resolve(existing);
      }
      return new Promise((resolve, reject) => {
        const waiter = { type, resolve };
        waiting.push(waiter);
        setTimeout(() => {
          if (waiting.includes(waiter)) {
            waiting.splice(waiting.indexOf(waiter), 1);
            reject(new Error(`timed out waiting for ${type}`));
          }
        }, 3000).unref();
      });
    },
  };
}

async function openSocket(url, certificate, token) {
  const socket = new WebSocket(url, {
    ca: certificate,
    rejectUnauthorized: true,
    headers: { Authorization: `Bearer ${token}` },
    perMessageDeflate: false,
  });
  await new Promise((resolve, reject) => {
    socket.once('open', resolve);
    socket.once('error', reject);
  });
  return socket;
}

function awaitClose(socket) {
  return new Promise((resolve) => socket.once('close', (code) => resolve(code)));
}

try {
  const certificatePath = join(temporaryDirectory, 'certificate.pem');
  const privateKeyPath = join(temporaryDirectory, 'private-key.pem');
  execFileSync(process.env.OPENSSL ?? 'openssl', [
    'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
    '-keyout', privateKeyPath, '-out', certificatePath,
    '-subj', '/CN=127.0.0.1', '-addext', 'subjectAltName=IP:127.0.0.1',
  ], { stdio: 'ignore' });
  const certificate = await readFile(certificatePath);
  const privateKey = await readFile(privateKeyPath);
  const token = randomBytes(32).toString('hex');
  simulator = await createSimulatorServer({
    certificate,
    privateKey,
    token,
    bridgeExecutable,
    journalPath: join(temporaryDirectory, 'journal.log'),
  });
  const baseUrl = `wss://127.0.0.1:${simulator.port}`;

  controlSocket = await openSocket(`${baseUrl}/v1/control`, certificate, token);
  const control = inbox(controlSocket, validateControlMessage);
  controlSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'hello',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
  }));
  await control.next('welcome');
  controlSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  }));
  const acquired = await control.next('state.snapshot');
  assert.equal(acquired.controlMode, 'manual');
  assert.equal(acquired.mediaTicket, undefined);
  const authorization = await control.next('media.authorize');
  assert.equal(authorization.controlLeaseId, acquired.controlLeaseId);
  const mediaTicket = authorization.mediaTicket;
  const mediaContext = {
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    conversationId: acquired.conversation.conversationId,
    generation: acquired.conversation.generation,
  };

  const rogueSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  const rogueClosed = awaitClose(rogueSocket);
  rogueSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'media.hello',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    mediaTicket: '0'.repeat(64),
    ...mediaContext,
  }));
  assert.equal(await rogueClosed, 1008);

  const staleSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  const staleClosed = awaitClose(staleSocket);
  staleSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'media.hello',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    mediaTicket,
    ...mediaContext,
    generation: mediaContext.generation + 1,
  }));
  assert.equal(await staleClosed, 1008);

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  let media = inbox(mediaSocket, validateMediaControlMessage);
  const helloId = randomUUID();
  await new Promise((resolve) => setTimeout(resolve, 100));
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'media.hello',
    messageId: helloId,
    deviceId: 'kufr-001',
    mediaTicket,
    ...mediaContext,
  }));
  const open = await media.next('media.open');
  assert.equal(open.requestMessageId, helloId);
  assert.equal(open.creditBytes, 16384);
  assert.equal(open.kind, 'pcmCapture');
  const chunks = Array.from({ length: 5 }, (_, index) =>
    Buffer.alloc(index === 4 ? 2816 : 4096, index + 1));
  mediaSocket.send(encodeMediaFrame({
    kind: 1, streamId: open.streamId, chunkSequence: 0, payload: chunks[0],
  }));
  mediaSocket.send(encodeMediaFrame({
    kind: 1, streamId: open.streamId, chunkSequence: 1, payload: chunks[1],
  }));
  const credit = await media.next('media.credit');
  assert.equal(credit.creditBytes, 8192);
  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'heartbeat', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
  }));
  await control.next('heartbeat.ack');
  for (let index = 2; index < chunks.length; index += 1) {
    mediaSocket.send(encodeMediaFrame({
      kind: 1, streamId: open.streamId, chunkSequence: index, payload: chunks[index],
    }));
  }
  const endId = randomUUID();
  const firstMediaClosed = awaitClose(mediaSocket);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1,
    type: 'media.end',
    messageId: endId,
    ...mediaContext,
    streamId: open.streamId,
    totalBytes: 19200,
    sha256: createHash('sha256').update(Buffer.concat(chunks)).digest('hex'),
  }));
  const received = await media.next('media.result');
  assert.equal(received.requestMessageId, endId);
  assert.equal(received.status, 'received');
  assert.equal(received.totalBytes, 19200);
  assert.equal(received.simulated, true);
  assert.equal(await firstMediaClosed, 1000);

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket, ...mediaContext,
  }));
  const secondOpen = await media.next('media.open');
  const secondMediaClosed = awaitClose(mediaSocket);
  mediaSocket.send(encodeMediaFrame({
    kind: 1, streamId: secondOpen.streamId, chunkSequence: 1,
    payload: Buffer.alloc(1280),
  }));
  assert.equal(await secondMediaClosed, 1008);

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket, ...mediaContext,
  }));
  const hashOpen = await media.next('media.open');
  for (let index = 0; index < chunks.length; index += 1) {
    mediaSocket.send(encodeMediaFrame({
      kind: 1, streamId: hashOpen.streamId, chunkSequence: index,
      payload: chunks[index],
    }));
  }
  const hashMediaClosed = awaitClose(mediaSocket);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.end', messageId: randomUUID(),
    ...mediaContext, streamId: hashOpen.streamId, totalBytes: 19200,
    sha256: '0'.repeat(64),
  }));
  assert.equal(await hashMediaClosed, 1008);

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket, ...mediaContext,
  }));
  const thirdOpen = await media.next('media.open');
  const thirdMediaClosed = awaitClose(mediaSocket);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.cancel', messageId: randomUUID(),
    ...mediaContext, streamId: thirdOpen.streamId,
  }));
  assert.equal((await media.next('media.result')).status, 'cancelled');
  assert.equal(await thirdMediaClosed, 1000);

  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'heartbeat', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
  }));
  await control.next('heartbeat.ack');

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket, ...mediaContext,
  }));
  await media.next('media.open');
  const timeoutMediaClosed = awaitClose(mediaSocket);
  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'heartbeat', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
  }));
  await control.next('heartbeat.ack');
  assert.equal(await timeoutMediaClosed, 1008);

  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket, ...mediaContext,
  }));
  await media.next('media.open');
  const finalMediaClosed = awaitClose(mediaSocket);
  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'control.release', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
  }));
  assert.equal(await finalMediaClosed, 1008);
  const released = await control.next('state.snapshot');
  assert.equal(released.controlMode, 'recovery');
  assert.equal(await simulator.recoverAfterInspection(), true);

  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'control.acquire', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
  }));
  let reacquired;
  do {
    reacquired = await control.next('state.snapshot');
  } while (reacquired.controlMode !== 'manual');
  const newAuthorization = await control.next('media.authorize');
  assert.equal(newAuthorization.controlLeaseId, reacquired.controlLeaseId);
  assert.notEqual(newAuthorization.mediaTicket, mediaTicket);
  mediaSocket = await openSocket(`${baseUrl}/v1/media`, certificate, token);
  media = inbox(mediaSocket, validateMediaControlMessage);
  mediaSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'media.hello', messageId: randomUUID(),
    deviceId: 'kufr-001', mediaTicket: newAuthorization.mediaTicket,
    bootId: simulator.bootId,
    controlLeaseId: reacquired.controlLeaseId,
    conversationId: reacquired.conversation.conversationId,
    generation: reacquired.conversation.generation,
  }));
  await media.next('media.open');
  const stoppedMediaClosed = awaitClose(mediaSocket);
  controlSocket.send(JSON.stringify({
    protocolVersion: 1, type: 'safety.stop', messageId: randomUUID(),
    deviceId: 'kufr-001', bootId: simulator.bootId,
  }));
  assert.equal(await stoppedMediaClosed, 1008);
  console.log('Media WSS: TLS, owner ticket, 16 KiB credit, hash, gap, timeout, cancel, release and Stop passed');
} finally {
  mediaSocket?.terminate();
  controlSocket?.terminate();
  await simulator?.close();
  const safeDirectory = resolve(temporaryDirectory);
  assert.ok(safeDirectory.startsWith(`${resolve(tmpdir())}${sep}`));
  await rm(safeDirectory, { recursive: true, force: true });
}
