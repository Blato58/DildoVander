import test from 'node:test';
import assert from 'node:assert/strict';
import { loadDeviceModule } from './load-test-module.mjs';
const { LocalVoiceClient } = await loadDeviceModule('LocalVoiceClient');
globalThis.window = { setTimeout, clearTimeout };
const settings = { available: true, mode: 'off', voice: 'marin', phase: 'idle', error: null };
const context = { deviceId: 'kufr-001', bootId: 'boot-1', revision: 37, controlMode: 'standalone', phase: 'ready', activeCommandId: null, voice: settings };

test('hardware test tickets require an advertised capability and never fall back to ordinary start', () => {
  const sent = [];
  const client = new LocalVoiceClient(message => { sent.push(message); return true; }, () => {}, () => {});
  const ticket = 'A'.repeat(43);
  const ready = { ...context, voice: { ...settings, mode: 'realtime' } };
  client.update(ready);
  assert.equal(client.start(false, ticket), false);
  client.update({ ...ready, capabilities: ['voiceTestContext'] });
  for (const invalid of ['', 'A'.repeat(42), 'A'.repeat(44), '<'.repeat(43)]) {
    assert.equal(client.start(false, invalid), false);
  }
  assert.equal(sent.length, 0);
  assert.equal(client.start(false, ticket), true);
  assert.equal(sent[0].testContext, ticket);
  assert.equal(sent[0].expectedRevision, context.revision);
  assert.equal(sent[0].imageConsent, undefined);
  client.update({ ...ready, bootId: 'boot-2' });
  assert.equal(client.start(false, ticket), false);
  assert.equal(sent.length, 1);
  client.invalidate();
});

test('voice requires device confirmation, ignores wrong boot/ACK and prevents replay', () => {
  const sent = [];
  const client = new LocalVoiceClient(m => { sent.push(m); return true; }, () => {}, () => assert.fail('unexpected close'));
  assert.equal(client.start(), false);
  client.update(context);
  assert.equal(client.configure('realtime', 'cedar'), true);
  assert.equal(client.state().mode, 'off');
  assert.equal(client.state().pending, true);
  const ack = { type: 'voice.ack', bootId: context.bootId, requestMessageId: sent[0].messageId, accepted: true, voice: { ...settings, mode: 'realtime', voice: 'cedar' } };
  client.receive({ ...ack, bootId: 'old-boot' });
  assert.equal(client.state().pending, true);
  client.receive({ ...ack, requestMessageId: 'old-request' });
  assert.equal(client.state().mode, 'off');
  client.receive(ack);
  assert.equal(client.state().voice, 'cedar');
  assert.equal(client.start(), true);
  assert.equal(sent[1].expectedRevision, 37);
  assert.equal(client.start(), false);
  assert.equal(client.stop(), true); // Stop supersedes start without waiting.
  client.receive({ ...ack, requestMessageId: sent[1].messageId });
  assert.equal(client.state().pending, true);
  client.invalidate();
  client.update(context);
  assert.equal(sent.length, 3); // No retry on reconnect.
  client.invalidate();
});

test('scoped Lab Stop cannot stop a later production or different test conversation', () => {
  const sent = [];
  const client = new LocalVoiceClient(message => { sent.push(message); return true; }, () => {}, () => {});
  const fingerprint = 'a'.repeat(64);
  const speaking = { ...context, capabilities: ['voiceTestContext'], voice: { ...settings, mode: 'realtime', phase: 'speaking' } };
  client.update(speaking);
  assert.equal(client.stop(fingerprint), false);
  client.update({ ...speaking, voice: { ...speaking.voice, testContextId: 'b'.repeat(64) } });
  assert.equal(client.stop(fingerprint), false);
  assert.equal(sent.length, 0);
  client.update({ ...speaking, voice: { ...speaking.voice, testContextId: fingerprint } });
  assert.equal(client.stop(fingerprint), true);
  assert.equal(sent[0].type, 'voice.stop');
  assert.equal(sent[0].testContextId, fingerprint);
  client.invalidate();
});

test('manual/recovery/unavailable and cost limit stay visible without enabling start', () => {
  const client = new LocalVoiceClient(() => true, () => {}, () => {});
  for (const state of [{ ...context, controlMode: 'manual' }, { ...context, phase: 'stopped' }, { ...context, voice: undefined }]) {
    client.update({ ...state, voice: state.voice && { ...settings, mode: 'realtime' } });
    assert.equal(client.state().canStart, false);
  }
  client.update({ ...context, voice: { ...settings, mode: 'realtime', phase: 'failed', error: 'cost_limit' } });
  assert.match(client.state().status, /OpenAI účet/);
  client.update({ ...context, voice: { ...settings, available: false } });
  assert.equal(client.configure('realtime', 'cedar'), false);
  assert.equal(client.configure('off', 'onyx'), false);
  client.invalidate();
});

test('GPT-Live requires firmware acknowledgment and cannot switch an active session', () => {
  const sent = [];
  const client = new LocalVoiceClient(m => { sent.push(m); return true; }, () => {}, () => {});
  client.update(context);
  assert.equal(client.configure('gpt-live', 'marin'), true);
  assert.equal(sent[0].mode, 'gpt-live');
  assert.equal(client.state().canStart, false);
  client.receive({ type: 'voice.ack', bootId: context.bootId, requestMessageId: sent[0].messageId,
    accepted: true, voice: { ...settings, mode: 'gpt-live' } });
  assert.equal(client.state().canStart, true);
  client.update({ ...context, voice: { ...settings, mode: 'gpt-live', phase: 'listening' } });
  assert.equal(client.configure('realtime', 'cedar'), false);
  assert.equal(client.state().canStop, true);
  client.invalidate();
});


test('camera consent is explicit, single-start and absent for old devices', () => {
  const sent = [];
  const client = new LocalVoiceClient(m => { sent.push(m); return true; }, () => {}, () => {});
  client.update({ ...context, voice: { ...settings, mode: 'realtime' } });
  assert.equal(client.start(true), false); // No camera capability on an old snapshot.
  assert.equal(client.start(), true);
  assert.equal(Object.hasOwn(sent[0], 'imageConsent'), false);
  client.invalidate();
  client.update({ ...context, voice: { ...settings, mode: 'realtime', cameraAvailable: true } });
  assert.equal(client.start(true), true);
  assert.equal(sent[1].imageConsent, true);
  client.invalidate();
  client.update({ ...context, voice: { ...settings, mode: 'realtime', cameraAvailable: true } });
  assert.equal(client.start(), true);
  assert.equal(Object.hasOwn(sent[2], 'imageConsent'), false);
  client.invalidate();
});

test('photo phases stay active and photo warning keeps the voice dialogue usable', () => {
  const client = new LocalVoiceClient(() => true, () => {}, () => {});
  for (const phase of ['photo_capture', 'photo_upload', 'photo_wait']) {
    client.update({ ...context, voice: { ...settings, mode: 'realtime', phase, cameraAvailable: true } });
    assert.equal(client.state().canStart, false);
    assert.equal(client.state().canStop, true);
    assert.equal(client.state().canConfigure, false);
    assert(client.state().status.length > 0);
  }
  client.update({ ...context, voice: { ...settings, mode: 'realtime', phase: 'listening', photoWarning: 'photo_failed' } });
  assert.match(client.state().status, /bez ní/);
  assert.equal(client.state().canStop, true);
  assert.equal(client.state().error, null);
  client.invalidate();
});
