import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { request as httpsRequest } from 'node:https';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import WebSocket from 'ws';
import { validateControlMessage } from '../../contracts/control-validator.mjs';
import { createSimulatorServer } from './server.mjs';
import { createMobileDevServer } from './mobile-dev-server.mjs';

const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!bridgeExecutable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const temporaryDirectory = await mkdtemp(join(tmpdir(), 'kufr-wss-test-'));
const certificatePath = join(temporaryDirectory, 'certificate.pem');
const privateKeyPath = join(temporaryDirectory, 'private-key.pem');
let simulator;
let client;
let observer;
let mobileDevServer;

function requestPage(port, certificate, path, authorization) {
  return new Promise((resolve, reject) => {
    const request = httpsRequest({
      hostname: '127.0.0.1',
      port,
      path,
      ca: certificate,
      headers: authorization ? { Authorization: authorization } : {},
    }, (response) => {
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => resolve({ status: response.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
    });
    request.on('error', reject);
    request.end();
  });
}

function observe(port, certificate, token) {
  return new Promise((resolve, reject) => {
    const request = httpsRequest({
      hostname: '127.0.0.1',
      port,
      path: '/v1/observe',
      ca: certificate,
      headers: { Authorization: `Bearer ${token}` },
    }, (response) => {
      const messages = [];
      let pending = '';
      response.on('data', (chunk) => {
        pending += chunk.toString('utf8');
        let newline;
        while ((newline = pending.indexOf('\n')) !== -1) {
          messages.push(JSON.parse(pending.slice(0, newline)));
          pending = pending.slice(newline + 1);
        }
      });
      resolve({ response, messages, status: response.statusCode });
    });
    request.on('error', reject);
    request.end();
  });
}

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
    journalPath: join(temporaryDirectory, 'journal.log'),
    onTimeline: (event) => timeline.push(event),
  });
  const url = `wss://127.0.0.1:${simulator.port}/v1/control`;

  const page = await requestPage(simulator.port, certificate, '/simulator');
  assert.equal(page.status, 200);
  assert.match(page.body, /SIMULACE/);
  assert.match(page.body, /<script src="\.\.\/design-system\/theme\.js"><\/script>/);
  const theme = await requestPage(simulator.port, certificate, '/design-system/theme.js');
  assert.equal(theme.status, 200);
  assert.match(theme.body, /globalThis\.DildoVanderTheme =/);
  assert.match(theme.body, /lord-voldemort/);
  const script = await requestPage(simulator.port, certificate, '/simulator/dashboard.js');
  assert.equal(script.status, 200);
  assert.match(script.body, /v1\/observe/);
  const dashboardUi = await requestPage(simulator.port, certificate, '/simulator/dashboard-ui.js');
  assert.equal(dashboardUi.status, 200);
  assert.match(dashboardUi.body, /design-system\/ui\.js/);
  const sharedStyles = await requestPage(simulator.port, certificate, '/design-system/arcane-workshop.css');
  assert.equal(sharedStyles.status, 200);
  assert.match(sharedStyles.body, /tokens\/palette\.css/);
  for (const assetPath of ['ui.js', 'tokens/palette.css', 'tokens/typography.css', 'tokens/layout.css', 'components/patterns.css', 'components/framework.css', 'fonts/cinzel.ttf', 'fonts/inter.ttf', 'fonts/ibmplexmono.ttf']) {
    const asset = await requestPage(simulator.port, certificate, `/design-system/${assetPath}`);
    assert.equal(asset.status, 200, `public design asset ${assetPath}`);
    assert.ok(asset.body.length > 0);
  }
  for (const forbiddenPath of ['/design-system/../server.mjs', '/design-system/%2e%2e/server.mjs', '/design-system/fonts/unknown.ttf']) {
    assert.equal((await requestPage(simulator.port, certificate, forbiddenPath)).status, 404);
  }
  assert.equal((await requestPage(simulator.port, certificate, '/v1/observe')).status, 401);
  observer = await observe(simulator.port, certificate, token);
  assert.equal(observer.status, 200);
  await new Promise((resolve) => setTimeout(resolve, 50));
  assert.equal(observer.messages[0].kind, 'snapshot');
  assert.equal(observer.messages[0].snapshot.controlMode, 'standalone');

  await new Promise((resolve, reject) => {
    const unauthorized = new WebSocket(url, { ca: certificate, rejectUnauthorized: true });
    unauthorized.on('unexpected-response', (_request, response) => {
      assert.equal(response.statusCode, 401);
      response.resume();
      resolve();
    });
    unauthorized.on('error', reject);
  });

  await new Promise((resolve, reject) => {
    const malformed = new WebSocket(url, {
      ca: certificate,
      rejectUnauthorized: true,
      headers: { Authorization: `Bearer ${'G'.repeat(64)}` },
    });
    malformed.on('unexpected-response', (_request, response) => {
      assert.equal(response.statusCode, 401);
      response.resume();
      resolve();
    });
    malformed.on('error', reject);
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
  assert.ok(acquired.conversation.conversationId);

  const textTurnId = randomUUID();
  const textMessageId = randomUUID();
  client.send({
    protocolVersion: 1,
    type: 'conversation.text',
    messageId: textMessageId,
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    conversationId: acquired.conversation.conversationId,
    clientTurnId: textTurnId,
    generation: acquired.conversation.generation,
    expectedRevision: acquired.revision,
    text: 'Česká otázka',
  });
  const textAck = await client.next('conversation.ack');
  assert.equal(textAck.requestMessageId, textMessageId);
  assert.equal(textAck.status, 'accepted');
  assert.equal(textAck.duplicate, false);
  const cancelMessage = {
    protocolVersion: 1,
    type: 'conversation.cancel',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    conversationId: acquired.conversation.conversationId,
    generation: acquired.conversation.generation,
  };
  client.send(cancelMessage);
  const cancelAck = await client.next('conversation.ack');
  assert.equal(cancelAck.status, 'accepted');
  assert.equal(cancelAck.generation, acquired.conversation.generation + 1);
  client.send(cancelMessage);
  const repeatedCancelAck = await client.next('conversation.ack');
  assert.equal(repeatedCancelAck.status, 'accepted');
  assert.equal(repeatedCancelAck.duplicate, true);
  assert.equal(repeatedCancelAck.generation, cancelAck.generation);
  await new Promise((resolve) => setTimeout(resolve, 450));
  assert.equal(client.latest('conversation.result'), undefined);

  client.send({
    protocolVersion: 1,
    type: 'conversation.text',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    conversationId: acquired.conversation.conversationId,
    clientTurnId: textTurnId,
    generation: acquired.conversation.generation,
    expectedRevision: acquired.revision,
    text: 'po zrušení',
  });
  const staleTextAck = await client.next('conversation.ack');
  assert.equal(staleTextAck.status, 'rejected');
  assert.equal(staleTextAck.errorCode, 'wrong_generation');

  const nextTurnId = randomUUID();
  client.send({
    protocolVersion: 1,
    type: 'conversation.text',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    conversationId: acquired.conversation.conversationId,
    clientTurnId: nextTurnId,
    generation: cancelAck.generation,
    expectedRevision: acquired.revision,
    text: 'Nová česká otázka',
  });
  assert.equal((await client.next('conversation.ack')).status, 'accepted');
  const fakeReply = await client.next('conversation.result');
  assert.equal(fakeReply.clientTurnId, nextTurnId);
  assert.equal(fakeReply.generation, cancelAck.generation);
  assert.equal(fakeReply.simulated, true);
  assert.equal(timeline.filter((event) => event.event === 'release_latch').length, 0);

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
  assert.equal(observer.messages.filter((message) => message.kind === 'timeline' && message.event.event === 'release_latch').length, 1);

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

  const reloadCommand = {
    protocolVersion: 1,
    type: 'inventory.confirmReload',
    commandId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: acquired.controlLeaseId,
    sequence: 2,
    expectedRevision: state.revision,
    issuedAtDeviceMs: state.deviceUptimeMs,
    maxAgeMs: 2000,
    payload: { compartment: 4, inspectionConfirmed: true },
  };
  client.send(reloadCommand);
  const reloadAck = await client.next('command.ack');
  assert.equal(reloadAck.status, 'accepted');
  let reloaded;
  do {
    reloaded = await client.next('state.snapshot');
  } while (reloaded.readyMask !== 63);
  assert.equal(timeline.filter((event) => event.event === 'release_latch').length, 1);
  client.send(reloadCommand);
  const repeatedReloadAck = await client.next('command.ack');
  assert.equal(repeatedReloadAck.status, 'accepted');
  assert.equal(repeatedReloadAck.commandId, reloadCommand.commandId);

  const secondCommand = {
    ...command,
    commandId: randomUUID(),
    sequence: 3,
    expectedRevision: reloaded.revision,
    issuedAtDeviceMs: reloaded.deviceUptimeMs,
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
  client = null;
  observer.response.destroy();
  observer = null;
  await simulator.close();
  simulator = await createSimulatorServer({
    certificate,
    privateKey,
    token,
    bridgeExecutable,
    journalPath: join(temporaryDirectory, 'journal.log'),
  });
  client = createClient(`wss://127.0.0.1:${simulator.port}/v1/control`, certificate, token);
  await new Promise((resolve, reject) => {
    client.socket.once('open', resolve);
    client.socket.once('error', reject);
  });
  client.send({ protocolVersion: 1, type: 'hello', messageId: randomUUID(), deviceId: 'kufr-001' });
  const afterRestart = await client.next('welcome');
  assert.equal(afterRestart.snapshot.controlMode, 'recovery');
  assert.equal(afterRestart.snapshot.phase, 'recoveryRequired');
  assert.equal(afterRestart.snapshot.readyMask, 63);
  assert.equal(afterRestart.snapshot.controlLeaseId, null);
  client.send({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  await new Promise((resolve) => client.socket.once('close', resolve));
  client = null;
  assert.equal(await simulator.recoverAfterInspection(), true);
  client = createClient(`wss://127.0.0.1:${simulator.port}/v1/control`, certificate, token);
  await new Promise((resolve, reject) => {
    client.socket.once('open', resolve);
    client.socket.once('error', reject);
  });
  client.send({ protocolVersion: 1, type: 'hello', messageId: randomUUID(), deviceId: 'kufr-001' });
  const afterInspection = await client.next('welcome');
  assert.equal(afterInspection.snapshot.controlMode, 'standalone');
  assert.equal(afterInspection.snapshot.readyMask, 47);

  client.socket.terminate();
  client = null;
  await simulator.close();
  let closeOnAcquire = true;
  let racingSocket;
  simulator = await createSimulatorServer({
    certificate,
    privateKey,
    token,
    bridgeExecutable,
    journalPath: join(temporaryDirectory, 'journal.log'),
    onNativeCommand: (operation) => {
      if (operation === 'ACQUIRE' && closeOnAcquire) {
        closeOnAcquire = false;
        racingSocket?.terminate();
      }
    },
  });
  const raceUrl = `wss://127.0.0.1:${simulator.port}/v1/control`;
  client = createClient(raceUrl, certificate, token);
  racingSocket = client.socket;
  await new Promise((resolve, reject) => {
    client.socket.once('open', resolve);
    client.socket.once('error', reject);
  });
  client.send({ protocolVersion: 1, type: 'hello', messageId: randomUUID(), deviceId: 'kufr-001' });
  await client.next('welcome');
  client.send({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  await new Promise((resolve) => client.socket.once('close', resolve));
  client = null;

  for (let attempt = 0; attempt < 20; attempt += 1) {
    const candidate = createClient(raceUrl, certificate, token);
    const opened = await new Promise((resolve) => {
      candidate.socket.once('open', () => resolve(true));
      candidate.socket.once('unexpected-response', (_request, response) => {
        response.resume();
        resolve(false);
      });
      candidate.socket.once('error', () => resolve(false));
    });
    if (opened) {
      client = candidate;
      break;
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  assert.ok(client, 'socket did not become available after disconnect');
  client.send({ protocolVersion: 1, type: 'hello', messageId: randomUUID(), deviceId: 'kufr-001' });
  const afterRace = await client.next('welcome');
  assert.equal(afterRace.snapshot.controlLeaseId, null);
  assert.equal(await simulator.recoverAfterInspection(), true);
  await client.next('state.snapshot');
  client.send({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
  });
  const renewedLease = await client.next('state.snapshot');
  assert.equal(renewedLease.controlMode, 'manual');
  await new Promise((resolve) => setTimeout(resolve, 6200));
  assert.equal(client.socket.readyState, WebSocket.OPEN);
  client.send({
    protocolVersion: 1,
    type: 'heartbeat',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: simulator.bootId,
    controlLeaseId: renewedLease.controlLeaseId,
  });
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('late heartbeat was not rejected')), 3000);
    client.socket.once('close', () => {
      clearTimeout(timeout);
      resolve();
    });
  });
  client = null;
  await simulator.close();
  simulator = await createSimulatorServer({
    certificate,
    privateKey,
    token,
    bridgeExecutable,
    journalPath: join(temporaryDirectory, 'mobile-proxy-journal.log'),
  });
  await writeFile(join(temporaryDirectory, 'index.html'), '<h1>SIMULACE</h1>');
  mobileDevServer = await createMobileDevServer({
    certificate,
    token,
    distDirectory: temporaryDirectory,
    simulatorPort: simulator.port,
    port: 0,
  });
  const mobileOrigin = `http://127.0.0.1:${mobileDevServer.port}`;
  const mobileMode = await fetch(`${mobileOrigin}/dev-mode`);
  assert.deepEqual(await mobileMode.json(), { simulator: true });
  assert.match(await (await fetch(mobileOrigin)).text(), /SIMULACE/);
  const deniedBrowser = new WebSocket(`ws://127.0.0.1:${mobileDevServer.port}/dev/control`, {
    origin: 'http://example.invalid',
  });
  assert.equal(await new Promise((resolve) => {
    deniedBrowser.once('unexpected-response', (_request, response) => {
      response.resume();
      resolve(response.statusCode);
    });
    deniedBrowser.once('error', () => resolve(0));
  }), 403);
  const mobileClient = new WebSocket(`ws://127.0.0.1:${mobileDevServer.port}/dev/control`, {
    origin: mobileOrigin,
  });
  await new Promise((resolve, reject) => {
    mobileClient.once('open', resolve);
    mobileClient.once('error', reject);
  });
  const competingBrowser = new WebSocket(`ws://127.0.0.1:${mobileDevServer.port}/dev/control`, {
    origin: mobileOrigin,
  });
  assert.equal(await new Promise((resolve) => {
    competingBrowser.once('unexpected-response', (_request, response) => {
      response.resume();
      resolve(response.statusCode);
    });
    competingBrowser.once('error', () => resolve(0));
  }), 403);
  mobileClient.send(JSON.stringify({
    protocolVersion: 1,
    type: 'hello',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
  }));
  const proxiedWelcome = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('mobile proxy welcome timed out')), 3000);
    mobileClient.once('message', (bytes) => {
      clearTimeout(timeout);
      resolve(validateControlMessage(bytes));
    });
    mobileClient.once('close', (code, reason) => {
      clearTimeout(timeout);
      reject(new Error(`mobile proxy closed before welcome: ${code} ${reason}`));
    });
    mobileClient.once('error', reject);
  });
  assert.equal(proxiedWelcome.type, 'welcome');
  const acquiredSnapshot = new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('mobile proxy acquire timed out')), 3000);
    const onMessage = (bytes) => {
      const message = validateControlMessage(bytes);
      if (message.type === 'state.snapshot' && message.controlMode === 'manual') {
        clearTimeout(timeout);
        mobileClient.off('message', onMessage);
        resolve(message);
      }
    };
    mobileClient.on('message', onMessage);
    mobileClient.once('error', reject);
  });
  mobileClient.send(JSON.stringify({
    protocolVersion: 1,
    type: 'control.acquire',
    messageId: randomUUID(),
    deviceId: 'kufr-001',
    bootId: proxiedWelcome.snapshot.bootId,
  }));
  assert.equal((await acquiredSnapshot).controlMode, 'manual');
  mobileClient.send('x'.repeat(9000));
  assert.equal(await new Promise((resolve) => mobileClient.once('close', resolve)), 1009);
  assert.deepEqual(await (await fetch(`${mobileOrigin}/dev-mode`)).json(), { simulator: true });
  await mobileDevServer.close();
  mobileDevServer = null;
  console.log('WSS: TLS/auth, lease expiry, conversation text/result/cancel, ACK retry, reload, priority Stop, strict input, restart, proxy and close/acquire race passed');
} finally {
  observer?.response.destroy();
  client?.socket.terminate();
  await mobileDevServer?.close();
  await simulator?.close();
  await rm(temporaryDirectory, { recursive: true, force: true });
}
