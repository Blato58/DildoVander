import { timingSafeEqual, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:https';
import { createInterface } from 'node:readline';
import { performance } from 'node:perf_hooks';

import WebSocket, { WebSocketServer } from 'ws';
import { validateControlMessage } from '../contracts/control-validator.mjs';

const deviceId = 'kufr-001';

function isBearerToken(header, expectedToken) {
  if (typeof header !== 'string' || !header.startsWith('Bearer ')) {
    return false;
  }

  const supplied = Buffer.from(header.slice(7));
  const expected = Buffer.from(expectedToken);
  return supplied.length === expected.length && timingSafeEqual(supplied, expected);
}

export class NativeBridge {
  constructor(executable, bootId) {
    this.process = spawn(executable, [bootId], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.pending = [];
    this.failed = null;
    this.process.on('error', (error) => this.fail(error));
    this.process.on('exit', (code) => this.fail(new Error(`native simulator exited: ${code}`)));
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

  close() {
    this.process.kill();
  }
}

export async function createSimulatorServer({ certificate, privateKey, token, bridgeExecutable, port = 0, onTimeline = () => {} }) {
  if (!certificate || !privateKey || !token || !bridgeExecutable) {
    throw new Error('TLS certificate, private key, test token and native bridge are required');
  }

  const bootId = randomUUID();
  const startedAtMs = performance.now();
  const bridge = new NativeBridge(bridgeExecutable, bootId);
  let current = (await bridge.send('SNAPSHOT', 0)).state;
  let socket = null;
  let disconnecting = false;
  let stopGeneration = 0;
  let eventSequence = 0;
  let lastResultSent = '';
  let ticking = false;

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
      readyMask: current.readyMask,
      calibrationRevision: 1,
      faults: [],
      microphoneState: 'unavailable',
      capabilities: ['manualControl'],
      lastResult,
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

  async function native(operation, ...args) {
    const result = await bridge.send(operation, ...args);
    current = result.state;
    for (const event of result.events) {
      onTimeline(event);
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
        sendSnapshot();
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

  const server = createServer({ key: privateKey, cert: certificate }, (_request, response) => {
    response.writeHead(404);
    response.end();
  });
  const webSockets = new WebSocketServer({ noServer: true, maxPayload: 2048, perMessageDeflate: false });

  server.on('upgrade', (request, networkSocket, head) => {
    if (request.url !== '/v1/control') {
      networkSocket.end('HTTP/1.1 404 Not Found\r\n\r\n');
      return;
    }
    if (!isBearerToken(request.headers.authorization, token)) {
      networkSocket.end('HTTP/1.1 401 Unauthorized\r\n\r\n');
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

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', resolve);
  });

  const tickTimer = setInterval(async () => {
    if (ticking) {
      return;
    }
    ticking = true;
    try {
      const result = await native('TICK', nowMs());
      if (result.events.length > 0 || result.state.success) {
        sendTerminalResult();
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
    close: async () => {
      clearInterval(tickTimer);
      socket?.terminate();
      webSockets.close();
      await new Promise((resolve) => server.close(resolve));
      bridge.close();
    },
  };
}
