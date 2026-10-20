import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { test } from 'node:test';
import { loadDeviceModule } from './load-test-module.mjs';
import { validateControlMessage } from '../../contracts/control-validator.mjs';

const { LocalConversationClient } = await loadDeviceModule('LocalConversationClient');
Object.defineProperty(globalThis, 'crypto', { value: { randomUUID }, configurable: true });
let nextTimer = 0;
const timers = new Map();
globalThis.window = {
  setTimeout(callback, milliseconds) { timers.set(++nextTimer, { callback, milliseconds }); return nextTimer; },
  clearTimeout(id) { timers.delete(id); },
};

function session() {
  timers.clear();
  const state = {
    deviceId: 'kufr-001', bootId: randomUUID(), revision: 3, eventSequence: 5,
    controlLeaseId: randomUUID(), capabilities: ['conversationText'],
    conversation: { conversationId: randomUUID(), generation: 2, activeTurnId: null },
  };
  const sent = [];
  let disconnects = 0;
  const client = new LocalConversationClient((message) => {
    const full = { protocolVersion: 1, ...message };
    validateControlMessage(new TextEncoder().encode(JSON.stringify(full)));
    sent.push(full);
    return true;
  }, () => {}, () => disconnects++);
  client.update(state);
  const ack = (overrides = {}) => ({
    type: 'conversation.ack', bootId: state.bootId,
    conversationId: state.conversation.conversationId, generation: 2,
    clientTurnId: sent[0]?.clientTurnId, requestMessageId: sent[0]?.messageId,
    eventSequence: 6, status: 'accepted', duplicate: false, errorCode: null, ...overrides,
  });
  const result = (overrides = {}) => ({
    type: 'conversation.result', bootId: state.bootId,
    conversationId: state.conversation.conversationId, generation: 2,
    clientTurnId: sent[0]?.clientTurnId, eventSequence: 8,
    reply: '<img src=x onerror=alert(1)>', simulated: false, ...overrides,
  });
  return { client, state, sent, ack, result, disconnects: () => disconnects };
}

test('text belongs to the current device session and requires manual capability and fresh UI', () => {
  const { client, state, sent } = session();
  client.update({ ...state, controlLeaseId: null });
  assert.equal(client.submit('hello'), false);
  client.update({ ...state, capabilities: [] });
  assert.equal(client.submit('hello'), false);
  client.update(state);
  assert.equal(client.snapshot(false).canSubmit, false);
  assert.equal(client.submit('Česká odpověď 🪄'), true);
  assert.equal(client.submit('second'), false);
  assert.equal(sent[0].bootId, state.bootId);
  assert.equal(sent[0].conversationId, state.conversation.conversationId);
  assert.equal(sent[0].controlLeaseId, state.controlLeaseId);
  assert.equal(sent[0].expectedRevision, 3);
  assert.equal(sent[0].generation, 2);
});

test('Unicode scalar bounds reject malformed text and permit the 8192-byte text exception', () => {
  const { client, sent } = session();
  for (const text of [' ', 'a'.repeat(1001), '\ud800']) {
    assert.equal(client.submit(text), false);
  }
  assert.equal(sent.length, 0);
  assert.equal(client.submit('🪄'.repeat(1000)), true);
  assert.ok(new TextEncoder().encode(JSON.stringify(sent[0])).length > 2048);
});

test('only matching accepted turn produces a bounded reply and duplicate ACK cannot extend deadline', () => {
  const { client, state, ack, result, sent } = session();
  client.submit('hello');
  client.receive(result());
  assert.equal(client.snapshot().reply, null);
  client.receive(ack());
  client.update({ ...state, eventSequence: 7, conversation: { ...state.conversation, activeTurnId: sent[0].clientTurnId } });
  const timer = [...timers.keys()][0];
  client.receive(ack({ eventSequence: 8, duplicate: true }));
  assert.equal([...timers.keys()][0], timer);
  for (const message of [result({ bootId: randomUUID(), eventSequence: 9 }),
    result({ clientTurnId: randomUUID(), eventSequence: 9 }),
    result({ generation: 1, eventSequence: 9 }), result({ reply: 'a'.repeat(501), eventSequence: 9 })]) {
    client.receive(message);
    assert.equal(client.snapshot().reply, null);
  }
  client.receive(result({ eventSequence: 9 }));
  assert.equal(client.snapshot().reply, '<img src=x onerror=alert(1)>');
  assert.equal(client.snapshot().simulated, false);
  assert.equal(client.snapshot().canCancel, false);
  assert.equal(client.snapshot().pending, false);
  assert.equal(client.cancel(), false);
  assert.equal(timers.size, 0);
});

test('cancel discards queued results before ACK and waits for the authoritative new generation', () => {
  const { client, state, sent, ack, result } = session();
  client.submit('hello');
  client.receive(ack());
  assert.equal(client.cancel(), true);
  client.receive(result());
  assert.equal(client.snapshot().reply, null);
  assert.equal(client.snapshot().canSubmit, false);
  client.receive(ack({ requestMessageId: sent[1].messageId, clientTurnId: null, generation: 3, eventSequence: 9 }));
  assert.equal(client.snapshot().canSubmit, false);
  client.update({ ...state, eventSequence: 10, conversation: { ...state.conversation, generation: 3 } });
  assert.equal(client.snapshot().canSubmit, true);
  client.receive(result({ eventSequence: 11 }));
  assert.equal(client.snapshot().reply, null);
});

test('Stop, lease loss, revision change and reboot invalidate all pending/replied content', () => {
  for (const change of [
    (state) => ({ ...state, controlLeaseId: null }),
    (state) => ({ ...state, revision: state.revision + 1 }),
    (state) => ({ ...state, bootId: randomUUID() }),
    (state) => ({ ...state, conversation: { ...state.conversation, generation: 3 } }),
  ]) {
    const { client, state, ack, result } = session();
    client.submit('hello'); client.receive(ack());
    client.update(change(state)); client.receive(result({ eventSequence: 12 }));
    assert.equal(client.snapshot().reply, null);
    assert.equal(timers.size, 0);
  }
  const { client, ack, result } = session();
  client.submit('hello'); client.receive(ack()); client.receive(result());
  client.invalidate();
  assert.equal(client.snapshot().reply, null);
  assert.equal(client.snapshot().canSubmit, false);
});

test('missing ACK/result disconnects without replay or deferred text', () => {
  for (const accepted of [false, true]) {
    const { client, sent, ack, disconnects } = session();
    client.submit('hello');
    if (accepted) { client.receive(ack()); }
    const [id, timer] = [...timers.entries()][0];
    assert.equal(timer.milliseconds, accepted ? 45000 : 3000);
    timers.delete(id); timer.callback();
    assert.equal(disconnects(), 1);
    assert.equal(sent.length, 1);
    assert.equal(client.snapshot().reply, null);
    assert.equal(client.snapshot().canSubmit, false);
  }
});
