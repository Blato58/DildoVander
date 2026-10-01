import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { stripTypeScriptTypes } from 'node:module';

const source = await readFile(new URL('../src/features/debug/ServerLogs.ts', import.meta.url), 'utf8');
const { ServerLogBuffer } = await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`);
const entry = id => ({ id, timestamp: '2026-10-01T12:00:00.000Z', level: 'info', eventCode: 'http_request' });
const page = (ids, changes = {}) => ({ storeId: 'store-a', oldestCursor: 1, latestCursor: 500,
  nextCursor: ids.at(-1) ?? 500, hasMore: false, truncated: false, droppedWrites: 0, entries: ids.map(entry), ...changes });

test('server logs merge overlapping pages and bound history without replaying cursor', () => {
  const logs = new ServerLogBuffer();
  logs.accept(page(Array.from({ length: 200 }, (_, i) => i + 1), { nextCursor: 200, hasMore: true }));
  assert.equal(logs.cursor, 200);
  logs.accept(page([199, 200, 201, 202]));
  assert.equal(logs.entries.length, 200);
  assert.equal(logs.entries[0].id, 3);
  assert.equal(logs.cursor, 202);
  logs.accept(page([], { nextCursor: 300 }));
  assert.equal(logs.cursor, 300);
  assert.equal(logs.entries.length, 200);
});

test('server logs handle retention gaps, recreated stores and filter reset', () => {
  const logs = new ServerLogBuffer();
  logs.accept(page([1, 2]));
  logs.accept(page([100, 101], { oldestCursor: 100, truncated: true, droppedWrites: 2 }));
  assert.deepEqual(logs.entries.map(e => e.id), [100, 101]);
  assert.equal(logs.gap, true);
  assert.equal(logs.droppedWrites, 2);
  assert.equal(logs.accept(page([200], { storeId: 'store-b' })), false);
  assert.equal(logs.cursor, undefined);
  logs.accept(page([200], { storeId: 'store-b' }));
  logs.clear();
  assert.equal(logs.storeId, undefined);
  assert.equal(logs.gap, false);
  assert.equal(logs.entries.length, 0);
});
