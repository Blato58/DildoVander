import assert from 'node:assert/strict';
import test from 'node:test';
import { secureUuid } from '../src/application/identifiers.mjs';

test('UUID fallback obtains cryptographic bytes and produces a version 4 UUID', () => {
  let draws = 0;
  const source = { getRandomValues(bytes) { draws++; assert.equal(bytes.length, 16); return crypto.getRandomValues(bytes); } };
  const first = secureUuid(source), second = secureUuid(source);
  assert.match(first, /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/);
  assert.notEqual(first, second);
  assert.equal(draws, 2);
});

test('UUIDs use the native method when present and fail without a secure source', () => {
  const expected = '12345678-1234-4234-8234-123456789abc';
  assert.equal(secureUuid({ randomUUID() { return expected; } }), expected);
  assert.throws(() => secureUuid({}), /Cryptographic randomness is unavailable/);
});
