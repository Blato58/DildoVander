import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

import { validateControlMessage } from './control-validator.mjs';

const path = process.argv[2];
assert.ok(path, 'Pass the C++ output test JSONL path');
const lines = (await readFile(path, 'utf8')).trim().split(/\r?\n/);
const messages = lines.map((line) => validateControlMessage(Buffer.from(line)));
const snapshots = messages.flatMap((message) => message.type === 'welcome'
  ? [message.snapshot] : message.type === 'state.snapshot' ? [message] : []);

assert.ok(snapshots.some((state) => state.calibrationRevision === 0 &&
  state.readyMask === 0 && state.phase === 'recoveryRequired'));
assert.ok(snapshots.some((state) => state.faults.includes('journal_failed') &&
  state.readyMask === 0 && state.phase === 'fault'));
for (const phase of ['searching', 'highlighting', 'vibrating', 'releasing', 'presenting', 'ready', 'stopped']) {
  assert.ok(snapshots.some((state) => state.phase === phase), phase);
}
assert.ok(snapshots.some((state) => state.conversation.activeTurnId));
assert.ok(messages.some((message) => message.type === 'conversation.ack' && message.clientTurnId === null));
const replies = messages.filter((message) => message.type === 'conversation.result');
assert.equal(replies.length, 2);
assert.ok(replies.every((message) => message.simulated === false));
assert.equal(replies[0].reply, 'Czech č reply: "quoted"\\path\n');
assert.equal(replies[1].reply, '\n'.repeat(500));
const results = messages.filter((message) => message.type === 'selection.result');
assert.deepEqual(results.map((result) => result.status).sort(), ['completed', 'outcome_unknown']);
for (const result of results) {
  assert.equal(result.physicalVerification, 'unavailable');
  assert.equal(result.requiresReload, true);
}
const voiceAcks = messages.filter(message => message.type === 'voice.ack');
assert.ok(messages.some(message => message.type === 'dance.ack' && message.accepted && message.dance.track === 'song.wav'));
assert.ok(snapshots.some(snapshot => snapshot.phase === 'dancing' && snapshot.dance.bpm === 120));
assert.equal(voiceAcks.length, 2);
assert.ok(voiceAcks.some(message => message.accepted && message.voice.voice === 'cedar'));
assert.ok(voiceAcks.some(message => !message.accepted && message.voice.error === 'cost_limit'));
assert.ok(snapshots.some(snapshot => snapshot.voice?.phase === 'listening'));
for (const snapshot of snapshots) {
  assert.equal(snapshot.microphoneState, snapshot.voice?.available
    ? snapshot.voice.phase === 'listening' ? 'recording' : snapshot.voice.phase === 'speaking' ? 'playing' : 'idle'
    : 'unavailable');
  if (snapshot.lastResult) {
    assert.ok(snapshot.lastResult.eventSequence <= snapshot.eventSequence);
    assert.ok(snapshot.lastResult.revision <= snapshot.revision);
  }
}
console.log(`Validated ${messages.length} actual firmware messages against shared v1 schema`);
