import { timingSafeEqual, randomBytes, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { createServer } from 'node:https';
import { createInterface } from 'node:readline';
import { performance } from 'node:perf_hooks';

import WebSocket, { WebSocketServer } from 'ws';
import { validateControlMessage } from '../contracts/control-validator.mjs';
import { MediaStreamVerifier } from '../contracts/media-frame-v1.mjs';
import { validateMediaControlMessage } from '../contracts/media-validator.mjs';

const deviceId = 'kufr-001';

function isBearerToken(header, expectedToken) {
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) {
    return false;
  }

  const encoded = header.slice(7);
  if (!/^[0-9a-f]{64}$/.test(encoded)) {
    return false;
  }
  const supplied = Buffer.from(encoded);
  const expected = Buffer.from(expectedToken);
  return supplied.length === expected.length && timingSafeEqual(supplied, expected);
}

export class NativeBridge {
  constructor(executable, bootId, journalPath, conversationId = randomUUID()) {
    this.process = spawn(executable, [bootId, journalPath, conversationId], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.pending = [];
    this.failed = null;
    this.closed = false;
    this.process.on('error', (error) => this.fail(error));
    this.process.on('exit', (code) => this.fail(new Error(`native simulator exited: ${code}`)));
    this.process.on('close', () => { this.closed = true; });
    this.process.stdin.on('error', (error) => this.fail(error));

    const lines = createInterface({ input: this.process.stdout });
    lines.on('line', (line) => {
      const request = this.pending[0];
      if (!request) {
        return;
      }

      try {
        if (line.startsWith('E ')) {
          request.events.push(JSON.parse(line.slice(2)));
        } else if (line.startsWith('R ')) {
          this.pending.shift();
          request.resolve({ state: JSON.parse(line.slice(2)), events: request.events });
        } else {
          throw new Error('invalid native simulator output');
        }
      } catch (error) {
        this.fail(error);
      }
    });
  }

  fail(error) {
    if (this.failed) {
      return;
    }
    this.failed = error;
    for (const request of this.pending.splice(0)) {
      request.reject(error);
    }
  }

  send(operation, ...args) {
    if (this.failed) {
      return Promise.reject(this.failed);
    }
    if (![operation, ...args].every((value) => /^[A-Za-z0-9_-]+$/.test(String(value)))) {
      return Promise.reject(new Error('invalid native command field'));
    }

    return new Promise((resolve, reject) => {
      this.pending.push({ resolve, reject, events: [] });
      this.process.stdin.write(`${[operation, ...args].join(' ')}\n`, (error) => {
        if (error) {
          this.fail(error);
        }
      });
    });
  }

  async close() {
    if (this.closed) {
      return;
    }
    await new Promise((resolve) => {
      this.process.once('close', resolve);
      this.process.kill();
    });
  }
}

export async function createSimulatorServer({ certificate, privateKey, token, bridgeExecutable, journalPath, port = 0, onTimeline = () => {}, onNativeCommand = () => {} }) {
  if (!certificate || !privateKey || !token || !bridgeExecutable || !journalPath) {
    throw new Error('TLS certificate, private key, test token, native bridge and journal path are required');
  }
  if (typeof token !== 'string' || !/^[0-9a-f]{64}$/.test(token)) {
    throw new Error('Test token must be 64 lowercase hexadecimal characters');
  }

  const bootId = randomUUID();
  const startedAtMs = performance.now();
  const bridge = new NativeBridge(bridgeExecutable, bootId, journalPath);
  let current;
  try {
    current = (await bridge.send('SNAPSHOT', 0)).state;
  } catch (error) {
    await bridge.close();
    throw error;
  }
  let socket = null;
  let mediaSocket = null;
  let mediaTicket = null;
  let mediaTicketLeaseId = '';
  let nextMediaStreamId = 1;
  let disconnecting = false;
  let stopGeneration = 0;
  let eventSequence = 0;
  let lastResultSent = '';
  let ticking = false;
  const observers = new Set();
  const dashboardHtml = await readFile(new URL('./dashboard.html', import.meta.url));
  const dashboardScript = await readFile(new URL('./dashboard.js', import.meta.url));
  // Only these public assets are exposed; request paths never become file paths.
  const designAssetFiles = [
    ['arcane-workshop.css', 'text/css; charset=utf-8'],
    ['tokens/palette.css', 'text/css; charset=utf-8'],
    ['tokens/typography.css', 'text/css; charset=utf-8'],
    ['tokens/layout.css', 'text/css; charset=utf-8'],
    ['components/patterns.css', 'text/css; charset=utf-8'],
    ['fonts/cinzel.ttf', 'font/ttf'],
    ['fonts/inter.ttf', 'font/ttf'],
    ['fonts/ibmplexmono.ttf', 'font/ttf'],
  ];
  const designAssets = new Map(await Promise.all(designAssetFiles.map(async ([path, contentType]) => [
    `/design-system/${path}`,
    { contentType, content: await readFile(new URL(`../design-system/${path}`, import.meta.url)) },
  ])));

  const nowMs = () => Math.floor(performance.now() - startedAtMs);

  function snapshot() {
    const controlMode = current.leaseId ? 'manual' : current.needsRecovery ? 'recovery' : 'standalone';
    const lastResult = ['completed', 'outcome_unknown'].includes(current.lastOutcome)
      ? {
          protocolVersion: 1,
          type: 'selection.result',
          commandId: current.lastCommandId,
          bootId,
          eventSequence,
          status: current.lastOutcome,
          revision: current.revision,
          compartment: current.lastCompartment,
          physicalVerification: 'unavailable',
          requiresReload: true,
        }
      : null;

    return {
      protocolVersion: 1,
      type: 'state.snapshot',
      deviceId,
      bootId,
      deviceUptimeMs: nowMs(),
      eventSequence,
      revision: current.revision,
      controlMode,
      controlLeaseId: current.leaseId || null,
      phase: current.phase,
      activeCommandId: current.activeCommandId || null,
      readyMask: current.journalFault ? 0 : current.readyMask,
      calibrationRevision: 1,
      faults: current.journalFault ? ['journal_failed'] : [],
      microphoneState: 'unavailable',
      capabilities: ['manualControl', 'inventoryReload', 'conversationText'],
      lastResult,
      conversation: {
        conversationId: current.conversationId,
        generation: current.conversationGeneration,
        activeTurnId: current.activeConversationTurnId || null,
      },
    };
  }

  function send(message) {
    if (socket?.readyState === WebSocket.OPEN) {
      validateControlMessage(Buffer.from(JSON.stringify(message)));
      socket.send(JSON.stringify(message));
    }
  }

  function sendSnapshot() {
    eventSequence += 1;
    send(snapshot());
    broadcast({ kind: 'snapshot', snapshot: snapshot(), journalPending: current.journalPending });
  }

  function broadcast(message) {
    const line = `${JSON.stringify(message)}\n`;
    for (const observer of observers) {
      if (observer.writableLength > 65536) {
        observer.destroy();
        observers.delete(observer);
      } else {
        observer.write(line);
      }
    }
  }

  function sendTerminalResult() {
    if (!['completed', 'outcome_unknown'].includes(current.lastOutcome)) {
      return;
    }
    const resultKey = `${current.lastCommandId}:${current.lastOutcome}`;
    if (resultKey === lastResultSent) {
      return;
    }
    lastResultSent = resultKey;
    const result = snapshot().lastResult;
    eventSequence += 1;
    result.eventSequence = eventSequence;
    send(result);
  }

  let lastConversationResultSent = '';
  function sendConversationResult() {
    if (!current.completedConversationTurnId) {
      return;
    }
    const resultKey = `${current.completedConversationTurnId}:${current.completedConversationGeneration}`;
    if (resultKey === lastConversationResultSent ||
        current.completedConversationGeneration !== current.conversationGeneration) {
      return;
    }
    lastConversationResultSent = resultKey;
    eventSequence += 1;
    send({
      protocolVersion: 1,
      type: 'conversation.result',
      bootId,
      conversationId: current.conversationId,
      clientTurnId: current.completedConversationTurnId,
      generation: current.completedConversationGeneration,
      eventSequence,
      reply: 'SIMULACE: Text byl prijat.',
      simulated: true,
    });
  }

  async function native(operation, ...args) {
    onNativeCommand(operation);
    const result = await bridge.send(operation, ...args);
    current = result.state;
    if (mediaTicketLeaseId && current.leaseId !== mediaTicketLeaseId) {
      mediaTicket = null;
      mediaTicketLeaseId = '';
    }
    if (mediaSocket?.readyState === WebSocket.OPEN && mediaSocket.context &&
        (!current.leaseId || current.leaseId !== mediaSocket.context?.controlLeaseId ||
         current.conversationGeneration !== mediaSocket.context?.generation ||
         current.phase !== 'ready' || current.activeCommandId ||
         current.activeConversationTurnId)) {
      mediaSocket.close(1008, 'media context expired');
    }
    for (const event of result.events) {
      onTimeline(event);
      broadcast({ kind: 'timeline', event });
    }
    return result;
  }

  async function handleMessage(rawBytes, isBinary, connection) {
    if (connection.readyState !== WebSocket.OPEN) {
      return;
    }
    if (isBinary) {
      connection.close(1003, 'text required');
      return;
    }

    let message;
    try {
      message = validateControlMessage(rawBytes);
    } catch {
      connection.close(1008, 'invalid message');
      return;
    }

    if (message.type === 'safety.stop') {
      if (message.deviceId !== deviceId || message.bootId !== bootId) {
        connection.close(1008, 'wrong device or boot');
        return;
      }
      mediaSocket?.close(1008, 'stopped');
      await native('STOP', nowMs());
      sendTerminalResult();
      sendSnapshot();
      return;
    }

    if (!connection.helloReceived) {
      if (message.type !== 'hello' || message.deviceId !== deviceId) {
        connection.close(1008, 'hello required');
        return;
      }
      connection.helloReceived = true;
      clearTimeout(connection.helloTimeout);
      eventSequence += 1;
      send({ protocolVersion: 1, type: 'welcome', requestMessageId: message.messageId, snapshot: snapshot() });
      return;
    }

    if (message.deviceId !== deviceId || message.bootId !== bootId) {
      connection.close(1008, 'wrong device or boot');
      return;
    }

    const time = nowMs();
    switch (message.type) {
      case 'state.get':
        await native('SNAPSHOT', time);
        sendSnapshot();
        break;
      case 'control.acquire': {
        const leaseId = randomUUID();
        const result = await native('ACQUIRE', leaseId, time);
        if (!result.state.success) {
          connection.close(1008, 'controller busy');
          break;
        }
        mediaTicket = randomBytes(32).toString('hex');
        mediaTicketLeaseId = leaseId;
        sendSnapshot();
        send({
          protocolVersion: 1,
          type: 'media.authorize',
          requestMessageId: message.messageId,
          bootId,
          controlLeaseId: leaseId,
          mediaTicket,
        });
        break;
      }
      case 'control.release':
        if (message.controlLeaseId !== current.leaseId) {
          connection.close(1008, 'wrong lease');
          break;
        }
        await native('DISCONNECT', time);
        sendTerminalResult();
        sendSnapshot();
        break;
      case 'heartbeat': {
        if (message.controlLeaseId !== current.leaseId) {
          connection.close(1008, 'wrong lease');
          break;
        }
        const result = await native('RENEW', message.controlLeaseId, time);
        if (!result.state.success) {
          connection.close(1008, 'lease expired');
          break;
        }
        send({
          protocolVersion: 1,
          type: 'heartbeat.ack',
          requestMessageId: message.messageId,
          bootId,
          deviceUptimeMs: time,
          revision: current.revision,
          controlLeaseId: current.leaseId,
        });
        break;
      }
      case 'selection.start': {
        const selectionGeneration = stopGeneration;
        const command = message;
        const result = await native(
          'SELECT', command.commandId, command.deviceId, command.bootId,
          command.controlLeaseId, command.sequence, command.expectedRevision,
          command.issuedAtDeviceMs, command.maxAgeMs, command.payload.compartment,
          command.payload.selectionSource, time,
        );
        eventSequence += 1;
        send({
          protocolVersion: 1,
          type: 'command.ack',
          commandId: command.commandId,
          bootId,
          eventSequence,
          status: result.state.success ? 'accepted' : 'rejected',
          revision: current.revision,
          errorCode: result.state.success ? null : result.state.error,
        });
        if (result.state.success && !result.state.duplicate &&
            connection.readyState === WebSocket.OPEN &&
            selectionGeneration === stopGeneration) {
          await native('BEGIN', nowMs());
          sendSnapshot();
        } else if (result.state.duplicate) {
          sendTerminalResult();
        }
        break;
      }
      case 'inventory.confirmReload': {
        const command = message;
        const result = await native(
          'RELOAD', command.commandId, command.deviceId, command.bootId,
          command.controlLeaseId, command.sequence, command.expectedRevision,
          command.issuedAtDeviceMs, command.maxAgeMs,
          command.payload.compartment,
          command.payload.inspectionConfirmed ? 1 : 0, time,
        );
        eventSequence += 1;
        send({
          protocolVersion: 1,
          type: 'command.ack',
          commandId: command.commandId,
          bootId,
          eventSequence,
          status: result.state.success ? 'accepted' : 'rejected',
          revision: current.revision,
          errorCode: result.state.success ? null : result.state.error,
        });
        if (result.state.success && !result.state.duplicate) {
          sendSnapshot();
        }
        break;
      }
      case 'conversation.text': {
        const result = await native(
          'TEXT', message.bootId, message.controlLeaseId,
          message.conversationId, message.clientTurnId, message.generation,
          message.expectedRevision, Buffer.from(message.text, 'utf8').toString('hex'),
          time,
        );
        eventSequence += 1;
        send({
          protocolVersion: 1,
          type: 'conversation.ack',
          requestMessageId: message.messageId,
          bootId,
          conversationId: current.conversationId,
          clientTurnId: message.clientTurnId,
          generation: current.conversationGeneration,
          eventSequence,
          status: result.state.success ? 'accepted' : 'rejected',
          duplicate: result.state.duplicate,
          errorCode: result.state.success ? null : result.state.error,
        });
        if (result.state.success && !result.state.duplicate) {
          sendSnapshot();
        }
        break;
      }
      case 'conversation.cancel': {
        const result = await native(
          'CANCEL', message.messageId, message.bootId, message.controlLeaseId,
          message.conversationId, message.generation, time,
        );
        eventSequence += 1;
        send({
          protocolVersion: 1,
          type: 'conversation.ack',
          requestMessageId: message.messageId,
          bootId,
          conversationId: current.conversationId,
          clientTurnId: null,
          generation: current.conversationGeneration,
          eventSequence,
          status: result.state.success ? 'accepted' : 'rejected',
          duplicate: result.state.duplicate,
          errorCode: result.state.success ? null : result.state.error,
        });
        if (result.state.success) {
          sendSnapshot();
        }
        break;
      }
      case 'safety.reset':
        if (message.controlLeaseId !== current.leaseId) {
          connection.close(1008, 'wrong lease');
          break;
        }
        await native('RECOVER', time);
        sendSnapshot();
        break;
      default:
        connection.close(1008, 'unsupported direction');
    }
  }

  const server = createServer({ key: privateKey, cert: certificate }, (request, response) => {
    const headers = {
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      'Referrer-Policy': 'no-referrer',
    };
    if (request.method !== 'GET') {
      response.writeHead(405, headers);
      response.end();
      return;
    }

    if (request.url === '/simulator' || request.url === '/simulator/') {
      response.writeHead(200, {
        ...headers,
        'Content-Type': 'text/html; charset=utf-8',
        'Content-Security-Policy': "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'",
      });
      response.end(dashboardHtml);
    } else if (request.url === '/simulator/dashboard.js') {
      response.writeHead(200, { ...headers, 'Content-Type': 'text/javascript; charset=utf-8' });
      response.end(dashboardScript);
    } else if (designAssets.has(request.url)) {
      const asset = designAssets.get(request.url);
      response.writeHead(200, { ...headers, 'Content-Type': asset.contentType });
      response.end(asset.content);
    } else if (request.url === '/v1/observe') {
      if (!isBearerToken(request.headers.authorization, token)) {
        response.writeHead(401, headers);
        response.end();
        return;
      }
      if (observers.size >= 4) {
        response.writeHead(429, headers);
        response.end();
        return;
      }
      response.writeHead(200, {
        ...headers,
        'Content-Type': 'application/x-ndjson; charset=utf-8',
        'X-Accel-Buffering': 'no',
      });
      observers.add(response);
      response.write(`${JSON.stringify({ kind: 'snapshot', snapshot: snapshot(), journalPending: current.journalPending })}\n`);
      response.on('close', () => observers.delete(response));
    } else {
      response.writeHead(404, headers);
      response.end();
    }
  });
  const webSockets = new WebSocketServer({ noServer: true, maxPayload: 8192, perMessageDeflate: false });
  const mediaWebSockets = new WebSocketServer({ noServer: true, maxPayload: 4112, perMessageDeflate: false });

  function acceptMediaSocket(connection) {
    mediaSocket = connection;
    let verifier = null;
    let creditRemaining = 0;
    let progressTimer = null;
    const helloTimer = setTimeout(() => connection.close(1008, 'media hello timeout'), 10000);

    function sendMedia(message) {
      validateMediaControlMessage(Buffer.from(JSON.stringify(message)));
      connection.send(JSON.stringify(message));
    }

    function isCurrentContext(context) {
      return socket?.readyState === WebSocket.OPEN &&
        current.leaseId === context.controlLeaseId &&
        nowMs() < current.leaseExpiresAtMs &&
        current.phase === 'ready' && !current.activeCommandId &&
        !current.activeConversationTurnId &&
        current.conversationId === context.conversationId &&
        current.conversationGeneration === context.generation;
    }

    function armProgressTimeout() {
      clearTimeout(progressTimer);
      progressTimer = setTimeout(() => connection.close(1008, 'media timeout'), 1000);
    }

    connection.on('message', (bytes, isBinary) => {
      try {
        if (isBinary) {
          if (!verifier || !isCurrentContext(connection.context)) {
            throw new Error('media context expired');
          }
          const payload = verifier.accept(bytes, nowMs());
          if (payload.length > creditRemaining) {
            throw new Error('media credit exceeded');
          }
          creditRemaining -= payload.length;
          armProgressTimeout();
          if (creditRemaining <= 8192) {
            const grantedBytes = 16384 - creditRemaining;
            creditRemaining = 16384;
            sendMedia({
              protocolVersion: 1,
              type: 'media.credit',
              streamId: verifier.streamId,
              creditBytes: grantedBytes,
            });
          }
          return;
        }

        const message = validateMediaControlMessage(bytes);
        if (!verifier) {
          const suppliedTicket = Buffer.from(message.mediaTicket ?? '');
          const expectedTicket = Buffer.from(mediaTicket ?? '');
          const ticketValid = expectedTicket.length === 64 &&
            suppliedTicket.length === expectedTicket.length &&
            timingSafeEqual(suppliedTicket, expectedTicket);
          if (message.type !== 'media.hello' || message.deviceId !== deviceId ||
              message.bootId !== bootId || !isCurrentContext(message) ||
              !ticketValid || nextMediaStreamId > 0xffffffff) {
            throw new Error('media hello rejected');
          }
          clearTimeout(helloTimer);
          connection.context = message;
          const streamId = nextMediaStreamId;
          nextMediaStreamId += 1;
          verifier = new MediaStreamVerifier({
            kind: 1,
            streamId,
            maxBytes: 1920000,
            startedAtMs: nowMs(),
          });
          creditRemaining = 16384;
          sendMedia({
            protocolVersion: 1,
            type: 'media.open',
            requestMessageId: message.messageId,
            bootId,
            conversationId: message.conversationId,
            generation: message.generation,
            streamId,
            direction: 'phoneToDevice',
            kind: 'pcmCapture',
            format: 'pcm16le',
            sampleRateHz: 32000,
            channels: 1,
            maxBytes: 1920000,
            creditBytes: 16384,
          });
          armProgressTimeout();
          return;
        }

        if (!isCurrentContext(connection.context) ||
            message.bootId !== bootId ||
            message.controlLeaseId !== connection.context.controlLeaseId ||
            message.conversationId !== connection.context.conversationId ||
            message.generation !== connection.context.generation ||
            message.streamId !== verifier.streamId) {
          throw new Error('media context mismatch');
        }
        if (message.type === 'media.end') {
          if (message.totalBytes < 19200 || message.totalBytes % 2 !== 0) {
            throw new Error('capture duration or sample alignment invalid');
          }
          const result = verifier.finish(message, nowMs());
          sendMedia({
            protocolVersion: 1,
            type: 'media.result',
            requestMessageId: message.messageId,
            streamId: verifier.streamId,
            status: 'received',
            totalBytes: result.totalBytes,
            errorCode: null,
            simulated: true,
          });
        } else if (message.type === 'media.cancel') {
          verifier.closed = true;
          sendMedia({
            protocolVersion: 1,
            type: 'media.result',
            requestMessageId: message.messageId,
            streamId: verifier.streamId,
            status: 'cancelled',
            totalBytes: verifier.receivedBytes,
            errorCode: null,
            simulated: true,
          });
        } else {
          throw new Error('unsupported media direction');
        }
        clearTimeout(progressTimer);
        connection.close(1000);
      } catch {
        connection.close(1008, 'invalid media');
      }
    });
    connection.on('close', () => {
      clearTimeout(helloTimer);
      clearTimeout(progressTimer);
      if (mediaSocket === connection) {
        mediaSocket = null;
      }
    });
  }

  server.on('upgrade', (request, networkSocket, head) => {
    if (request.url !== '/v1/control' && request.url !== '/v1/media') {
      networkSocket.end('HTTP/1.1 404 Not Found\r\n\r\n');
      return;
    }
    if (!isBearerToken(request.headers.authorization, token)) {
      networkSocket.end('HTTP/1.1 401 Unauthorized\r\n\r\n');
      return;
    }
    if (request.url === '/v1/media') {
      if (mediaSocket) {
        networkSocket.end('HTTP/1.1 409 Conflict\r\n\r\n');
        return;
      }
      mediaWebSockets.handleUpgrade(request, networkSocket, head, acceptMediaSocket);
      return;
    }
    if (socket || disconnecting) {
      networkSocket.end('HTTP/1.1 409 Conflict\r\n\r\n');
      return;
    }
    webSockets.handleUpgrade(request, networkSocket, head, (connection) => {
      socket = connection;
      connection.helloReceived = false;
      connection.helloTimeout = setTimeout(() => connection.close(1008, 'hello timeout'), 10000);
      let processing = Promise.resolve();
      let queuedMessages = 0;
      let overloaded = false;
      let priorityStopPending = false;
      connection.on('message', (bytes, isBinary) => {
        if (overloaded) {
          return;
        }
        if (!isBinary) {
          try {
            const message = validateControlMessage(bytes);
            if (message.type === 'safety.stop') {
              if (priorityStopPending) {
                return;
              }
              priorityStopPending = true;
              stopGeneration += 1;
              handleMessage(bytes, false, connection)
                .catch(() => connection.close(1011, 'simulator failure'))
                .finally(() => { priorityStopPending = false; });
              return;
            }
          } catch {
            // The regular handler closes malformed messages.
          }
        }
        queuedMessages += 1;
        if (queuedMessages > 32) {
          overloaded = true;
          stopGeneration += 1;
          native('STOP', nowMs()).finally(() => connection.close(1008, 'control queue full'));
          return;
        }
        const receivedBeforeStop = stopGeneration;
        processing = processing
          .then(() => {
            if (receivedBeforeStop === stopGeneration) {
              return handleMessage(bytes, isBinary, connection);
            }
            return undefined;
          })
          .catch(() => connection.close(1011, 'simulator failure'))
          .finally(() => { queuedMessages -= 1; });
      });
      connection.on('close', () => {
        clearTimeout(connection.helloTimeout);
        if (socket === connection) {
          socket = null;
          disconnecting = true;
          processing
            .then(async () => {
              if (current.leaseId) {
                await native('DISCONNECT', nowMs());
              }
            })
            .catch(() => {})
            .finally(() => { disconnecting = false; });
        }
      });
    });
  });

  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(port, '127.0.0.1', resolve);
    });
  } catch (error) {
    webSockets.close();
    mediaWebSockets.close();
    await bridge.close();
    throw error;
  }

  const tickTimer = setInterval(async () => {
    if (ticking) {
      return;
    }
    ticking = true;
    try {
      const result = await native('TICK', nowMs());
      if (result.events.length > 0 || result.state.success) {
        sendTerminalResult();
        sendConversationResult();
        sendSnapshot();
      }
    } catch {
      socket?.close(1011, 'simulator failure');
    } finally {
      ticking = false;
    }
  }, 50);

  return {
    port: server.address().port,
    bootId,
    recoverAfterInspection: async () => {
      const result = await native('RECOVER', nowMs());
      if (result.state.success) {
        sendSnapshot();
      }
      return result.state.success;
    },
    close: async () => {
      clearInterval(tickTimer);
      for (const observer of observers) {
        observer.end();
      }
      observers.clear();
      socket?.terminate();
      mediaSocket?.terminate();
      webSockets.close();
      mediaWebSockets.close();
      await new Promise((resolve) => server.close(resolve));
      await bridge.close();
    },
  };
}
