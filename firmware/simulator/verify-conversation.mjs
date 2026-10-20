import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { NativeBridge } from './server.mjs';

const executable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!executable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const directory = await mkdtemp(join(tmpdir(), 'kufr-conversation-test-'));
const bootId = randomUUID();
const leaseId = randomUUID();
const bridge = new NativeBridge(executable, bootId, join(directory, 'journal.log'));

function textCommand(state, turnId, text, nowMs) {
  return bridge.send(
    'TEXT', bootId, leaseId, state.conversationId, turnId,
    state.conversationGeneration, state.revision,
    Buffer.from(text, 'utf8').toString('hex'), nowMs,
  );
}

try {
  let state = (await bridge.send('SNAPSHOT', 0)).state;
  assert.ok(state.conversationId);
  state = (await bridge.send('ACQUIRE', leaseId, 0)).state;
  assert.equal(state.success, true);

  const firstTurnId = randomUUID();
  const accepted = await textCommand(state, firstTurnId, 'Český text', 10);
  assert.equal(accepted.state.success, true);
  assert.equal(accepted.state.activeConversationTurnId, firstTurnId);
  assert.deepEqual(accepted.events, []);
  const duplicate = await textCommand(state, firstTurnId, 'Český text', 20);
  assert.equal(duplicate.state.duplicate, true);
  assert.equal((await textCommand(state, firstTurnId, 'jiný', 30)).state.error, 'id_conflict');
  assert.equal((await textCommand(state, randomUUID(), 'další', 40)).state.error, 'busy');

  const cancelMessageId = randomUUID();
  const cancelled = await bridge.send(
    'CANCEL', cancelMessageId, bootId, leaseId, state.conversationId,
    state.conversationGeneration, 50,
  );
  assert.equal(cancelled.state.success, true);
  assert.equal(cancelled.state.conversationGeneration, 1);
  assert.equal(cancelled.state.activeConversationTurnId, '');
  const repeatedCancel = await bridge.send(
    'CANCEL', cancelMessageId, bootId, leaseId, state.conversationId,
    state.conversationGeneration, 60,
  );
  assert.equal(repeatedCancel.state.success, true);
  assert.equal(repeatedCancel.state.duplicate, true);
  assert.equal(repeatedCancel.state.conversationGeneration, 1);
  assert.equal((await bridge.send(
    'CANCEL', cancelMessageId, bootId, leaseId, state.conversationId,
    1, 70,
  )).state.error, 'id_conflict');
  assert.equal((await bridge.send('TICK', 500)).state.completedConversationTurnId, '');
  assert.equal((await textCommand(state, firstTurnId, 'Český text', 510)).state.error, 'wrong_generation');

  state = (await bridge.send('SNAPSHOT', 510)).state;
  assert.equal((await textCommand(state, firstTurnId, 'Český text', 520)).state.error,
    'already_seen_result_unavailable');
  const secondTurnId = randomUUID();
  assert.equal((await textCommand(state, secondTurnId, 'nyní', 530)).state.success, true);
  // The control loop normally runs every 10 ms; keep its watchdog alive while
  // the fake conversation waits for a reply.
  assert.deepEqual((await bridge.send('TICK', 700)).events, []);
  const completed = await bridge.send('TICK', 831);
  assert.equal(completed.state.completedConversationTurnId, secondTurnId);
  assert.equal(completed.state.completedConversationGeneration, 1);
  assert.deepEqual(completed.events, []);
  assert.equal((await textCommand(state, secondTurnId, 'nyní', 840)).state.error,
    'already_seen_result_unavailable');

  const thirdTurnId = randomUUID();
  assert.equal((await textCommand(state, thirdTurnId, 'čeká', 850)).state.success, true);
  const disconnected = await bridge.send('DISCONNECT', 860);
  assert.equal(disconnected.state.conversationGeneration, 2);
  assert.equal(disconnected.state.activeConversationTurnId, '');
  assert.equal(disconnected.state.completedConversationTurnId, '');
  assert.equal((await bridge.send('TICK', 1200)).state.completedConversationTurnId, '');
  assert.equal((await textCommand(state, randomUUID(), 'bez lease', 1210)).state.error,
    'unauthorized');

  console.log('Conversation bridge: duplicate, cancel, late result, lease loss and no actuator events passed');
} finally {
  await bridge.close();
  await rm(directory, { recursive: true, force: true });
}
