import test from 'node:test';
import assert from 'node:assert/strict';
import { loadDeviceModule } from './load-test-module.mjs';
const { LocalDanceClient } = await loadDeviceModule('LocalDanceClient');
globalThis.window = { setTimeout, clearTimeout };
const settings = { available: true, phase: 'idle', track: null, positionMs: 0, durationMs: 0, bpm: 0, error: null };
const context = { deviceId: 'kufr-001', bootId: 'boot-1', revision: 37, controlMode: 'standalone',
  phase: 'ready', activeCommandId: null, dance: settings };

test('dance starts once, requires matched ACK and supports stop while start is pending', () => {
  const sent = [];
  const client = new LocalDanceClient(m => { sent.push(m); return true; }, () => {}, () => assert.fail('unexpected close'));
  assert.equal(client.start(), false);
  client.update(context);
  assert.equal(client.start(), true);
  assert.equal(sent[0].expectedRevision, 37);
  assert.equal(client.start(), false);
  const playing = { ...settings, phase: 'playing', track: 'song.wav', bpm: 120, durationMs: 10000 };
  const ack = { type: 'dance.ack', bootId: context.bootId, requestMessageId: sent[0].messageId, accepted: true, dance: playing };
  client.receive({ ...ack, bootId: 'old-boot' });
  client.receive({ ...ack, requestMessageId: 'old-request' });
  assert.equal(client.state().pending, true);
  assert.equal(client.stop(), true);
  client.receive(ack); // Late start ACK cannot overwrite the pending stop.
  assert.equal(client.state().pending, true);
  client.receive({ ...ack, requestMessageId: sent[1].messageId, dance: { ...playing, phase: 'stopping' } });
  assert.equal(client.state().phase, 'stopping');
  client.invalidate();
  client.update(context);
  assert.equal(sent.length, 2); // Reconnect never replays a start.
  client.invalidate();
});

test('manual, recovery, conversation, voice and absent SD prevent start', () => {
  const client = new LocalDanceClient(() => true, () => {}, () => {});
  for (const blocked of [
    { controlMode: 'manual' }, { controlMode: 'recovery' }, { phase: 'stopped' },
    { activeCommandId: 'command' }, { conversation: { activeTurnId: 'turn' } },
    { voice: { phase: 'listening' } }, { dance: { ...settings, available: false } },
    { dance: undefined }, { dance: { ...settings, phase: 'playing' } },
  ]) {
    client.update({ ...context, ...blocked });
    assert.equal(client.start(), false);
  }
  client.update({ ...context, dance: { ...settings, phase: 'failed', error: 'dance_manifest_invalid' } });
  assert.match(client.state().status, /Seznam skladeb/);
  client.invalidate();
});

test('lost ACK closes without retry; new boot discards pending request', () => {
  let expire;
  let closed = 0;
  const sent = [];
  globalThis.window = { setTimeout: callback => { expire = callback; return 1; }, clearTimeout: () => {} };
  const client = new LocalDanceClient(m => { sent.push(m); return true; }, () => {}, () => ++closed);
  client.update(context);
  client.start();
  expire();
  assert.equal(closed, 1);
  assert.equal(sent.length, 1);
  client.update({ ...context, bootId: 'boot-2' });
  assert.equal(client.state().pending, false);
  assert.equal(sent.length, 1);
  client.invalidate();
});
