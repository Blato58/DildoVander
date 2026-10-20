import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { stripTypeScriptTypes } from 'node:module';

const source = await readFile(new URL('../src/features/debug/DebugJournal.ts', import.meta.url), 'utf8');
const code = stripTypeScriptTypes(source, { mode: 'transform' });
const { DebugJournal } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const snapshot = {
  source: 'native-device', phase: 'ready', controlMode: 'observer', readyMask: 63,
  busy: false, revision: 1, status: 'secret-status', bearer: 'secret-token',
  conversation: { pending: false, reply: 'secret-reply', status: 'secret-text' },
  diagnostics: { bootId: 'secret-boot', eventSequence: 1, deviceUptimeMs: 1234, receivedAtMs: 5, roundTripMs: 5, faultCount: 0 },
};

test('journal projects safe fields, deduplicates heartbeats and returns owned copies', () => {
  const journal = new DebugJournal();
  journal.observe(snapshot);
  journal.observe({ ...snapshot, diagnostics: { ...snapshot.diagnostics, receivedAtMs: 100 } });
  assert.equal(journal.read().length, 1);
  const exported = journal.export();
  assert(!exported.includes('secret'));
  const copy = journal.read();
  copy[0].values.readyMask = 0;
  assert.equal(journal.read()[0].values.readyMask, 63);
  journal.observe({ ...snapshot, phase: 'disconnected', diagnostics: undefined });
  assert.equal(journal.read().length, 2);
});

test('journal bounds retained history, filters and resets deduplication on clear', () => {
  const journal = new DebugJournal();
  for (let i = 0; i < 220; i++) { journal.observe({ ...snapshot, revision: i }); }
  assert.equal(journal.read().length, 200);
  assert.equal(journal.read()[0].values.revision, 20);
  journal.record('voice_started');
  assert.equal(journal.read('voice').length, 1);
  assert.equal(journal.read('device').length, 199);
  journal.clear();
  assert.equal(journal.read().length, 0);
  journal.observe(snapshot);
  assert.equal(journal.read().length, 1);
});
