import test from 'node:test';
import assert from 'node:assert/strict';
import { loadDeviceModule } from './load-test-module.mjs';
const { LocalVoiceClient } = await loadDeviceModule('LocalVoiceClient');
globalThis.window = { setTimeout, clearTimeout };
const settings = { available: true, mode: 'off', voice: 'marin', phase: 'idle', error: null };
const context = { deviceId: 'kufr-001', bootId: 'boot-1', revision: 37, controlMode: 'standalone', phase: 'ready', activeCommandId: null, voice: settings };

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

test('manual/recovery/unavailable and cost limit stay visible without enabling start', () => {
  const client = new LocalVoiceClient(() => true, () => {}, () => {});
  for (const state of [{ ...context, controlMode: 'manual' }, { ...context, phase: 'stopped' }, { ...context, voice: undefined }]) {
    client.update({ ...state, voice: state.voice && { ...settings, mode: 'realtime' } });
    assert.equal(client.state().canStart, false);
  }
  client.update({ ...context, voice: { ...settings, mode: 'realtime', phase: 'failed', error: 'cost_limit' } });
  assert.match(client.state().status, /limit nákladů/);
  client.update({ ...context, voice: { ...settings, available: false } });
  assert.equal(client.configure('realtime', 'cedar'), false);
  assert.equal(client.configure('off', 'onyx'), false);
  client.invalidate();
});
