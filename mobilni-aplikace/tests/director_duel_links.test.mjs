import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/features/director/DuelLinks.ts', import.meta.url), 'utf8');
const code = stripTypeScriptTypes(source, { mode: 'transform' });
const { duelPageUrl, DuelGrantMemory } = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`);
const token = 'a'.repeat(43);

test('read-only result has no grant and side authority stays only in the URL fragment', () => {
  const result = new URL(duelPageUrl('https://keepsake.example', 'duel-one'));
  assert.equal(result.pathname, '/keepsake/duel.html');
  assert.equal(result.searchParams.get('duel'), 'duel-one');
  assert.equal(result.hash, '');
  for (const side of ['left', 'right']) {
    const play = new URL(duelPageUrl('https://keepsake.example', 'duel-one', { side, token }));
    assert.equal(play.searchParams.has('grant'), false);
    assert.equal(new URLSearchParams(play.hash.slice(1)).get('grant'), token);
    assert.equal(new URLSearchParams(play.hash.slice(1)).get('side'), side);
  }
});

test('link creation refuses missing/unsafe origins and malformed side authority', () => {
  for (const origin of [undefined, '', 'javascript:alert(1)', 'http://remote.example', 'https://u:p@example.com',
    'https://example.com/?token=other', 'https://example.com/#other', 'https://example.com/private']) {
    assert.equal(duelPageUrl(origin, 'duel-one', { side: 'left', token }), null);
  }
  assert.equal(duelPageUrl('https://example.com', '../foreign', { side: 'left', token }), null);
  assert.equal(duelPageUrl('https://example.com', 'duel-one', { side: 'other', token }), null);
  assert.equal(duelPageUrl('https://example.com', 'duel-one', { side: 'left', token: '<script>' }), null);
  assert.ok(duelPageUrl('http://127.0.0.1:5088', 'duel-one'));
});

test('memory binds both sides to one scope, revokes independently and drops expiry', context => {
  const now = Date.now(); context.mock.method(Date, 'now', () => now);
  const memory = new DuelGrantMemory();
  const grant = { token, expiresAt: new Date(now + 10000).toISOString() };
  memory.bind('evening-one', 'duel-one');
  memory.remember('left', grant); memory.remember('right', { ...grant, token: 'b'.repeat(43) });
  const copy = memory.get('left'); copy.token = 'changed';
  assert.equal(memory.get('left').token, token);
  memory.remove('left'); assert.equal(memory.get('left'), undefined); assert.ok(memory.get('right'));
  memory.bind('evening-two', 'duel-one'); assert.equal(memory.get('right'), undefined);
  memory.remember('left', grant); memory.bind('evening-two', 'duel-two'); assert.equal(memory.get('left'), undefined);
  memory.remember('left', grant); context.mock.method(Date, 'now', () => now + 20000);
  assert.equal(memory.get('left'), undefined);
  memory.clear(); assert.throws(() => memory.remember('right', grant));
});
